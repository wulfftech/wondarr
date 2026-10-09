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
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _timeProvider = timeProvider;
        _logger = logger;
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

        while (true)
        {
            var ids = await database.Commands
                .Where(command => FinishedStatuses.Contains(command.Status)
                    && (command.EndedAt ?? command.QueuedAt) < cutoff)
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

        // A held file's source_ref names the candidate (and run) that produced it; the identity rule
        // reads that candidate back. There is one file per song, so this set is as small as the library.
        var heldRuns = new HashSet<long>();
        var heldCandidates = new HashSet<long>();
        var references = await database.SongFiles
            .AsNoTracking()
            .Where(file => file.SourceRef != null)
            .Select(file => file.SourceRef!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var reference in references)
        {
            ReadSourceRef(reference, heldRuns, heldCandidates);
        }

        var heldCandidateRuns = heldCandidates.Count == 0
            ? []
            : await database.Candidates
                .AsNoTracking()
                .Where(candidate => heldCandidates.Contains(candidate.Id))
                .Select(candidate => candidate.SearchRunId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        heldRuns.UnionWith(heldCandidateRuns);

        while (true)
        {
            // queue_item.search_run_id cascades and queue_item.candidate_id restricts, so a run with
            // a queue item of its own, or one of whose candidates a queue item points at, stays.
            var ids = await database.SearchRuns
                .Where(run => run.StartedAt < cutoff
                    && !database.QueueItems.Any(item => item.SearchRunId == run.Id || item.Candidate.SearchRunId == run.Id)
                    && !heldRuns.Contains(run.Id))
                .OrderBy(run => run.Id)
                .Select(run => run.Id)
                .Take(SearchRunBatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                return (runs, candidates);
            }

            candidates += await database.Candidates
                .CountAsync(candidate => ids.Contains(candidate.SearchRunId), cancellationToken)
                .ConfigureAwait(false);

            // The candidates go with their run through the foreign key's cascade.
            runs += await database.SearchRuns
                .Where(run => ids.Contains(run.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void ReadSourceRef(string sourceRef, HashSet<long> runs, HashSet<long> candidates)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceRef);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var id))
                {
                    continue;
                }

                if (string.Equals(property.Name, "candidateId", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(id);
                }
                else if (string.Equals(property.Name, "searchRunId", StringComparison.OrdinalIgnoreCase))
                {
                    runs.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // A source_ref that is not ours names nothing to protect.
        }
    }

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
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);

            var pages = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
            var free = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);

            if (!ShouldVacuum(pages, free))
            {
                return "checkpointed";
            }

            try
            {
                await ExecuteAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                LogVacuumSkipped(exception.Message);

                return "checkpointed, vacuum skipped (database busy)";
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
