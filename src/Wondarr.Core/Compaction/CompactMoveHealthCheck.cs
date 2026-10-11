using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Persistence;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Compaction;

/// <summary>
/// Warns about every library that still has unfinished <c>compact_move</c> rows: files parked in the
/// hidden <c>.wondarr-compact</c> folder, which Plex no longer shows and which imports for those songs
/// wait for. It clears itself when a Compact run finishes the rows.
/// </summary>
public sealed class CompactMoveHealthCheck : IHealthCheck
{
    private readonly WondarrDbContext _database;

    /// <summary>Initialises a new instance of the <see cref="CompactMoveHealthCheck"/> class.</summary>
    /// <param name="database">The Wondarr database, which holds the moves and the libraries.</param>
    public CompactMoveHealthCheck(WondarrDbContext database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    /// <inheritdoc />
    public string Name => "Compaction";

    /// <inheritdoc />
    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var unfinished = await _database.CompactMoves
            .AsNoTracking()
            .Where(CompactMoveRules.IsUnfinished)
            .GroupBy(row => row.LibraryId)
            .Select(group => new { LibraryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var strandedRows = await _database.CompactMoves
            .AsNoTracking()
            .Where(row => row.State == CompactMoveState.GivenUp
                && row.FinalPath != null
                && row.FinalPath.Contains(CompactExecutor.StagingFolderName))
            .GroupBy(row => row.LibraryId)
            .Select(group => new { LibraryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (unfinished.Count == 0 && strandedRows.Count == 0)
        {
            return new HealthReport(Name, HealthCheckResult.Ok, "No compaction is waiting to be finished", null);
        }

        var ids = unfinished.Concat(strandedRows).Select(entry => entry.LibraryId).Distinct().ToList();
        var names = await _database.Libraries
            .AsNoTracking()
            .Where(library => ids.Contains(library.Id))
            .ToDictionaryAsync(library => library.Id, library => library.Name, cancellationToken)
            .ConfigureAwait(false);

        string Describe(IEnumerable<(long LibraryId, int Count)> entries) => string.Join(
            ", ",
            entries
                .OrderBy(entry => entry.LibraryId)
                .Select(entry => string.Concat(
                    names.TryGetValue(entry.LibraryId, out var name)
                        ? name
                        : "library " + entry.LibraryId.ToString(CultureInfo.InvariantCulture),
                    " (",
                    entry.Count.ToString(CultureInfo.InvariantCulture),
                    entry.Count == 1 ? " file)" : " files)")));

        var message = string.Empty;

        if (unfinished.Count > 0)
        {
            message = "A compaction left files in the hidden .wondarr-compact folder of "
                + Describe(unfinished.Select(entry => (entry.LibraryId, entry.Count)))
                + ". Their songs are missing from Plex until Compact library finishes the move.";
        }

        if (strandedRows.Count > 0)
        {
            message = string.Concat(
                message,
                message.Length > 0 ? " " : string.Empty,
                "Moves that could not be finished were given up on, and their files still sit in the hidden .wondarr-compact folder of ",
                Describe(strandedRows.Select(entry => (entry.LibraryId, entry.Count))),
                ". Move them back by hand (the Compaction result and the log name each file).");
        }

        return new HealthReport(Name, HealthCheckResult.Warning, message, null);
    }
}
