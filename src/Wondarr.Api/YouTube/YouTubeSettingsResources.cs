using System.Text.Json;
using Wondarr.Sources.YouTube;

namespace Wondarr.Api.YouTube;

/// <summary>
/// The YouTube settings as the settings page reads them. Nothing here is a secret: the cookies path
/// and the PO-token URL are a path and a URL on the user's own machine, so they travel in full.
/// </summary>
/// <param name="Enabled">Whether the YouTube source runs at all (off by default: YouTube's terms prohibit automated access, so the user decides).</param>
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
                settings.Ytdlp.Retries,
                settings.Ytdlp.AutoUpdate,
                settings.Ytdlp.UpdateChannel),
            [.. settings.ReadOnlyFields]);
    }

    /// <summary>Parses the JSON the policy serialises to; it is generated here, so it always parses.</summary>
    private static JsonElement ParsePolicy(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}

/// <summary>The yt-dlp pacing flags and update settings, as the settings page reads them.</summary>
/// <param name="SleepRequestsSeconds">Seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">Seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">The upper bound of the pre-download sleep.</param>
/// <param name="Retries">How many times yt-dlp itself retries a failed download.</param>
/// <param name="AutoUpdate">Whether Wondarr keeps its own copy of yt-dlp current.</param>
/// <param name="UpdateChannel">The release channel the update follows: <c>stable</c> or <c>nightly</c>.</param>
public sealed record YouTubeYtdlpPacingResource(
    double SleepRequestsSeconds,
    int SleepIntervalSeconds,
    int MaxSleepIntervalSeconds,
    int Retries,
    bool AutoUpdate,
    string UpdateChannel);

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
                Ytdlp.Retries,
                Ytdlp.AutoUpdate,
                Ytdlp.UpdateChannel));
}

/// <summary>The yt-dlp pacing flags and update settings to change; an absent one is left alone.</summary>
/// <param name="SleepRequestsSeconds">New seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">New seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">New upper bound of the pre-download sleep.</param>
/// <param name="Retries">New retry count.</param>
/// <param name="AutoUpdate">New state of the automatic-update switch.</param>
/// <param name="UpdateChannel">New update channel: <c>stable</c> or <c>nightly</c>.</param>
public sealed record YouTubeYtdlpPacingUpdateResource(
    double? SleepRequestsSeconds = null,
    int? SleepIntervalSeconds = null,
    int? MaxSleepIntervalSeconds = null,
    int? Retries = null,
    bool? AutoUpdate = null,
    string? UpdateChannel = null);

/// <summary>What the yt-dlp health probe found, and how the copy in use is kept current, as the settings page shows it.</summary>
/// <param name="Version">The version <c>yt-dlp --version</c> printed, or <c>null</c> when it did not answer.</param>
/// <param name="HasJsRuntime">Whether the Deno runtime (the EJS interpreter yt-dlp needs) is runnable.</param>
/// <param name="BinaryAvailable">Whether the yt-dlp binary answered at all.</param>
/// <param name="Source">Which yt-dlp runs: <c>managed</c> (Wondarr's own copy, kept current), <c>bundled</c> (the image's) or <c>custom</c> (a path the user set).</param>
/// <param name="BundledVersion">The version of the yt-dlp the image bundles, when known.</param>
/// <param name="AutoUpdate">Whether automatic updates are on.</param>
/// <param name="UpdateChannel">The channel the update follows: <c>stable</c> or <c>nightly</c>.</param>
/// <param name="UpdateActive">Whether the update would do anything now: it is on, the path is not custom, and the YouTube source or music videos is on.</param>
/// <param name="LastCheckedAt">When the update last checked, or <c>null</c> since Wondarr started.</param>
/// <param name="LastCheckSucceeded">Whether that check finished, or <c>null</c> when there was none.</param>
/// <param name="LastCheckMessage">What that check found, in a sentence.</param>
/// <param name="LatestVersion">The newest yt-dlp release the last successful check saw.</param>
/// <param name="JsRuntimeWarning">yt-dlp's complaint that the JS runtime (Deno) is unsupported or too old, when it made one.</param>
/// <param name="FallbackReason">Why the updated copy is not in use, when it is broken and the bundled one runs instead.</param>
public sealed record YouTubeStatusResource(
    string? Version,
    bool HasJsRuntime,
    bool BinaryAvailable,
    string Source,
    string? BundledVersion,
    bool AutoUpdate,
    string UpdateChannel,
    bool UpdateActive,
    DateTimeOffset? LastCheckedAt,
    bool? LastCheckSucceeded,
    string? LastCheckMessage,
    string? LatestVersion,
    string? JsRuntimeWarning,
    string? FallbackReason)
{
    /// <summary>Maps the probe's answer and the update's state onto the resource.</summary>
    public static YouTubeStatusResource From(YtDlpHealthStatus status, YtDlpUpdateStatus update)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(update);

        var source = update.Source switch
        {
            YtDlpBinarySource.Managed => "managed",
            YtDlpBinarySource.Custom => "custom",
            _ => "bundled",
        };

        // When the bundled binary is the one that runs, its version is the probe's.
        var bundledVersion = update.BundledVersion ?? (update.Source == YtDlpBinarySource.Bundled ? status.Version : null);

        return new YouTubeStatusResource(
            status.Version,
            status.HasJsRuntime,
            status.BinaryAvailable,
            source,
            bundledVersion,
            update.AutoUpdate,
            update.UpdateChannel,
            update.UpdateActive,
            update.LastCheckedAt,
            update.LastCheckSucceeded,
            update.LastCheckMessage,
            update.LatestVersion,
            update.JsRuntimeWarning,
            update.FallbackReason);
    }
}
