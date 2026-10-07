using Wondarr.Core.Albums;

namespace Wondarr.Api.Albums;

/// <summary>What a caller asks an album add to add, and where its songs land.</summary>
/// <param name="Source">The provider the album lives on: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="Id">The album's id on that provider: a release MBID or a Deezer album id.</param>
/// <param name="TrackKeys">The tracks to add, as recording MBIDs or Deezer track ids; <see langword="null"/> adds all of them.</param>
/// <param name="LibraryId">The library the songs are filed in, or <see langword="null"/> for the default one.</param>
/// <param name="QualityProfileId">The profile the songs are monitored against, or <see langword="null"/> for the standard one.</param>
/// <param name="Monitored">Whether the new songs are wanted; <see langword="true"/> when omitted.</param>
public sealed record AlbumAddResource(
    string? Source,
    string? Id,
    IReadOnlyList<string>? TrackKeys,
    long? LibraryId,
    long? QualityProfileId,
    bool? Monitored);

/// <summary>
/// What an album add answered with: the queued command. The caller polls
/// <c>GET /api/v1/command/{commandId}</c> for progress and the summary.
/// </summary>
/// <param name="CommandId">The queued <c>AddAlbum</c> command.</param>
public sealed record AlbumAddAcceptedResource(long CommandId);

/// <summary>Which album a song is filed under.</summary>
/// <param name="Source">The provider: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="Id">The album's id on that provider.</param>
public sealed record AlbumRefResource(string Source, string Id);

/// <summary>One album a search found.</summary>
/// <param name="Source">The provider the hit came from.</param>
/// <param name="Id">The release-group MBID or the Deezer album id, as a string.</param>
/// <param name="Title">The album title.</param>
/// <param name="Artist">The album artist's display name.</param>
/// <param name="Year">The year of the first release, when the provider knows it.</param>
/// <param name="Type">The type: Album, Single, EP, Compilation …</param>
/// <param name="TrackCount">How many tracks the album holds, when the provider knows it.</param>
/// <param name="CoverUrl">The cover URL, when the provider has one.</param>
public sealed record AlbumSearchResultResource(
    string Source,
    string Id,
    string Title,
    string Artist,
    string? Year,
    string? Type,
    int? TrackCount,
    string? CoverUrl);

/// <summary>One official release of a release group, as the release picker lists it.</summary>
/// <param name="Id">The release MBID.</param>
/// <param name="Title">The release title.</param>
/// <param name="Date">The release date, when MusicBrainz knows it.</param>
/// <param name="Country">The release country, as an ISO-3166 code.</param>
/// <param name="Formats">The media formats, for example <c>CD</c> or <c>CD + DVD-Video</c>.</param>
/// <param name="TrackCount">How many tracks the release holds.</param>
/// <param name="Disambiguation">The MusicBrainz disambiguation comment.</param>
/// <param name="IsDefault">True when this is the release an album add files the tracks under by default.</param>
public sealed record AlbumReleaseResource(
    string Id,
    string Title,
    string? Date,
    string? Country,
    string? Formats,
    int? TrackCount,
    string? Disambiguation,
    bool IsDefault);

/// <summary>One track of an album's tracklist, with the library's copy of it beside it.</summary>
/// <param name="Disc">The disc the track sits on, starting at 1.</param>
/// <param name="Position">The track's position on its disc, starting at 1.</param>
/// <param name="Title">The track title.</param>
/// <param name="ArtistCredit">The track's display credit.</param>
/// <param name="LengthMs">The track length in milliseconds, when the provider knows it.</param>
/// <param name="MbRecordingId">The recording MBID, for a MusicBrainz track.</param>
/// <param name="DeezerTrackId">The Deezer track id, for a Deezer track.</param>
/// <param name="Isrcs">The ISRCs the recording carries.</param>
/// <param name="SongId">The song the library already holds for this track, when it does.</param>
/// <param name="LibraryId">The library that song is filed in.</param>
/// <param name="Owned">True when the library already holds this track.</param>
public sealed record AlbumTrackResource(
    int Disc,
    int Position,
    string Title,
    string ArtistCredit,
    int? LengthMs,
    string? MbRecordingId,
    long? DeezerTrackId,
    IReadOnlyList<string> Isrcs,
    long? SongId,
    long? LibraryId,
    bool Owned);

/// <summary>Maps the album service's results onto the resources above.</summary>
public static class AlbumResourceExtensions
{
    /// <summary>Maps one search hit.</summary>
    /// <param name="result">The hit to map.</param>
    public static AlbumSearchResultResource ToResource(this AlbumSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AlbumSearchResultResource(
            result.Source,
            result.ReleaseGroupId ?? result.DeezerAlbumId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty,
            result.Title,
            result.Artist,
            result.Year,
            result.Type,
            result.TrackCount,
            result.CoverUrl);
    }

    /// <summary>Maps one release of a release group.</summary>
    /// <param name="release">The release to map.</param>
    public static AlbumReleaseResource ToResource(this AlbumRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new AlbumReleaseResource(
            release.Id,
            release.Title,
            release.Date,
            release.Country,
            release.Formats,
            release.TrackCount,
            release.Disambiguation,
            release.IsDefault);
    }

    /// <summary>Maps one track of a tracklist.</summary>
    /// <param name="track">The track to map.</param>
    public static AlbumTrackResource ToResource(this AlbumTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new AlbumTrackResource(
            track.Disc,
            track.Position,
            track.Title,
            track.ArtistCredit,
            track.LengthMs,
            track.MbRecordingId,
            track.DeezerTrackId,
            track.Isrcs,
            track.SongId,
            track.LibraryId,
            track.Owned);
    }
}
