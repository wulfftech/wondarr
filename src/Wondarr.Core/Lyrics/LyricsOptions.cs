using Microsoft.Extensions.Options;

namespace Wondarr.Core.Lyrics;

/// <summary>
/// The <c>lyrics</c> section of <c>config.yml</c>: whether Wondarr asks LRCLIB for lyrics at all, which
/// host it asks, and how politely.
/// </summary>
/// <remarks>
/// Lyrics are a nicety: nothing in here can make an import fail. Switching <see cref="Enabled"/> off
/// only means a file is filed without lyrics, and a slow or throttled LRCLIB costs one bounded wait.
/// </remarks>
public sealed class LyricsOptions
{
    /// <summary>Whether lyrics are looked up at all when a song is filed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the LRCLIB API.</summary>
    public string BaseUrl { get; set; } = "https://lrclib.net/";

    /// <summary>
    /// How long one LRCLIB request is spaced from the next, in milliseconds. LRCLIB's own advice for
    /// library-sized work is sequential requests with 200–500 ms gaps.
    /// </summary>
    public int RequestIntervalMs { get; set; } = 500;

    /// <summary>
    /// How long one LRCLIB request may take before it is given up on. Bounds what a slow service can
    /// cost an import; the import is never failed by the wait.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Validates <see cref="LyricsOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class LyricsOptionsValidator : IValidateOptions<LyricsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LyricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"lyrics.base_url: must be an absolute http or https URL (was '{options.BaseUrl}')");
        }

        if (options.RequestIntervalMs is < 200 or > 5000)
        {
            failures.Add(
                $"lyrics.request_interval_ms: must be between 200 and 5000, which is LRCLIB's own advice "
                + $"for sequential requests (was {options.RequestIntervalMs})");
        }

        if (options.TimeoutSeconds is < 2 or > 60)
        {
            failures.Add(
                $"lyrics.timeout_seconds: must be between 2 and 60 (was {options.TimeoutSeconds})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
