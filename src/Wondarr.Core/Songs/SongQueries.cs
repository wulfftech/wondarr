using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;

namespace Wondarr.Core.Songs;

/// <summary>
/// The one query shape for "the song with everything filing it needs", so the import, the adopter,
/// the compactor and the converter cannot drift apart.
/// </summary>
public static class SongQueries
{
    /// <summary>
    /// Includes the album context (it decides the folder and the tags), the file and the artist
    /// credits with their artists (what the tag writer writes).
    /// </summary>
    /// <remarks>
    /// Deliberately without the file's <see cref="SongFile.Quality"/>: a tracked quality navigation
    /// changes how EF fixes up a changed <see cref="SongFile.QualityId"/>, so only the callers that
    /// need it use <see cref="IncludeFilingGraphWithQuality"/>.
    /// </remarks>
    /// <param name="songs">The songs to include on.</param>
    /// <returns>The songs, with the filing graph included.</returns>
    public static IQueryable<Song> IncludeFilingGraph(this IQueryable<Song> songs) =>
        songs
            .Include(song => song.AlbumContext)
            .Include(song => song.File)
            .Include(song => song.Artists)
                .ThenInclude(credit => credit.Artist);

    /// <summary>
    /// <see cref="IncludeFilingGraph"/> plus the file's <see cref="SongFile.Quality"/>, for the callers
    /// that compare or replace the file's quality while they hold the song.
    /// </summary>
    /// <param name="songs">The songs to include on.</param>
    /// <returns>The songs, with the filing graph and the file's quality included.</returns>
    public static IQueryable<Song> IncludeFilingGraphWithQuality(this IQueryable<Song> songs) =>
        songs
            .Include(song => song.AlbumContext)
            .Include(song => song.File).ThenInclude(file => file!.Quality)
            .Include(song => song.Artists)
                .ThenInclude(credit => credit.Artist);
}
