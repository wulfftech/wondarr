using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Songs;

namespace Wondarr.Api.Songs;

/// <summary>
/// What a caller adds a song by: exactly one of the two ids, and optionally where it lands. The
/// <c>SongResource</c> itself lives with the other song shapes in <c>SongResource.cs</c>.
/// </summary>
/// <param name="MbRecordingId">The MusicBrainz recording MBID, when the caller knows one.</param>
/// <param name="DeezerId">The Deezer track id, when the caller knows one.</param>
/// <param name="QualityProfileId">The profile the song is monitored against, or <see langword="null"/> for the default.</param>
/// <param name="LibraryId">The library the song is filed in, or <see langword="null"/> for the default library.</param>
/// <param name="Monitored">Whether the new song is wanted; defaults to <see langword="true"/>.</param>
public sealed record SongAddResource(
    string? MbRecordingId,
    long? DeezerId,
    long? QualityProfileId,
    long? LibraryId,
    bool? Monitored);

/// <summary>The two fields a song can be edited by.</summary>
/// <param name="Monitored">The new monitored flag, or <see langword="null"/> to leave it alone.</param>
/// <param name="QualityProfileId">The new quality profile, or <see langword="null"/> to leave it alone.</param>
public sealed record SongUpdateResource(bool? Monitored, long? QualityProfileId);

/// <summary>One search term for the add dialog's lookup.</summary>
/// <param name="Term">"Artist - Title", free text, an MBID, an ISRC or a Deezer link.</param>
public sealed record SongLookupRequest(string? Term);

/// <summary>One result of the add dialog's lookup, mapped from a ranked candidate.</summary>
/// <param name="Source">The provider the candidate came from: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="MbRecordingId">The recording MBID, for a MusicBrainz candidate.</param>
/// <param name="DeezerId">The Deezer track id, for a Deezer candidate.</param>
/// <param name="Title">The title, including any version suffix.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="DurationMs">The duration in milliseconds, when the provider knows it.</param>
/// <param name="Disambiguation">The MusicBrainz disambiguation comment, when it carries one.</param>
/// <param name="VersionFlags">The version hints the title carries, as snake_case wire names.</param>
/// <param name="FirstReleaseDate">The first release date, when the provider knows it.</param>
/// <param name="ReleaseTypes">The distinct release types of the hit's release sample, for example <c>Album</c>.</param>
/// <param name="AlbumTitle">The title of the hit's release sample, when it carries one.</param>
/// <param name="CoverUrl">The cover URL, when one is known.</param>
/// <param name="Isrcs">The ISRCs, upper-case.</param>
/// <param name="Score">The score against the query, from 0 to 100.</param>
/// <param name="ViaIsrc">Whether the candidate was reached through the ISRC bridge from a Deezer hit.</param>
/// <param name="ExistingSongId">The id of the song already holding this MBID or Deezer id, or <see langword="null"/>.</param>
public sealed record SongLookupResource(
    string Source,
    string? MbRecordingId,
    long? DeezerId,
    string Title,
    string ArtistCredit,
    int? DurationMs,
    string? Disambiguation,
    IReadOnlyList<string> VersionFlags,
    string? FirstReleaseDate,
    IReadOnlyList<string> ReleaseTypes,
    string? AlbumTitle,
    string? CoverUrl,
    IReadOnlyList<string> Isrcs,
    double Score,
    bool ViaIsrc,
    long? ExistingSongId);

/// <summary>
/// One album a song could be filed under, for the album picker. The final entry is always the
/// artist's Singles pseudo-album, which is not a release at all.
/// </summary>
/// <param name="Key">The album key an explicit re-assignment names: a release MBID, a synthetic Deezer key, or <c>singles</c>.</param>
/// <param name="MbReleaseId">The MusicBrainz release id, or <see langword="null"/>.</param>
/// <param name="MbReleaseGroupId">The MusicBrainz release group id, or <see langword="null"/>.</param>
/// <param name="Title">The album title.</param>
/// <param name="AlbumArtist">The album artist.</param>
/// <param name="PrimaryType">The primary release type, for example <c>Album</c>, or <see langword="null"/>.</param>
/// <param name="SecondaryTypes">The secondary release types, for example <c>Compilation</c>.</param>
/// <param name="Status">The release status, or <see langword="null"/>.</param>
/// <param name="Date">The release date, or <see langword="null"/>.</param>
/// <param name="TrackNo">The song's track number on the release, or <see langword="null"/>.</param>
/// <param name="TotalTracks">The release's track count, or <see langword="null"/>.</param>
/// <param name="IsCurrent">Whether the song is filed under this album right now.</param>
/// <param name="CoverUrl">A cover thumbnail the browser can load (built from ids, never fetched by the server), or <see langword="null"/>.</param>
/// <param name="IsVariousArtists">Whether the release is credited to Various Artists.</param>
/// <param name="OriginalDate">The release group's first release date, or <see langword="null"/>.</param>
/// <param name="DiscNo">The disc the song sits on, or <see langword="null"/> when unknown.</param>
public sealed record AlbumOptionResource(
    string Key,
    string? MbReleaseId,
    string? MbReleaseGroupId,
    string Title,
    string AlbumArtist,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? Status,
    string? Date,
    int? TrackNo,
    int? TotalTracks,
    bool IsCurrent,
    string? CoverUrl,
    bool IsVariousArtists,
    string? OriginalDate,
    int? DiscNo);

/// <summary>The album a song is explicitly moved to.</summary>
/// <param name="AlbumKey">One of the song's album option keys, or the literal <c>singles</c>.</param>
public sealed record SongAlbumContextUpdateResource(string? AlbumKey);

/// <summary>One artist, with how many songs credit them.</summary>
/// <param name="Id">The artist id.</param>
/// <param name="Name">The display name.</param>
/// <param name="SortName">The name used for sorting.</param>
/// <param name="MbArtistId">The MusicBrainz artist id, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer artist id, or <see langword="null"/>.</param>
/// <param name="SongCount">How many songs credit them, in any role.</param>
public sealed record ArtistResource(
    long Id,
    string Name,
    string SortName,
    string? MbArtistId,
    long? DeezerId,
    int SongCount);

/// <summary>A fresh, short-lived Deezer preview URL.</summary>
/// <param name="Url">The signed preview URL; it expires after about half an hour.</param>
public sealed record PreviewResource(string Url);

/// <summary>Maps candidates, release options and artists onto the resources above.</summary>
public static class SongApiResourceExtensions
{
    /// <summary>Maps a ranked candidate. <paramref name="existingSongId"/> comes from the one library query.</summary>
    /// <param name="candidate">The candidate to map.</param>
    /// <param name="existingSongId">The id of the song already holding the candidate's id, or <see langword="null"/>.</param>
    public static SongLookupResource ToResource(this SongCandidate candidate, long? existingSongId)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new SongLookupResource(
            candidate.Source,
            candidate.MbRecordingId,
            candidate.DeezerId,
            candidate.Title,
            candidate.ArtistCredit,
            candidate.DurationMs,
            candidate.Disambiguation,
            VersionFlagNames.ToWireNames(candidate.Flags),
            candidate.FirstReleaseDate,
            candidate.ReleaseTypes,
            candidate.AlbumTitle,
            candidate.CoverUrl,
            candidate.Isrcs,
            candidate.Score,
            candidate.ViaIsrc,
            existingSongId);
    }

    /// <summary>Maps one release option.</summary>
    /// <param name="option">The release option to map.</param>
    /// <param name="isCurrent">Whether the song is filed under this option right now.</param>
    public static AlbumOptionResource ToResource(this ReleaseOption option, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(option);

        return new AlbumOptionResource(
            option.Key,
            option.MbReleaseId,
            option.MbReleaseGroupId,
            option.Title,
            option.AlbumArtist,
            option.PrimaryType,
            option.SecondaryTypes,
            option.Status,
            option.Date,
            option.TrackNo,
            option.TotalTracks,
            isCurrent,
            SongDetailsResourceExtensions.CoverUrlFor(option),
            option.IsVariousArtists,
            option.ReleaseGroupFirstDate,
            option.DiscNo);
    }

    /// <summary>Maps an artist and its song count.</summary>
    /// <param name="summary">The artist and its count.</param>
    public static ArtistResource ToResource(this ArtistSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ArtistResource(
            summary.Artist.Id,
            summary.Artist.Name,
            summary.Artist.SortName,
            summary.Artist.MbArtistId,
            summary.Artist.DeezerId,
            summary.SongCount);
    }
}
