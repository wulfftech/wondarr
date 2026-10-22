using System.Text.RegularExpressions;
using System.Web;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Reads the video id out of a link a person pasted, and writes the one link Wondarr uses for it. Only
/// YouTube's own hosts are read, and only the id travels on: yt-dlp is never handed the pasted text, so a
/// link to anything else cannot make it fetch that.
/// </summary>
public static partial class YouTubeVideoLinks
{
    /// <summary>The hosts a watch, shorts, live or embed link may be on.</summary>
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
    };

    /// <summary>The paths whose second segment is the video id.</summary>
    private static readonly HashSet<string> IdPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "shorts",
        "live",
        "embed",
    };

    /// <summary>
    /// Reads the video id from a link: <c>youtube.com/watch?v=</c> (also on <c>m.</c> and <c>music.</c>),
    /// <c>youtu.be/</c>, <c>/shorts/</c>, <c>/live/</c> and <c>/embed/</c>, with or without the scheme.
    /// </summary>
    /// <param name="text">What the person pasted.</param>
    /// <param name="videoId">The 11-character video id, or an empty string.</param>
    /// <returns>Whether <paramref name="text"/> is a link to one YouTube video.</returns>
    public static bool TryParse(string? text, out string videoId)
    {
        videoId = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = string.Concat("https://", trimmed);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? candidate = null;

        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            candidate = segments.Length == 1 ? segments[0] : null;
        }
        else if (YouTubeHosts.Contains(uri.Host))
        {
            if (segments is ["watch"])
            {
                candidate = HttpUtility.ParseQueryString(uri.Query)["v"];
            }
            else if (segments.Length == 2 && IdPaths.Contains(segments[0]))
            {
                candidate = segments[1];
            }
        }

        if (candidate is null || !VideoId().IsMatch(candidate))
        {
            return false;
        }

        videoId = candidate;

        return true;
    }

    /// <summary>The watch link for a video id, the form MusicBrainz and everything else expect.</summary>
    /// <param name="videoId">The 11-character video id.</param>
    public static string WatchUrl(string videoId) => string.Concat("https://www.youtube.com/watch?v=", videoId);

    /// <summary>
    /// A YouTube video id: eleven URL-safe base64 characters and nothing after them (<c>\z</c>, because
    /// <c>$</c> also matches before a final line break, which a decoded <c>%0A</c> would be).
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VideoId();
}
