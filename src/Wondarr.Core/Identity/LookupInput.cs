using System.Globalization;
using System.Text.RegularExpressions;

namespace Wondarr.Core.Identity;

/// <summary>What the user's input turned out to be.</summary>
public enum LookupKind
{
    /// <summary>"Artist - Title".</summary>
    ArtistTitle,

    /// <summary>Free text with no separator.</summary>
    FreeText,

    /// <summary>A MusicBrainz recording MBID, or a link to one.</summary>
    MbRecordingId,

    /// <summary>An ISRC.</summary>
    Isrc,

    /// <summary>A Deezer track id, or a link to one.</summary>
    DeezerTrackId,

    /// <summary>Something Wondarr cannot look up yet.</summary>
    Unsupported,
}

/// <summary>
/// One parsed lookup: what the user typed, and the ids or the artist and title that came out of it.
/// </summary>
/// <param name="Kind">What the input turned out to be.</param>
/// <param name="Raw">The trimmed input, echoed back so callers can quote it in a reason.</param>
/// <param name="Artist">The artist, for <see cref="LookupKind.ArtistTitle"/>.</param>
/// <param name="Title">The title, for <see cref="LookupKind.ArtistTitle"/>.</param>
/// <param name="MbRecordingId">The recording MBID, lower-case, for <see cref="LookupKind.MbRecordingId"/>.</param>
/// <param name="Isrc">The ISRC, upper-case, for <see cref="LookupKind.Isrc"/>.</param>
/// <param name="DeezerTrackId">The Deezer track id, for <see cref="LookupKind.DeezerTrackId"/>.</param>
/// <param name="UnsupportedReason">Why the input cannot be looked up, for <see cref="LookupKind.Unsupported"/>.</param>
public sealed record LookupInput(
    LookupKind Kind,
    string Raw,
    string? Artist,
    string? Title,
    string? MbRecordingId,
    string? Isrc,
    long? DeezerTrackId,
    string? UnsupportedReason)
{
    /// <summary>An MBID, with or without dashes, as MusicBrainz writes it.</summary>
    private static readonly Regex MbRecordingUrlRegex = new(
        @"^https?://(?:beta\.)?musicbrainz\.org/recording/(?<id>[0-9a-fA-F-]{36})(?:[/?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A Deezer track link, with or without a locale segment.</summary>
    private static readonly Regex DeezerUrlRegex = new(
        @"^https?://(?:www\.)?deezer\.com/(?:[a-z]{2}/)?track/(?<id>\d+)(?:[/?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A Deezer track id in its short form.</summary>
    private static readonly Regex DeezerUriRegex = new(
        @"^deezer:track:(?<id>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An ISRC: two letters, three alphanumerics, seven digits.</summary>
    private static readonly Regex IsrcRegex = new(
        @"^[A-Za-z]{2}[A-Za-z0-9]{3}\d{7}$",
        RegexOptions.CultureInvariant);

    /// <summary>A Spotify link or URI, which arrives through the Phase 6 CSV export instead.</summary>
    private static readonly Regex SpotifyRegex = new(
        @"^(?:https?://open\.spotify\.com/|spotify:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A YouTube link or URI, which arrives with the Phase 4 YouTube source.</summary>
    private static readonly Regex YouTubeRegex = new(
        @"^https?://(?:(?:www|music)\.)?(?:youtube\.com|youtu\.be)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Any other absolute URL.</summary>
    private static readonly Regex UrlRegex = new(
        @"^[a-z][a-z0-9+.\-]*://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The separators that split "Artist - Title": space, dash, space.</summary>
    private static readonly string[] TitleSeparators = [" - ", " – ", " — "];

    /// <summary>How long every separator in <see cref="TitleSeparators"/> is.</summary>
    private const int TitleSeparatorLength = 3;

    /// <summary>Parses whatever the user typed or pasted.</summary>
    /// <param name="raw">The raw input.</param>
    /// <returns>The lookup kind, and whatever ids or text came with it.</returns>
    public static LookupInput Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var text = raw.Trim();

        if (text.Length == 0)
        {
            return Unsupported(string.Empty, "Nothing to look up");
        }

        var url = MbRecordingUrlRegex.Match(text);
        if (url.Success)
        {
            return Recording(url.Groups["id"].Value);
        }

        if (Guid.TryParse(text, out var guid))
        {
            return Recording(guid.ToString("D"));
        }

        var isrc = text.Replace("-", string.Empty, StringComparison.Ordinal);
        if (IsrcRegex.IsMatch(isrc))
        {
            return new LookupInput(LookupKind.Isrc, text, null, null, null, isrc.ToUpperInvariant(), null, null);
        }

        var deezerUrl = DeezerUrlRegex.Match(text);
        if (deezerUrl.Success)
        {
            return DeezerTrack(text, deezerUrl.Groups["id"].Value);
        }

        var deezerUri = DeezerUriRegex.Match(text);
        if (deezerUri.Success)
        {
            return DeezerTrack(text, deezerUri.Groups["id"].Value);
        }

        if (SpotifyRegex.IsMatch(text))
        {
            return Unsupported(text, "Spotify links are imported through CSV exports (Phase 6)");
        }

        if (YouTubeRegex.IsMatch(text))
        {
            return Unsupported(text, "YouTube links arrive with the YouTube source (Phase 4)");
        }

        if (UrlRegex.IsMatch(text))
        {
            return Unsupported(text, "Unsupported link");
        }

        var separator = FindSeparator(text);
        if (separator >= 0)
        {
            // Every separator is a single character between two spaces.
            var artist = text[..separator].Trim();
            var title = text[(separator + TitleSeparatorLength)..].Trim();

            if (artist.Length > 0 && title.Length > 0)
            {
                return new LookupInput(LookupKind.ArtistTitle, text, artist, title, null, null, null, null);
            }
        }

        return new LookupInput(LookupKind.FreeText, text, null, null, null, null, null, null);
    }

    /// <summary>Builds a recording lookup, lower-casing the id.</summary>
    private static LookupInput Recording(string id) =>
        new(LookupKind.MbRecordingId, id, null, null, id.ToLowerInvariant(), null, null, null);

    /// <summary>Builds a Deezer track lookup.</summary>
    private static LookupInput DeezerTrack(string text, string id) =>
        new(LookupKind.DeezerTrackId, text, null, null, null, null, long.Parse(id, CultureInfo.InvariantCulture), null);

    /// <summary>Builds an unsupported lookup.</summary>
    private static LookupInput Unsupported(string text, string reason) =>
        new(LookupKind.Unsupported, text, null, null, null, null, null, reason);

    /// <summary>The index of the earliest " - ", " – " or " — ", or -1.</summary>
    private static int FindSeparator(string text)
    {
        var earliest = -1;

        foreach (var separator in TitleSeparators)
        {
            var index = text.IndexOf(separator, StringComparison.Ordinal);
            if (index >= 0 && (earliest < 0 || index < earliest))
            {
                earliest = index;
            }
        }

        return earliest;
    }
}
