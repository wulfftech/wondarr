using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.Searching;

/// <summary>
/// Keeps the automatic search loops away from songs whose library root is missing or unmarked: a grab
/// for such a song would only wait in the queue (imports into it wait), so it is not searched for.
/// </summary>
internal static partial class SearchLibraryGuard
{
    /// <summary>
    /// The candidates whose library is not ready, with one information log line per library naming how
    /// many were left out. Checked live, once per library per call.
    /// </summary>
    /// <param name="database">Where the libraries and songs are.</param>
    /// <param name="roots">Checks each root.</param>
    /// <param name="candidateIds">The songs the loop would search.</param>
    /// <param name="search">The loop's name, for the log.</param>
    /// <param name="logger">The loop's logger.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    internal static async Task<HashSet<long>> UnavailableSongsAsync(
        WondarrDbContext database,
        ILibraryRoots roots,
        IReadOnlyCollection<long> candidateIds,
        string search,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var skipped = new HashSet<long>();

        if (candidateIds.Count == 0)
        {
            return skipped;
        }

        var libraries = await database.Libraries
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var unavailable = libraries
            .Where(library => roots.Check(library.RootPath) != LibraryRootStatus.Ready)
            .ToDictionary(library => library.Id);

        if (unavailable.Count == 0)
        {
            return skipped;
        }

        var ids = unavailable.Keys.ToList();
        var inUnavailable = await database.Songs
            .AsNoTracking()
            .Where(song => ids.Contains(song.LibraryId))
            .Select(song => new { song.Id, song.LibraryId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = candidateIds as ISet<long> ?? candidateIds.ToHashSet();

        foreach (var group in inUnavailable.Where(song => candidates.Contains(song.Id)).GroupBy(song => song.LibraryId))
        {
            foreach (var song in group)
            {
                skipped.Add(song.Id);
            }

            var library = unavailable[group.Key];
            var count = group.Count();
            var reason = roots.Describe(library, roots.Check(library.RootPath));
            LogSkipped(logger, search, count, library.Name, reason);
        }

        return skipped;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Search}: skipped {Count} songs of library {Library} because its folder is not ready. {Reason}")]
    private static partial void LogSkipped(ILogger logger, string search, int count, string library, string reason);
}
