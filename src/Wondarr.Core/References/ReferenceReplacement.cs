using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.References;

/// <summary>
/// Which songs held by a reference file Wondarr may replace with a better copy (DECISIONS build session 14
/// #32): the file was settled with Replace in the Match queue, or its reference library replaces files
/// below the cutoff. Such a song is searched, upgraded and bundled like one with a library file, under the
/// same cutoff rules; the import files the new copy in the library and repoints the song, and the user's
/// file stays exactly where it is. Every other reference-held song is still never searched for.
/// </summary>
public static class ReferenceReplacement
{
    /// <summary>
    /// The songs, of those given (or of all of them), whose held file is a reference file that may be
    /// replaced. Only the file the song holds decides: a second copy of the recording in another library
    /// does not make the song replaceable.
    /// </summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="songIds">The songs to look at, or <see langword="null"/> for every song.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The replaceable songs' ids.</returns>
    public static async Task<HashSet<long>> ReplaceableSongsAsync(
        WondarrDbContext database,
        IReadOnlyCollection<long>? songIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Nobody asked for a replacement anywhere: the usual case answers without reading a song file.
        if (!await database.ReferenceLibraries.AnyAsync(library => library.ReplaceBelowCutoff, cancellationToken).ConfigureAwait(false)
            && !await database.ReferenceFiles.AnyAsync(row => row.Replace, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var held = database.SongFiles
            .AsNoTracking()
            .Where(file => file.SourceType == SourceTypes.Reference && file.SourceRef != null);

        if (songIds is not null)
        {
            if (songIds.Count == 0)
            {
                return [];
            }

            held = held.Where(file => songIds.Contains(file.SongId));
        }

        var files = await held
            .Select(file => new { file.SongId, file.SourceRef })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songByRow = new Dictionary<long, long>();

        foreach (var file in files)
        {
            if (SongFileSource.Parse(SourceTypes.Reference, file.SourceRef).ReferenceFileId is { } rowId)
            {
                songByRow[rowId] = file.SongId;
            }
        }

        if (songByRow.Count == 0)
        {
            return [];
        }

        var rowIds = songByRow.Keys.ToList();
        var replaceable = await database.ReferenceFiles
            .AsNoTracking()
            .Where(row => rowIds.Contains(row.Id) && (row.Replace || row.ReferenceLibrary.ReplaceBelowCutoff))
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. replaceable.Select(rowId => songByRow[rowId])];
    }

    /// <summary>Whether one song's held reference file may be replaced.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns><see langword="true"/> when it may.</returns>
    public static async Task<bool> IsReplaceableAsync(
        WondarrDbContext database,
        long songId,
        CancellationToken cancellationToken) =>
        (await ReplaceableSongsAsync(database, [songId], cancellationToken).ConfigureAwait(false)).Contains(songId);
}
