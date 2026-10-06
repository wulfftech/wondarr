using System.Text.Json;
using Wondarr.Sources.YouTube;

namespace Wondarr.Api.YouTube;

/// <summary>
/// The YouTube settings as the settings page reads them. Nothing here is a secret: the cookies path
/// and the PO-token URL are a path and a URL on the user's own machine, so they travel in full.
/// </summary>
/// <param name="Enabled">Whether the YouTube source runs at all (off by default, DECISIONS.md #6).</param>
/// <param name="CookiesPath">Path of the Netscape cookie file yt-dlp reads, or <see langword="null"/>.</param>
/// <param name="PoTokenBaseUrl">Base URL of the user's own bgutil PO-token provider, or <see langword="null"/>.</param>
/// <param name="AllowVideos">Whether <c>videos</c> results may be grabbed when the songs shelf found nothing.</param>
/// <param name="SearchLimit">How many searches one song may cost, the ISRC query included.</param>
/// <param name="OutputPolicy">
/// The default output policy a library without one of its own uses, as the same JSON object a
/// library's <c>output_policy</c> carries.
/// </param>
/// <param name="Ytdlp">The yt-dlp pacing flags every download carries.</param>
/// <param name="ReadOnlyFields">
/// camelCase names of the fields the environment sets through <c>APP__YOUTUBE__…</c>; the UI shows
/// them as read-only because a change written to <c>config.yml</c> would be overridden.
/// </param>
public sealed record YouTubeSettingsResource(
    bool Enabled,
    string? CookiesPath,
    string? PoTokenBaseUrl,
    bool AllowVideos,
    int SearchLimit,
    JsonElement OutputPolicy,
    YouTubeYtdlpPacingResource Ytdlp,
    List<string> ReadOnlyFields)
{
    /// <summary>Maps the service's settings onto the resource.</summary>
    public static YouTubeSettingsResource From(YouTubeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new YouTubeSettingsResource(
            settings.Enabled,
            settings.CookiesPath,
            settings.PoTokenBaseUrl,
            settings.AllowVideos,
            settings.SearchLimit,
            ParsePolicy(settings.OutputPolicy.ToJson()),
            new YouTubeYtdlpPacingResource(
                settings.Ytdlp.SleepRequestsSeconds,
                settings.Ytdlp.SleepIntervalSeconds,
                settings.Ytdlp.MaxSleepIntervalSeconds,
                settings.Ytdlp.Retries),
            [.. settings.ReadOnlyFields]);
    }

    /// <summary>Parses the JSON the policy serialises to; it is generated here, so it always parses.</summary>
    private static JsonElement ParsePolicy(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}

/// <summary>The yt-dlp pacing flags, as the settings page reads them.</summary>
/// <param name="SleepRequestsSeconds">Seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">Seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">The upper bound of the pre-download sleep.</param>
/// <param name="Retries">How many times yt-dlp itself retries a failed download.</param>
public sealed record YouTubeYtdlpPacingResource(
    double SleepRequestsSeconds,
    int SleepIntervalSeconds,
    int MaxSleepIntervalSeconds,
    int Retries);

/// <summary>
/// A change to the YouTube settings. Every field is optional and an absent field is left alone; a
/// cookies path or PO-token URL of an empty string clears it.
/// </summary>
/// <param name="Enabled">New state of the enable toggle.</param>
/// <param name="CookiesPath">New cookie-file path; an empty string clears it.</param>
/// <param name="PoTokenBaseUrl">New PO-token provider base URL; an empty string clears it.</param>
/// <param name="AllowVideos">New state of the videos rule.</param>
/// <param name="SearchLimit">New search budget.</param>
/// <param name="OutputPolicy">The new default output policy, as a JSON object.</param>
/// <param name="Ytdlp">The pacing flags to change; the absent ones are left alone.</param>
public sealed record YouTubeSettingsUpdateResource(
    bool? Enabled = null,
    string? CookiesPath = null,
    string? PoTokenBaseUrl = null,
    bool? AllowVideos = null,
    int? SearchLimit = null,
    JsonElement? OutputPolicy = null,
    YouTubeYtdlpPacingUpdateResource? Ytdlp = null)
{
    /// <summary>Maps the request body onto the service's update.</summary>
    public YouTubeSettingsUpdate ToUpdate() => new(
        Enabled,
        CookiesPath,
        PoTokenBaseUrl,
        AllowVideos,
        SearchLimit,
        OutputPolicy is { } policy && policy.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? policy.GetRawText()
            : null,
        Ytdlp is null
            ? null
            : new YtDlpPacingUpdate(
                Ytdlp.SleepRequestsSeconds,
                Ytdlp.SleepIntervalSeconds,
                Ytdlp.MaxSleepIntervalSeconds,
                Ytdlp.Retries));
}

/// <summary>The yt-dlp pacing flags to change; an absent flag is left alone.</summary>
/// <param name="SleepRequestsSeconds">New seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">New seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">New upper bound of the pre-download sleep.</param>
/// <param name="Retries">New retry count.</param>
public sealed record YouTubeYtdlpPacingUpdateResource(
    double? SleepRequestsSeconds = null,
    int? SleepIntervalSeconds = null,
    int? MaxSleepIntervalSeconds = null,
    int? Retries = null);

/// <summary>What the yt-dlp health probe found, as the settings page shows it.</summary>
/// <param name="Version">The version <c>yt-dlp --version</c> printed, or <c>null</c> when it did not answer.</param>
/// <param name="HasJsRuntime">Whether the Deno runtime (the EJS interpreter yt-dlp needs) is runnable.</param>
/// <param name="BinaryAvailable">Whether the yt-dlp binary answered at all.</param>
public sealed record YouTubeStatusResource(string? Version, bool HasJsRuntime, bool BinaryAvailable)
{
    /// <summary>Maps the probe's answer onto the resource.</summary>
    public static YouTubeStatusResource From(YtDlpHealthStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new YouTubeStatusResource(status.Version, status.HasJsRuntime, status.BinaryAvailable);
    }
}
