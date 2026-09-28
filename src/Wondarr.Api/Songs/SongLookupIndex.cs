using Wondarr.Core.Identity;
using Wondarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Api.Songs;

/// <summary>
/// Answers "which of these lookup candidates is already in the library?" in one query.
/// <para>
/// The identity resolver is read-only and knows nothing about the database, and
/// <see cref="Wondarr.Core.Songs.ISongService"/> has no id-list lookup, so the read lives here: the
/// controller only maps its result onto <see cref="SongLookupResource.ExistingSongId"/>.
/// </para>
/// </summary>
public static class SongLookupIndex
{
    /// <summary>The key a candidate and a stored song are matched by.</summary>
    private const string MbPrefix = "mb:";

    /// <summary>The key a Deezer-only candidate and a stored song are matched by.</summary>
    private const string DeezerPrefix = "dz:";

    /// <summary>
    /// Finds the song holding each candidate's MBID or Deezer id. One query for the whole list,
    /// whatever its length.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="candidates">The candidates the resolver just returned.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The candidate key to song id map; a key with no entry is not in the library.</returns>
    public static async Task<IReadOnlyDictionary<string, long>> FindExistingAsync(
        WondarrDbContext database,
        IReadOnlyList<SongCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(candidates);

        var mbIds = new List<string>();
        var deezerIds = new List<long?>();

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.MbRecordingId))
            {
                var id = candidate.MbRecordingId!.ToLowerInvariant();
                if (!mbIds.Contains(id, StringComparer.Ordinal))
                {
                    mbIds.Add(id);
                }
            }
            else if (candidate.DeezerId is { } deezerId && !deezerIds.Contains(deezerId))
            {
                deezerIds.Add(deezerId);
            }
        }

        var existing = new Dictionary<string, long>(StringComparer.Ordinal);
        if (mbIds.Count == 0 && deezerIds.Count == 0)
        {
            return existing;
        }

        var rows = await database.Songs
            .AsNoTracking()
            .Where(song =>
                (song.MbRecordingId != null && mbIds.Contains(song.MbRecordingId))
                || (song.DeezerId != null && deezerIds.Contains(song.DeezerId)))
            .Select(song => new { song.Id, song.MbRecordingId, song.DeezerId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var key = Key(row.MbRecordingId, row.DeezerId);
            if (key is not null)
            {
                existing.TryAdd(key, row.Id);
            }
        }

        return existing;
    }

    /// <summary>The key a candidate is looked up by: its recording MBID, or its Deezer id without one.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The key, or <see langword="null"/> when the candidate carries no id at all.</returns>
    public static string? KeyOf(SongCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return Key(candidate.MbRecordingId, candidate.DeezerId);
    }

    /// <summary>The same key, from whichever of the two ids is set.</summary>
    private static string? Key(string? mbRecordingId, long? deezerId) =>
        !string.IsNullOrWhiteSpace(mbRecordingId)
            ? MbPrefix + mbRecordingId.ToLowerInvariant()
            : deezerId is { } id
                ? DeezerPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
}
