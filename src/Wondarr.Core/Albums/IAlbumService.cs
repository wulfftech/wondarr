namespace Wondarr.Core.Albums;

/// <summary>
/// The album add (ARCHITECTURE §5.6): search albums, list a release group's official releases, read a
/// release's tracklist, and add its tracks as songs. The unit stays the song (ADR-0001) — an album
/// add is only a batch of songs that are pinned to one release.
/// </summary>
public interface IAlbumService
{
    /// <summary>Searches MusicBrainz for release groups, and Deezer when MusicBrainz has nothing.</summary>
    /// <param name="term">What the user typed, for example <c>Queen - A Night at the Opera</c>.</param>
    /// <param name="limit">How many hits to return at most.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits, best first.</returns>
    /// <exception cref="ArgumentException">The term is blank.</exception>
    Task<IReadOnlyList<AlbumSearchResult>> SearchAsync(
        string term,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every official release of a release group, with the default one marked.</summary>
    /// <param name="releaseGroupId">The release-group MBID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The releases, best first.</returns>
    Task<IReadOnlyList<AlbumRelease>> GetReleasesAsync(
        string releaseGroupId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an album's tracklist, with the library's copy of every track beside it.</summary>
    /// <param name="album">The album to read.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tracks in album order, or <see langword="null"/> when the provider does not know the id.</returns>
    /// <exception cref="ArgumentException">The source is not one Wondarr reads, or the id is not one.</exception>
    Task<IReadOnlyList<AlbumTrack>?> GetTracklistAsync(
        AlbumRef album,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an album's tracks as songs, in one batch.</summary>
    /// <param name="request">The album, the tracks to add and where the songs land.</param>
    /// <param name="reportProgress">Called with a progress message, when one is given.</param>
    /// <param name="cancellationToken">Cancels the add.</param>
    /// <returns>What the add did.</returns>
    /// <exception cref="ArgumentException">The request is unusable, or the library or profile is unknown.</exception>
    /// <exception cref="KeyNotFoundException">The provider does not know the album.</exception>
    Task<AlbumAddResult> AddAsync(
        AlbumAddRequest request,
        Func<string, Task>? reportProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks the library and quality profile an album add names, before the command is queued.</summary>
    /// <param name="libraryId">The library, or <see langword="null"/> for the default one.</param>
    /// <param name="qualityProfileId">The quality profile, or <see langword="null"/> for the standard one.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="ArgumentException">The library or the profile does not exist, with the field named.</exception>
    Task ValidateAddAsync(
        long? libraryId,
        long? qualityProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>The album a song is filed under, when it is a real MusicBrainz release.</summary>
    /// <param name="songId">The song to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The album reference, or <see langword="null"/> when the song is unknown or not filed under a release.</returns>
    Task<AlbumRef?> GetAlbumForSongAsync(long songId, CancellationToken cancellationToken = default);
}
