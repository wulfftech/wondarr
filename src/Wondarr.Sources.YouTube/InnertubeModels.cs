namespace Wondarr.Sources.YouTube;

/// <summary>Which InnerTube search shelf a request asks for (the <c>params</c> protobuf ytmusicapi's filters map to).</summary>
public enum InnertubeSearchFilter
{
    /// <summary>The songs shelf: the catalogue's Art Tracks, the only results a grab may use by default.</summary>
    Songs,

    /// <summary>The videos shelf: official videos and user uploads, only when the source rule allows them.</summary>
    Videos,

    /// <summary>No filter at all: the ISRC query's shape, because the songs filter on an ISRC returns junk.</summary>
    None,
}

/// <summary>One search to send: the text and the shelf it asks for.</summary>
/// <param name="Query">The search text (an ISRC, or "artist title").</param>
/// <param name="Filter">The shelf to ask for.</param>
public sealed record InnertubeSearchRequest(string Query, InnertubeSearchFilter Filter)
{
    /// <summary>The songs-filter <c>params</c> protobuf, as ytmusicapi's <c>parsers/search.py</c> builds it.</summary>
    public const string SongsParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    /// <summary>The videos-filter <c>params</c> protobuf.</summary>
    public const string VideosParams = "EgWKAQIQAWoMEA4QChADEAQQCRAF";

    /// <summary>The <c>params</c> value this filter maps to; null when the request must go out unfiltered.</summary>
    public string? Params => Filter switch
    {
        InnertubeSearchFilter.Songs => SongsParams,
        InnertubeSearchFilter.Videos => VideosParams,
        _ => null,
    };
}

/// <summary>One parsed InnerTube search result: a catalogue song, a video or the top-result card.</summary>
/// <param name="VideoId">The 11-character YouTube video id — the only thing a grab needs.</param>
/// <param name="Title">The title as the result shows it, version hints and all.</param>
/// <param name="Artists">The credited artists; a user upload's channel name is its only artist.</param>
/// <param name="Album">The album the result links to, when it is a catalogue song.</param>
/// <param name="DurationMs">The duration the result shows, in milliseconds; null when it shows none.</param>
/// <param name="MusicVideoType">The raw <c>musicVideoType</c> (<see cref="InnertubeVideoTypes.ArtTrack"/>, …); null when the result carries none.</param>
/// <param name="IsExplicit">Whether the result carries the explicit badge.</param>
public sealed record InnertubeResult(
    string VideoId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    int? DurationMs,
    string? MusicVideoType,
    bool IsExplicit);

/// <summary>What one InnerTube search returned.</summary>
/// <param name="Query">The search text, as sent.</param>
/// <param name="Filter">The shelf that was asked for.</param>
/// <param name="TopResult">The <c>musicCardShelfRenderer</c> top result, when the response had one — an ISRC query's answer.</param>
/// <param name="Results">The shelf items, in response order.</param>
public sealed record InnertubeSearchResult(
    string Query,
    InnertubeSearchFilter Filter,
    InnertubeResult? TopResult,
    IReadOnlyList<InnertubeResult> Results)
{
    /// <summary>A response with neither a card nor shelf items.</summary>
    public static InnertubeSearchResult Empty(string query, InnertubeSearchFilter filter) =>
        new(query, filter, null, []);
}

/// <summary>What a result is, once its raw <c>musicVideoType</c> is classified.</summary>
public enum InnertubeVideoKind
{
    /// <summary>An Art Track: the catalogue's own upload of the song, the only kind grabbed by default.</summary>
    ArtTrack,

    /// <summary>An official music video.</summary>
    OfficialVideo,

    /// <summary>A user upload, and everything unlisted: not grabbable by default.</summary>
    UserUpload,
}

/// <summary>The <c>musicVideoType</c> values InnerTube sends, and what they mean for a grab.</summary>
public static class InnertubeVideoTypes
{
    /// <summary>An Art Track: the label's own upload — the song's audio over a static cover.</summary>
    public const string ArtTrack = "MUSIC_VIDEO_TYPE_ATV";

    /// <summary>An official music video.</summary>
    public const string OfficialVideo = "MUSIC_VIDEO_TYPE_OMV";

    /// <summary>A user upload.</summary>
    public const string UserUpload = "MUSIC_VIDEO_TYPE_UGC";

    /// <summary>
    /// Classifies a raw <c>musicVideoType</c>. Anything unlisted (MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK,
    /// MUSIC_VIDEO_TYPE_PODCAST_EPISODE) counts as a user upload: not grabbable by default.
    /// </summary>
    public static InnertubeVideoKind KindOf(string? musicVideoType) => musicVideoType switch
    {
        ArtTrack => InnertubeVideoKind.ArtTrack,
        OfficialVideo => InnertubeVideoKind.OfficialVideo,
        _ => InnertubeVideoKind.UserUpload,
    };
}
