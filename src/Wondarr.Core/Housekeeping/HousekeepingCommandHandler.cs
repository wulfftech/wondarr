using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Jobs;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Housekeeping;

/// <summary>
/// The daily <c>Housekeeping</c> task (ARCHITECTURE §5.5): keeps a long-running install small.
/// It prunes finished commands, old search runs with their candidates, expired blocklist rows and the
/// recycle bin, then checkpoints the WAL and vacuums when a good part of the file is free pages.
/// History, queue items and song files are never touched. Every step runs even when an earlier one
/// failed; a failed step fails the command after the others have run.
/// </summary>
public sealed partial class HousekeepingCommandHandler : ICommandHandler
{
    /// <summary>The name the <c>Housekeeping</c> scheduled task queues.</summary>
    public const string CommandName = "Housekeeping";

    /// <summary>How long a finished command is kept.</summary>
    public static readonly TimeSpan CommandRetention = TimeSpan.FromDays(7);

    /// <summary>How long a search run, and its candidates, is kept.</summary>
    public static readonly TimeSpan SearchRunRetention = TimeSpan.FromDays(30);

    /// <summary>The most search runs one delete statement removes, so no run holds a long write lock.</summary>
    public const int SearchRunBatchSize = 500;

    /// <summary>The most command rows one delete statement removes.</summary>
    public const int CommandBatchSize = 1000;

    /// <summary>The fewest free pages before a <c>VACUUM</c> is worth its cost.</summary>
    public const long VacuumMinimumFreePages = 1000;

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    private static readonly CommandStatus[] FinishedStatuses =
    [
        CommandStatus.Completed,
        CommandStatus.Failed,
        CommandStatus.Aborted,
        CommandStatus.Cancelled,
        CommandStatus.Orphaned,
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HousekeepingCommandHandler> _logger;
    private readonly Func<long, long, bool> _shouldVacuum;

    /// <summary>Initialises a new instance of the <see cref="HousekeepingCommandHandler"/> class.</summary>
    /// <param name="scopes">
    /// Builds the scope the run's services are resolved in: the handler is resolved whenever the
    /// queue asks which handlers exist, so the database context is only built once a run starts.
    /// </param>
    /// <param name="timeProvider">The clock the retentions are measured against.</param>
    /// <param name="logger">The logger.</param>
    public HousekeepingCommandHandler(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        ILogger<HousekeepingCommandHandler> logger)
        : this(scopes, timeProvider, logger, ShouldVacuum)
    {
    }

    /// <summary>Initialises a new instance with its own vacuum threshold, so a test can use a small file.</summary>
    /// <param name="scopes">The scope factory.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="shouldVacuum">Decides from the page count and the free page count whether to vacuum.</param>
    internal HousekeepingCommandHandler(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        ILogger<HousekeepingCommandHandler> logger,
        Func<long, long, bool> shouldVacuum)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(shouldVacuum);

        _scopes = scopes;
        _timeProvider = timeProvider;
        _logger = logger;
        _shouldVacuum = shouldVacuum;
    }

    /// <inheritdoc />
    public string Name => CommandName;

    /// <summary>Whether a database with these page counts is worth a <c>VACUUM</c>.</summary>
    /// <param name="pageCount">The pages in the file.</param>
    /// <param name="freePages">The pages on the free list.</param>
    /// <returns><see langword="true"/> when more than a quarter of the file, and more than 1 000 pages, is free.</returns>
    public static bool ShouldVacuum(long pageCount, long freePages) =>
        freePages > VacuumMinimumFreePages && freePages * 4 > pageCount;

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var parts = new List<string>();
        var failures = new List<string>();

        await Step("commands", async () => $"commands {await PruneCommandsAsync(now, cancellationToken).ConfigureAwait(false)}")
            .ConfigureAwait(false);

        await Step(
            "search runs",
            async () =>
            {
                var (runs, candidates) = await PruneSearchRunsAsync(now, cancellationToken).ConfigureAwait(false);

                return $"search runs {runs} (candidates {candidates})";
            }).ConfigureAwait(false);

        await Step("blocklist", async () => $"blocklist {await PruneBlocklistAsync(now, cancellationToken).ConfigureAwait(false)}")
            .ConfigureAwait(false);

        await Step(
            "recycle bin",
            async () =>
            {
                using var scope = _scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<IRecycleBin>()
                    .CleanupAsync(cancellationToken)
                    .ConfigureAwait(false);

                return $"recycle bin {removed} files";
            }).ConfigureAwait(false);

        await Step("vacuum", () => VacuumAsync(cancellationToken)).ConfigureAwait(false);

        var summary = string.Join(", ", parts);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Housekeeping step(s) failed: {string.Join("; ", failures)}. Done: {(summary.Length == 0 ? "nothing" : summary)}");
        }

        return summary;

        async Task Step(string step, Func<Task<string>> run)
        {
            try
            {
                parts.Add(await run().ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogStepFailed(step, exception);
                failures.Add($"{step}: {exception.Message}");
            }
        }
    }

    private async Task<int> PruneCommandsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - CommandRetention;
        var total = 0;

        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        // The Tasks page reads each task's last run from the newest finished row of that name, and
        // Backup can run once a year, so the newest finished row per name is never pruned.
        while (true)
        {
            var ids = await database.Commands
                .Where(command => FinishedStatuses.Contains(command.Status)
                    && (command.EndedAt ?? command.QueuedAt) < cutoff
                    && database.Commands.Any(newer => newer.Name == command.Name
                        && newer.Id > command.Id
                        && FinishedStatuses.Contains(newer.Status))
                    && (command.StartedAt == null
                        || command.EndedAt == null
                        || database.Commands.Any(newer => newer.Name == command.Name
                            && newer.Id > command.Id
                            && newer.StartedAt != null
                            && newer.EndedAt != null
                            && FinishedStatuses.Contains(newer.Status))))
                .OrderBy(command => command.Id)
                .Select(command => command.Id)
                .Take(CommandBatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                return total;
            }

            total += await database.Commands
                .Where(command => ids.Contains(command.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<(int Runs, int Candidates)> PruneSearchRunsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - SearchRunRetention;
        var runs = 0;
        var candidates = 0;

        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        while (true)
        {
            // The candidate count is for the message only; the delete below re-checks every guard in
            // its own statement, so a queue item or an import that lands in between is still honoured.
            var batchCandidates = await database.Database
                .SqlQueryRaw<int>(
                    CountCandidatesSql,
                    cutoff)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            // One statement: the guards and the delete cannot be split by another writer. The
            // candidates go with their run through the foreign key's cascade.
            var deleted = await database.Database
                .ExecuteSqlRawAsync(
                    DeleteRunsSql,
                    [cutoff],
                    cancellationToken)
                .ConfigureAwait(false);

            if (deleted == 0)
            {
                return (runs, candidates);
            }

            runs += deleted;
            candidates += batchCandidates;
        }
    }

    private static string CountCandidatesSql =>
        "SELECT count(*) AS \"Value\" FROM candidate WHERE search_run_id IN (" + EligibleRunsSql + ")";

    private static string DeleteRunsSql => "DELETE FROM search_run WHERE id IN (" + EligibleRunsSql + ")";

    /// <summary>
    /// The ids of at most <see cref="SearchRunBatchSize"/> runs that may go. queue_item.search_run_id
    /// cascades and queue_item.candidate_id restricts, so a run with a queue item of its own, or one of
    /// whose candidates a queue item points at, stays. So does a run a held file's <c>source_ref</c>
    /// names (<c>searchRunId</c>), or one of whose candidates it names (<c>candidateId</c>): the
    /// identity rule reads that candidate back. Those are the property names ImportService writes. A
    /// <c>source_ref</c> that is not valid JSON protects nothing, on purpose — the identity rule
    /// ignores it too.
    /// </summary>
    private static readonly string EligibleRunsSql = $$"""
        SELECT r.id FROM search_run r
        WHERE r.started_at < {0}
          AND NOT EXISTS (SELECT 1 FROM queue_item q WHERE q.search_run_id = r.id)
          AND NOT EXISTS (
              SELECT 1 FROM queue_item q JOIN candidate c ON c.id = q.candidate_id
              WHERE c.search_run_id = r.id)
          AND NOT EXISTS (
              SELECT 1 FROM song_file f
              WHERE f.source_ref IS NOT NULL
                AND (CASE WHEN json_valid(f.source_ref) THEN json_extract(f.source_ref, '$.searchRunId') END) = r.id)
          AND NOT EXISTS (
              SELECT 1 FROM song_file f JOIN candidate c ON c.search_run_id = r.id
              WHERE f.source_ref IS NOT NULL
                AND (CASE WHEN json_valid(f.source_ref) THEN json_extract(f.source_ref, '$.candidateId') END) = c.id)
        ORDER BY r.id
        LIMIT {{SearchRunBatchSize}}
        """;

    private async Task<int> PruneBlocklistAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await database.Blocklist
            .Where(item => item.ExpiresAt != null && item.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> VacuumAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var connection = database.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // PRAGMA wal_checkpoint answers with one row: busy, log frames, checkpointed frames.
            bool busy;
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                busy = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    && Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) == 1;
            }

            var state = busy ? "checkpoint busy" : "checkpointed";

            var pages = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
            var free = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);

            if (!_shouldVacuum(pages, free))
            {
                return state;
            }

            // VACUUM rewrites the file under an exclusive lock: not while another command may be
            // writing to it.
            var othersRunning = await database.Commands
                .AnyAsync(
                    command => command.Status == CommandStatus.Started && command.Name != CommandName,
                    cancellationToken)
                .ConfigureAwait(false);

            if (othersRunning)
            {
                LogVacuumSkipped("other commands are running");

                return $"{state}, vacuum skipped (commands running)";
            }

            try
            {
                await ExecuteAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);

                // In WAL mode the rewritten file lands in the WAL; fold it back so the file shrinks now.
                await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                LogVacuumSkipped(exception.Message);

                return $"{state}, vacuum skipped (database busy)";
            }

            return "vacuumed";
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Housekeeping step '{Step}' failed")]
    private partial void LogStepFailed(string step, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Housekeeping skipped the vacuum, the database is busy: {Reason}")]
    private partial void LogVacuumSkipped(string reason);
}
