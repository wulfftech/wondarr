// The error strings and their retry/blocklist split are derived from yt-dlp
// (https://github.com/yt-dlp/yt-dlp, Unlicense) and spotDL
// (https://github.com/spotDL/spotify-downloader, MIT), as re-verified in
// docs/research/research_youtube.md §0.1.

namespace Wondarr.Sources.YouTube;

/// <summary>Why a yt-dlp run failed, in the taxonomy the queue and blocklist share.</summary>
public enum YtDlpErrorKind
{
    /// <summary>YouTube asked the client to prove it is not a bot; usually clears on its own.</summary>
    BotCheck,

    /// <summary>YouTube throttled the client (HTTP 429/402 or the try-again-later page).</summary>
    RateLimited,

    /// <summary>The uploader blocked this country; no retry will change that.</summary>
    GeoRestricted,

    /// <summary>The video needs an account; the user's cookies are their own choice.</summary>
    AgeGated,

    /// <summary>The video is private, gone or never existed.</summary>
    PrivateOrUnavailable,

    /// <summary>The yt-dlp binary itself could not be started.</summary>
    ToolMissing,

    /// <summary>The run was killed by the configured timeout.</summary>
    TimedOut,

    /// <summary>Anything the taxonomy does not name; the message carries the detail.</summary>
    Unknown,
}

/// <summary>What the queue should do with a failed download.</summary>
public enum YtDlpErrorAction
{
    /// <summary>Put the item back on the queue and try again later.</summary>
    RetryLater,

    /// <summary>Never try this candidate again; record it on the blocklist.</summary>
    Blocklist,
}

/// <summary>
/// Maps a yt-dlp stderr to a <see cref="YtDlpErrorKind"/> and the kind to the action the queue takes.
/// The strings are matched case-insensitively against the whole stderr.
/// </summary>
public static class YtDlpErrorTaxonomy
{
    /// <summary>Classifies a failed run's stderr.</summary>
    /// <param name="standardError">Everything yt-dlp wrote to stderr.</param>
    /// <returns>The kind the stderr names, or <see cref="YtDlpErrorKind.Unknown"/>.</returns>
    public static YtDlpErrorKind Classify(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        if (Contains(standardError, "Sign in to confirm you're not a bot"))
        {
            return YtDlpErrorKind.BotCheck;
        }

        if (Contains(standardError, "This content isn't available, try again later")
            || Contains(standardError, "HTTP Error 429")
            || Contains(standardError, "HTTP Error 402"))
        {
            return YtDlpErrorKind.RateLimited;
        }

        if (Contains(standardError, "The uploader has not made this video available in your country"))
        {
            return YtDlpErrorKind.GeoRestricted;
        }

        if (Contains(standardError, "Login details are needed to download this content")
            || Contains(standardError, "age-restricted"))
        {
            return YtDlpErrorKind.AgeGated;
        }

        if (Contains(standardError, "This video is private")
            || Contains(standardError, "Video unavailable")
            || Contains(standardError, "This video does not exist"))
        {
            return YtDlpErrorKind.PrivateOrUnavailable;
        }

        return YtDlpErrorKind.Unknown;
    }

    /// <summary>The action the queue takes for a kind.</summary>
    /// <param name="kind">The classified failure.</param>
    /// <returns>
    /// <see cref="YtDlpErrorAction.RetryLater"/> for bot checks, rate limits, missing tools, timeouts
    /// and unknown failures; <see cref="YtDlpErrorAction.Blocklist"/> for geo blocks, age gates and
    /// private or deleted videos.
    /// </returns>
    public static YtDlpErrorAction ActionFor(YtDlpErrorKind kind) => kind switch
    {
        YtDlpErrorKind.GeoRestricted
        or YtDlpErrorKind.AgeGated
        or YtDlpErrorKind.PrivateOrUnavailable => YtDlpErrorAction.Blocklist,
        _ => YtDlpErrorAction.RetryLater,
    };

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
