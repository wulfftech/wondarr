using Wondarr.Core.Domain;

namespace Wondarr.Core.Searching;

/// <summary>
/// The per-song re-search backoff both scheduled search loops share (MATCHING_ENGINE §6.6): the nth
/// consecutive fruitless run waits <c>BackoffHours[min(n − 1, last)]</c> before the song is searched
/// again. A run that grabbed something resets the wait, a run that errored does not count as an
/// attempt, and the missing-song loop counts every run but the user's own while the upgrade loop
/// counts only its own, so neither loop's history delays the other.
/// </summary>
internal static class SearchBackoff
{
    /// <summary>
    /// Whether a song's backoff has expired and it may be searched again.
    /// </summary>
    /// <param name="runs">The song's runs, any order.</param>
    /// <param name="options">The backoff steps.</param>
    /// <param name="now">The instant the loop is picking its batch at.</param>
    /// <param name="countsForBackoff">
    /// Whether one run speaks for this loop's backoff: the missing-song loop counts every run but
    /// the user's own, the upgrade loop only its own <c>Upgrade</c> runs.
    /// </param>
    /// <returns><see langword="true"/> when the song is due for a search.</returns>
    internal static bool IsEligible(
        IReadOnlyList<SearchRun> runs,
        SearchOptions options,
        DateTime now,
        Func<SearchRun, bool> countsForBackoff)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(countsForBackoff);

        if (options.BackoffHours.Count == 0)
        {
            return true;
        }

        var counted = runs
            .Where(run => countsForBackoff(run)
                && run.Outcome is SearchOutcome.NoResults or SearchOutcome.NoAcceptableCandidate or SearchOutcome.Grabbed)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .ToList();

        if (counted.Count == 0)
        {
            return true;
        }

        var failures = 0;

        foreach (var run in counted)
        {
            if (run.Outcome is SearchOutcome.NoResults or SearchOutcome.NoAcceptableCandidate)
            {
                failures++;
            }
            else
            {
                break;
            }
        }

        var index = Math.Clamp(failures - 1, 0, options.BackoffHours.Count - 1);

        return now - counted[0].StartedAt >= TimeSpan.FromHours(options.BackoffHours[index]);
    }
}