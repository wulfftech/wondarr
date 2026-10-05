using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// The <c>youtube</c> section of <c>config.yml</c>: the cookies and PO-token server the extractor
/// needs, and the <see cref="Ytdlp"/> sub-section that drives the runner.
/// </summary>
public sealed class YouTubeOptions
{
    /// <summary>
    /// A Netscape-format cookies file for an age-gated account, or <see langword="null"/>. The path is
    /// passed to yt-dlp; its contents are never read or logged by Wondarr.
    /// </summary>
    public string? CookiesPath { get; set; }

    /// <summary>
    /// The base URL of a bgutil PO-token server (for example <c>http://localhost:4416</c>), or
    /// <see langword="null"/>. Only the base URL is passed on; no query string is ever added.
    /// </summary>
    public string? PoTokenBaseUrl { get; set; }

    /// <summary>How yt-dlp is invoked: the <c>youtube.ytdlp</c> sub-section.</summary>
    public YtDlpOptions Ytdlp { get; set; } = new();
}

/// <summary>
/// The <c>youtube.ytdlp</c> section of <c>config.yml</c>: where the bundled yt-dlp is, how long one
/// download may run, and the pacing flags every download carries.
/// </summary>
public sealed class YtDlpOptions
{
    /// <summary>Path or name of the yt-dlp binary.</summary>
    public string BinaryPath { get; set; } = "yt-dlp";

    /// <summary>How long one download may run, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// yt-dlp's <c>--sleep-requests</c>: seconds between HTTP requests during extraction. yt-dlp's own
    /// <c>-t sleep</c> preset value.
    /// </summary>
    public double SleepRequestsSeconds { get; set; } = 0.75;

    /// <summary>yt-dlp's <c>--sleep-interval</c>: seconds before each download.</summary>
    public int SleepIntervalSeconds { get; set; } = 10;

    /// <summary>yt-dlp's <c>--max-sleep-interval</c>: the upper bound of the pre-download sleep.</summary>
    public int MaxSleepIntervalSeconds { get; set; } = 20;

    /// <summary>How many times yt-dlp itself retries a failed download.</summary>
    public int Retries { get; set; } = 5;

    /// <summary>
    /// How many downloads Wondarr runs at once. YouTube's tolerance holds this at 1; the validator
    /// rejects anything else.
    /// </summary>
    public int Concurrency { get; set; } = 1;
}

/// <summary>
/// Validates the <c>youtube.ytdlp</c> section, reached through the bound <see cref="YouTubeOptions"/>
/// type (the options pipeline only asks validators about the type it resolves). Every failure
/// message starts with the YAML key so the user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class YtDlpOptionsValidator : IValidateOptions<YouTubeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, YouTubeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ValidateYtdlp(options.Ytdlp);
    }

    /// <summary>Validates the <c>youtube.ytdlp</c> sub-section on its own, for direct tests.</summary>
    /// <param name="options">The sub-section to check.</param>
    /// <returns>The failures, or success.</returns>
    internal static ValidateOptionsResult ValidateYtdlp(YtDlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BinaryPath))
        {
            failures.Add($"youtube.ytdlp.binary_path: must name the executable (was '{options.BinaryPath}')");
        }

        if (options.TimeoutSeconds is < 30 or > 3600)
        {
            failures.Add($"youtube.ytdlp.timeout_seconds: must be between 30 and 3600 (was {options.TimeoutSeconds})");
        }

        if (options.SleepRequestsSeconds is < 0.1 or > 60)
        {
            failures.Add(
                $"youtube.ytdlp.sleep_requests_seconds: must be between 0.1 and 60 (was {options.SleepRequestsSeconds})");
        }

        if (options.SleepIntervalSeconds is < 0 or > 120)
        {
            failures.Add(
                $"youtube.ytdlp.sleep_interval_seconds: must be between 0 and 120 (was {options.SleepIntervalSeconds})");
        }

        if (options.MaxSleepIntervalSeconds is < 0 or > 120)
        {
            failures.Add(
                $"youtube.ytdlp.max_sleep_interval_seconds: must be between 0 and 120 (was {options.MaxSleepIntervalSeconds})");
        }

        if (options.MaxSleepIntervalSeconds < options.SleepIntervalSeconds)
        {
            failures.Add(
                "youtube.ytdlp.max_sleep_interval_seconds: must be at least sleep_interval_seconds "
                + $"(was {options.MaxSleepIntervalSeconds} < {options.SleepIntervalSeconds})");
        }

        if (options.Retries is < 0 or > 20)
        {
            failures.Add($"youtube.ytdlp.retries: must be between 0 and 20 (was {options.Retries})");
        }

        if (options.Concurrency != 1)
        {
            failures.Add(
                $"youtube.ytdlp.concurrency: must be 1 — YouTube's tolerance does not survive parallel "
                + $"downloads (was {options.Concurrency})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
