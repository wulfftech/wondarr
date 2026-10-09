using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Songs;

namespace Wondarr.Api.Songs;

/// <summary>
/// Everything a song's own page shows that <c>GET /song/{id}</c> does not. Every external section is
/// <see langword="null"/> when its source is missing, failed or took longer than five seconds; the
/// endpoint still answers 200.
/// </summary>
/// <param name="Releases">The song's cached release options, as the album picker lists them (without the Singles entry).</param>
/// <param name="MusicBrainz">The MusicBrainz recording, or <see langword="null"/> for a song without a recording id.</param>
/// <param name="Deezer">The Deezer track, or <see langword="null"/> for a song without a Deezer id or when Deezer failed.</param>
/// <param name="ReferenceFile">The reference-library row, when the song is owned through a reference library.</param>
/// <param name="Lyrics">Which lyrics sidecars sit next to the file; the text itself is <c>GET /song/{id}/lyrics</c>.</param>
/// <param name="LastFm">What Last.fm knows, or <see langword="null"/> when no Last.fm key is set or Last.fm failed.</param>
public sealed record SongDetailsResource(
    IReadOnlyList<SongReleaseResource> Releases,
    SongMusicBrainzResource? MusicBrainz,
    SongDeezerResource? Deezer,
    SongReferenceFileResource? ReferenceFile,
    SongLyricsAvailabilityResource Lyrics,
    SongLastFmResource? LastFm);

/// <summary>What Last.fm knows about the song.</summary>
/// <param name="Url">The track's Last.fm page; show it as the attribution for the text.</param>
/// <param name="Listeners">How many people have listened to it.</param>
/// <param name="Playcount">How often it has been played.</param>
/// <param name="Tags">The top five tag names.</param>
/// <param name="Wiki">The wiki summary as plain text (Last.fm's HTML and its "Read more" link are removed), or <see langword="null"/>.</param>
/// <param name="Artist">The artist, or <see langword="null"/> when Last.fm could not describe them.</param>
/// <param name="Similar">Up to ten similar tracks.</param>
public sealed record SongLastFmResource(
    string? Url,
    long? Listeners,
    long? Playcount,
    IReadOnlyList<string> Tags,
    string? Wiki,
    SongLastFmArtistResource? Artist,
    IReadOnlyList<SongLastFmSimilarResource> Similar);

/// <summary>What Last.fm knows about the artist.</summary>
/// <param name="Name">The artist's name as Last.fm spells it.</param>
/// <param name="Url">The artist's Last.fm page.</param>
/// <param name="BioSummary">The biography summary as plain text, or <see langword="null"/>.</param>
/// <param name="Listeners">How many people listen to the artist.</param>
public sealed record SongLastFmArtistResource(string Name, string? Url, string? BioSummary, long? Listeners);

/// <summary>A track Last.fm lists as similar.</summary>
/// <param name="Artist">The artist.</param>
/// <param name="Title">The title.</param>
/// <param name="Url">The track's Last.fm page.</param>
/// <param name="Match">How similar, from 0 to 1.</param>
/// <param name="SongId">The Wondarr song id when that recording is already in the library, otherwise <see langword="null"/>.</param>
public sealed record SongLastFmSimilarResource(string Artist, string Title, string? Url, double? Match, long? SongId);

/// <summary>One release the song appears on.</summary>
/// <param name="Key">The album key a re-assignment names: a release MBID or a synthetic key.</param>
/// <param name="MbReleaseId">The MusicBrainz release id, or <see langword="null"/>.</param>
/// <param name="MbReleaseGroupId">The MusicBrainz release group id, or <see langword="null"/>.</param>
/// <param name="Title">The release title.</param>
/// <param name="AlbumArtist">The release's album artist.</param>
/// <param name="PrimaryType">The primary release type, for example <c>Album</c>, or <see langword="null"/>.</param>
/// <param name="SecondaryTypes">The secondary release types, for example <c>Compilation</c>.</param>
/// <param name="Status">The release status, or <see langword="null"/>.</param>
/// <param name="Date">The release date, or <see langword="null"/>.</param>
/// <param name="TrackNo">The song's track number on the release, or <see langword="null"/>.</param>
/// <param name="TotalTracks">The release's track count, or <see langword="null"/>.</param>
/// <param name="CoverUrl">The cover thumbnail URL, built from ids (no request is made), or <see langword="null"/>.</param>
/// <param name="IsCurrent">Whether the song is filed under this release right now.</param>
public sealed record SongReleaseResource(
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
    string? CoverUrl,
    bool IsCurrent);

/// <summary>What MusicBrainz knows about the recording.</summary>
/// <param name="RecordingId">The recording MBID.</param>
/// <param name="FirstReleaseDate">The recording's first release date, or <see langword="null"/>.</param>
/// <param name="Disambiguation">The disambiguation comment, or <see langword="null"/>.</param>
/// <param name="Isrcs">The ISRCs the recording is registered under.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="Url">The recording's MusicBrainz page.</param>
public sealed record SongMusicBrainzResource(
    string RecordingId,
    string? FirstReleaseDate,
    string? Disambiguation,
    IReadOnlyList<string> Isrcs,
    string ArtistCredit,
    string Url);

/// <summary>What Deezer knows about the track.</summary>
/// <param name="TrackId">The Deezer track id.</param>
/// <param name="Url">The track's Deezer page.</param>
/// <param name="Rank">Deezer's popularity rank (higher is more popular), or <see langword="null"/>.</param>
/// <param name="ExplicitLyrics">Whether Deezer flags the lyrics as explicit.</param>
/// <param name="Bpm">The tempo in beats per minute, or <see langword="null"/> when Deezer has not analysed the track.</param>
/// <param name="Gain">Deezer's loudness gain in dB, or <see langword="null"/>.</param>
/// <param name="ReleaseDate">The release date, or <see langword="null"/>.</param>
/// <param name="AlbumCoverUrl">The album cover URL, or <see langword="null"/>.</param>
public sealed record SongDeezerResource(
    long TrackId,
    string Url,
    long? Rank,
    bool ExplicitLyrics,
    double? Bpm,
    double? Gain,
    string? ReleaseDate,
    string? AlbumCoverUrl);

/// <summary>The reference-library row a song is owned through.</summary>
/// <param name="LibraryId">The reference library.</param>
/// <param name="LibraryName">The library's name.</param>
/// <param name="RelativePath">The file's path relative to the library root.</param>
/// <param name="IdentifiedBy">How the file was identified, or <see langword="null"/>.</param>
/// <param name="Confidence">How sure the identification was, from 0 to 1.</param>
/// <param name="State">What the last scan concluded, as a camel-case string.</param>
public sealed record SongReferenceFileResource(
    long LibraryId,
    string LibraryName,
    string RelativePath,
    string? IdentifiedBy,
    double Confidence,
    ReferenceFileState State);

/// <summary>Which lyrics sidecars sit next to the song's file.</summary>
/// <param name="Source"><c>sidecar</c> when at least one exists, otherwise <c>none</c>.</param>
/// <param name="Synced">Whether a <c>.lrc</c> sidecar exists.</param>
/// <param name="Plain">Whether a <c>.txt</c> sidecar exists.</param>
public sealed record SongLyricsAvailabilityResource(string Source, bool Synced, bool Plain);

/// <summary>A song's lyrics.</summary>
/// <param name="Source"><c>sidecar</c>, <c>lrclib</c>, or <see langword="null"/> when there are none.</param>
/// <param name="Synced">The LRC text with its time tags, or <see langword="null"/>.</param>
/// <param name="Plain">The unsynced text, or <see langword="null"/>.</param>
public sealed record SongLyricsResource(string? Source, string? Synced, string? Plain);

/// <summary>Maps the song-page data onto the resources above.</summary>
public static class SongDetailsResourceExtensions
{
    private const string MusicBrainzRecordingUrl = "https://musicbrainz.org/recording/";
    private const string DeezerTrackUrl = "https://www.deezer.com/track/";
    private const string CoverArtArchive = "https://coverartarchive.org/";

    /// <summary>Maps the details.</summary>
    /// <param name="details">The details to map.</param>
    public static SongDetailsResource ToResource(this SongDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        return new SongDetailsResource(
            details.Releases
                .Select(option => option.ToReleaseResource(
                    string.Equals(option.Key, details.CurrentAlbumKey, StringComparison.Ordinal)))
                .ToList(),
            details.MusicBrainz is { } musicBrainz
                ? new SongMusicBrainzResource(
                    musicBrainz.RecordingId,
                    musicBrainz.FirstReleaseDate,
                    musicBrainz.Disambiguation,
                    musicBrainz.Isrcs,
                    musicBrainz.ArtistCredit,
                    MusicBrainzRecordingUrl + Uri.EscapeDataString(musicBrainz.RecordingId))
                : null,
            details.Deezer is { } deezer
                ? new SongDeezerResource(
                    deezer.TrackId,
                    DeezerTrackUrl + deezer.TrackId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    deezer.Rank,
                    deezer.ExplicitLyrics,
                    deezer.Bpm,
                    deezer.Gain,
                    deezer.ReleaseDate,
                    deezer.AlbumCoverUrl)
                : null,
            details.ReferenceFile is { } reference
                ? new SongReferenceFileResource(
                    reference.LibraryId,
                    reference.LibraryName,
                    reference.RelativePath,
                    reference.IdentifiedBy,
                    reference.Confidence,
                    reference.State)
                : null,
            new SongLyricsAvailabilityResource(details.Lyrics.Source, details.Lyrics.Synced, details.Lyrics.Plain),
            details.LastFm is { } lastFm
                ? new SongLastFmResource(
                    lastFm.Url,
                    lastFm.Listeners,
                    lastFm.Playcount,
                    lastFm.Tags,
                    lastFm.Wiki,
                    lastFm.Artist is { } artist
                        ? new SongLastFmArtistResource(artist.Name, artist.Url, artist.BioSummary, artist.Listeners)
                        : null,
                    lastFm.Similar
                        .Select(similar => new SongLastFmSimilarResource(
                            similar.Artist,
                            similar.Title,
                            similar.Url,
                            similar.Match,
                            similar.SongId))
                        .ToList())
                : null);
    }

    /// <summary>Maps lyrics.</summary>
    /// <param name="lyrics">The lyrics to map.</param>
    public static SongLyricsResource ToResource(this SongLyricsText lyrics)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        return new SongLyricsResource(lyrics.Source, lyrics.Synced, lyrics.Plain);
    }

    /// <summary>Maps one release option.</summary>
    /// <param name="option">The option to map.</param>
    /// <param name="isCurrent">Whether the song is filed under it right now.</param>
    public static SongReleaseResource ToReleaseResource(this ReleaseOption option, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(option);

        return new SongReleaseResource(
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
            CoverUrlFor(option),
            isCurrent);
    }

    /// <summary>
    /// The cover thumbnail URL of a release option, built with no network call: the Cover Art Archive's
    /// 250 px front image of the release, or of its release group when only that is known, else the cover
    /// a Deezer option carries; <see langword="null"/> when there is none. The Change album dialog
    /// (<c>AlbumOptionResource</c>) and the song page's release list both use this one rule.
    /// </summary>
    /// <param name="option">The option to build a URL for.</param>
    public static string? CoverUrlFor(ReleaseOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        if (!string.IsNullOrWhiteSpace(option.MbReleaseId))
        {
            return $"{CoverArtArchive}release/{Uri.EscapeDataString(option.MbReleaseId)}/front-250";
        }

        if (!string.IsNullOrWhiteSpace(option.MbReleaseGroupId))
        {
            return $"{CoverArtArchive}release-group/{Uri.EscapeDataString(option.MbReleaseGroupId)}/front-250";
        }

        return string.IsNullOrWhiteSpace(option.CoverUrl) ? null : option.CoverUrl;
    }
}
