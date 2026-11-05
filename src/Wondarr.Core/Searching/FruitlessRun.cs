using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Searching;

/// <summary>
/// Turns a run that grabbed things but got nothing out of them into a fruitless one. A run is set to
/// <see cref="SearchOutcome.Grabbed"/> when it hands a candidate to a download client, long before the
/// file is verified, so a run whose every grab then failed or was rejected would otherwise read as a
/// success to <see cref="SearchBackoff"/> and reset the song's wait to the shortest step.
/// </summary>
internal static class FruitlessRun
{
    /// <summary>
    /// Records the run of a failed queue item as <see cref="SearchOutcome.NoAcceptableCandidate"/> when
    /// that failure left the run with nothing: no candidate left to try (the caller has asked), and none
    /// of the run's grabs for its own song imported or still in flight. The caller has already saved the
    /// item as failed, and saves the run.
    /// </summary>
    /// <param name="database">The context the caller saves; the run is read and changed through it.</param>
    /// <param name="item">The item that failed.</param>
    /// <param name="lead">The words put before <paramref name="reason"/> in the run's message.</param>
    /// <param name="reason">Why the item failed.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see langword="true"/> when the run was changed.</returns>
    internal static async Task<bool> RecordAsync(
        WondarrDbContext database,
        QueueItem item,
        string lead,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(item);

        var run = await database.SearchRuns
            .FirstOrDefaultAsync(entry => entry.Id == item.SearchRunId, cancellationToken)
            .ConfigureAwait(false);

        // A bundled partner's item sits on another song's run: its failure says nothing about that song.
        if (run is not { Outcome: SearchOutcome.Grabbed } || item.SongId != run.SongId)
        {
            return false;
        }

        // Every state but Failed and Cancelled is an import or a grab still in flight.
        var bore = await database.QueueItems
            .AnyAsync(
                other => other.SearchRunId == run.Id
                    && other.SongId == run.SongId
                    && other.State != QueueItemState.Failed
                    && other.State != QueueItemState.Cancelled,
                cancellationToken)
            .ConfigureAwait(false);

        if (bore)
        {
            return false;
        }

        run.Outcome = SearchOutcome.NoAcceptableCandidate;
        run.Message = string.Concat(run.Message, " ", lead, " ", reason);

        return true;
    }
}
