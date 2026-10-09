using Wondarr.Core.Domain;

namespace Wondarr.Api.Songs;

/// <summary>
/// The song shape every song-returning endpoint uses: the wanted lists, history rows and, in later
/// phases, the queue. Everything it exposes is loaded by the query that produced the song, so the
/// mapper never triggers a second round trip.
/// </summary>
/// <param name="Id">The song row.</param>
/// <param name="Title">The track title.</param>
/// <param name="ArtistCredit">The display credit, for example "Daft Punk feat. Pharrell Williams".</param>
/// <param name="PrimaryArtistId">The main artist's id.</param>
/// <param name="PrimaryArtistName">The main artist's name.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id, or <see langword="null"/>.</param>
/// <param name="Isrcs">The ISRCs the recording is registered under.</param>
/// <param name="DurationMs">The known duration in milliseconds, or <see langword="null"/>.</param>
/// <param name="VersionFlags">The version flags, as snake_case wire names.</param>
/// <param name="Monitored">Whether the song is wanted.</param>
/// <param name="QualityProfileId">The profile that decides acceptable qualities and the cutoff.</param>
/// <param name="LibraryId">The library the song is filed in.</param>
/// <param name="AddedBy">What added the song: <c>ui</c>, <c>api</c> or <c>list:{id}</c>.</param>
/// <param name="Added">The UTC instant the song row was created.</param>
/// <param name="HasFile">Whether a file satisfies the song.</param>
/// <param name="QualityId">The file's quality, or <see langword="null"/> when there is no file.</param>
/// <param name="AlbumContext">The album the song is filed under, or <see langword="null"/> until assigned.</param>
/// <param name="File">The file the song holds, or <see langword="null"/> while it has none.</param>
/// <param name="Tags">The song's tags (trimmed, lower-case); empty when it has none.</param>
public sealed record SongResource(
    long Id,
    string Title,
    string ArtistCredit,
    long PrimaryArtistId,
    string PrimaryArtistName,
    string? MbRecordingId,
    long? DeezerId,
    IReadOnlyList<string> Isrcs,
    int? DurationMs,
    IReadOnlyList<string> VersionFlags,
    bool Monitored,
    long QualityProfileId,
    long LibraryId,
    string AddedBy,
    DateTime Added,
    bool HasFile,
    long? QualityId,
    SongAlbumContextResource? AlbumContext,
    SongFileResource? File = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>Gets the song's tags; never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}

/// <summary>The file a song holds: where it is and what is on disk (its quality is the song's <c>qualityId</c>).</summary>
/// <param name="Path">The file's path in the library.</param>
/// <param name="Codec">The codec on disk (<c>mp3</c>, <c>flac</c>, <c>aac</c>, <c>opus</c>, …).</param>
/// <param name="Container">The container on disk.</param>
/// <param name="BitrateKbps">The measured bitrate, when known.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="SourceType">Where it came from: <c>soulseek</c>, <c>youtube</c>, <c>reference</c>, ….</param>
public sealed record SongFileResource(
    string Path,
    string Codec,
    string Container,
    int? BitrateKbps,
    long Size,
    string SourceType);

/// <summary>
/// The song's album context: what the folder layout and Plex's grouping key on. A song always has one
/// once the album policy has run, so it is the one part of <see cref="SongResource"/> that can be
/// <see langword="null"/>.
/// </summary>
/// <param name="Kind">Whether this is a real album, a single, an EP or a synthetic pseudo-album.</param>
/// <param name="AlbumTitle">The album title.</param>
/// <param name="AlbumArtist">The album artist.</param>
/// <param name="AlbumKey">The grouping key: a real release MBID, or a synthetic pseudo-album UUID.</param>
/// <param name="MbReleaseId">The MusicBrainz release id, or <see langword="null"/>.</param>
/// <param name="MbReleaseGroupId">The MusicBrainz release group id, or <see langword="null"/>.</param>
/// <param name="TrackNo">The track number within the medium, or <see langword="null"/>.</param>
/// <param name="DiscNo">The disc number, or <see langword="null"/>.</param>
/// <param name="TotalTracks">The number of tracks on the release, or <see langword="null"/>.</param>
/// <param name="Date">The release date as <c>YYYY</c> or <c>YYYY-MM-DD</c>, or <see langword="null"/>.</param>
/// <param name="OriginalDate">The original release date, or <see langword="null"/>.</param>
/// <param name="CoverUrl">The cover art URL, or <see langword="null"/>.</param>
/// <param name="IsVariousArtists">Whether the album artist is "Various Artists".</param>
/// <param name="Pinned">
/// Whether the user chose this album explicitly; the Compact task never re-plans a pinned song.
/// </param>
public sealed record SongAlbumContextResource(
    AlbumContextKind Kind,
    string AlbumTitle,
    string AlbumArtist,
    string AlbumKey,
    string? MbReleaseId,
    string? MbReleaseGroupId,
    int? TrackNo,
    int? DiscNo,
    int? TotalTracks,
    string? Date,
    string? OriginalDate,
    string? CoverUrl,
    bool IsVariousArtists,
    bool Pinned);

/// <summary>Maps the persisted song to the shape the API returns.</summary>
public static class SongResourceExtensions
{
    /// <summary>
    /// Maps a song. <c>PrimaryArtist</c>, <c>AlbumContext</c> and <c>File</c> must have been loaded
    /// by the query — that is what "has a file" and <c>qualityId</c> are read from.
    /// </summary>
    /// <param name="song">The song to map.</param>
    public static SongResource ToResource(this Song song)
    {
        ArgumentNullException.ThrowIfNull(song);

        return new SongResource(
            song.Id,
            song.Title,
            song.ArtistCredit,
            song.PrimaryArtistId,
            song.PrimaryArtist?.Name ?? string.Empty,
            song.MbRecordingId,
            song.DeezerId,
            song.Isrcs,
            song.DurationMs,
            song.VersionFlags,
            song.Monitored,
            song.QualityProfileId,
            song.LibraryId,
            song.AddedBy,
            song.CreatedAt,
            song.File is not null,
            song.File?.QualityId,
            song.AlbumContext?.ToResource(),
            song.File is { } file
                ? new SongFileResource(file.Path, file.Codec, file.Container, file.BitrateKbps, file.Size, file.SourceType)
                : null,
            song.Tags);
    }

    /// <summary>Maps an album context.</summary>
    /// <param name="context">The album context to map.</param>
    public static SongAlbumContextResource ToResource(this AlbumContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new SongAlbumContextResource(
            context.Kind,
            context.AlbumTitle,
            context.AlbumArtist,
            context.AlbumKey,
            context.MbReleaseId,
            context.MbReleaseGroupId,
            context.TrackNo,
            context.DiscNo,
            context.TotalTracks,
            context.Date,
            context.OriginalDate,
            context.CoverUrl,
            context.IsVariousArtists,
            context.Pinned);
    }
}
