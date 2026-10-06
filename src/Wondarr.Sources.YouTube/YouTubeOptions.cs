using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>The <c>youtube</c> section of <c>config.yml</c>.</summary>
public sealed class YouTubeOptions
{
    /// <summary>
    /// Whether the YouTube source runs at all. Off by default (DECISIONS.md #6): enabling it in
    /// Settings shows the one-time ToS warning, and no cookies or accounts are bundled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to a Netscape cookie file yt-dlp reads, for age-gated videos and the bot check. Optional:
    /// without it, everything that needs an account is rejected at grab time.
    /// </summary>
    public string? CookiesPath { get; set; }

    /// <summary>
    /// Base URL of the user's own bgutil PO-token provider (<c>http://host:4416</c>), needed for
    /// downloads when YouTube asks for a proof of origin. Optional; the search never needs it.
    /// </summary>
    public string? PoTokenBaseUrl { get; set; }

    /// <summary>Whether <c>videos</c> results may be grabbed when the songs shelf found nothing.</summary>
    public bool AllowVideos { get; set; }

    /// <summary>How many searches one song may cost, the ISRC query included.</summary>
    public int SearchLimit { get; set; } = 20;

    /// <summary>Where InnerTube lives. The real host by default; a test points it at its own stub.</summary>
    public string BaseUrl { get; set; } = "https://music.youtube.com/";

    /// <summary>How yt-dlp is invoked: the <c>youtube.ytdlp</c> sub-section.</summary>
    public YtDlpOptions Ytdlp { get; set; } = new();
}

/// <summary>
/// Validates <see cref="YouTubeOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class YouTubeOptionsValidator : IValidateOptions<YouTubeOptions>
{
    /// <summary>The widest search budget that makes sense: one ISRC query plus one per shelf.</summary>
    public const int MaxSearchLimit = 50;

    public ValidateOptionsResult Validate(string? name, YouTubeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.SearchLimit is < 1 or > MaxSearchLimit)
        {
            failures.Add($"youtube.search_limit: must be between 1 and {MaxSearchLimit} (was {options.SearchLimit})");
        }

        if (!IsHttpUrl(options.BaseUrl))
        {
            failures.Add($"youtube.base_url: must be an absolute http(s) URI (was {options.BaseUrl})");
        }

        if (!string.IsNullOrEmpty(options.PoTokenBaseUrl) && !IsHttpUrl(options.PoTokenBaseUrl))
        {
            failures.Add($"youtube.po_token_base_url: must be an absolute http(s) URI (was {options.PoTokenBaseUrl})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Whether the value is an absolute http or https URI.</summary>
    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
