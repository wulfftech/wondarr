using System.Collections;
using Wondarr.Core.Configuration;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// What the YouTube settings page may read. Nothing here is a secret: the cookies path and the
/// PO-token URL are a path and a URL on the user's own machine, so they travel in full.
/// </summary>
/// <param name="Enabled">Whether the YouTube source runs at all (off by default, DECISIONS.md #6).</param>
/// <param name="CookiesPath">Path of the Netscape cookie file yt-dlp reads, or <see langword="null"/>.</param>
/// <param name="PoTokenBaseUrl">Base URL of the user's own bgutil PO-token provider, or <see langword="null"/>.</param>
/// <param name="AllowVideos">Whether <c>videos</c> results may be grabbed when the songs shelf found nothing.</param>
/// <param name="SearchLimit">How many searches one song may cost, the ISRC query included.</param>
/// <param name="OutputPolicy">The default output policy a library without one of its own uses.</param>
/// <param name="Ytdlp">The yt-dlp pacing flags every download carries.</param>
/// <param name="ReadOnlyFields">
/// camelCase names of the fields the environment sets through <c>APP__YOUTUBE__…</c>. The
/// environment wins over the file, so the UI shows these as read-only.
/// </param>
public sealed record YouTubeSettings(
    bool Enabled,
    string? CookiesPath,
    string? PoTokenBaseUrl,
    bool AllowVideos,
    int SearchLimit,
    OutputPolicy OutputPolicy,
    YtDlpPacingSettings Ytdlp,
    IReadOnlyList<string> ReadOnlyFields);

/// <summary>The yt-dlp pacing flags, as the settings page reads them.</summary>
/// <param name="SleepRequestsSeconds">Seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">Seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">The upper bound of the pre-download sleep.</param>
/// <param name="Retries">How many times yt-dlp itself retries a failed download.</param>
public sealed record YtDlpPacingSettings(
    double SleepRequestsSeconds,
    int SleepIntervalSeconds,
    int MaxSleepIntervalSeconds,
    int Retries);

/// <summary>
/// A change to the YouTube settings. Every field is optional; an absent field is left alone. A
/// cookies path or PO-token URL of an empty string clears it.
/// </summary>
/// <param name="Enabled">New state of the enable toggle.</param>
/// <param name="CookiesPath">New cookie-file path; an empty string clears it.</param>
/// <param name="PoTokenBaseUrl">New PO-token provider base URL; an empty string clears it.</param>
/// <param name="AllowVideos">New state of the videos rule.</param>
/// <param name="SearchLimit">New search budget.</param>
/// <param name="OutputPolicyJson">
/// The new default output policy, as the same JSON a library's <c>output_policy</c> column holds.
/// </param>
/// <param name="Ytdlp">The pacing flags to change; the absent ones are left alone.</param>
public sealed record YouTubeSettingsUpdate(
    bool? Enabled = null,
    string? CookiesPath = null,
    string? PoTokenBaseUrl = null,
    bool? AllowVideos = null,
    int? SearchLimit = null,
    string? OutputPolicyJson = null,
    YtDlpPacingUpdate? Ytdlp = null);

/// <summary>The yt-dlp pacing flags to change; an absent flag is left alone.</summary>
/// <param name="SleepRequestsSeconds">New seconds between HTTP requests during extraction.</param>
/// <param name="SleepIntervalSeconds">New seconds before each download.</param>
/// <param name="MaxSleepIntervalSeconds">New upper bound of the pre-download sleep.</param>
/// <param name="Retries">New retry count.</param>
public sealed record YtDlpPacingUpdate(
    double? SleepRequestsSeconds = null,
    int? SleepIntervalSeconds = null,
    int? MaxSleepIntervalSeconds = null,
    int? Retries = null);

/// <summary>The outcome of a settings change.</summary>
/// <param name="Success">Whether the change was written. On failure nothing was written.</param>
/// <param name="Errors">Why it was refused: a validation failure, or a field the environment owns.</param>
public sealed record YouTubeSettingsUpdateResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>
/// Reads and writes the <c>youtube</c> section of <c>config.yml</c> for the settings page, and the
/// default output policy a library without one of its own uses.
/// </summary>
public interface IYouTubeSettingsService
{
    /// <summary>Reads the current settings.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<YouTubeSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates a change and, when it is valid, writes only the changed keys to <c>config.yml</c>.
    /// The write reloads the configuration, so the provider and the runner see the new options at
    /// once — nothing has to be restarted.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<YouTubeSettingsUpdateResult> UpdateAsync(YouTubeSettingsUpdate update, CancellationToken cancellationToken);

    /// <summary>What the health probe last found, as the health checks report it.</summary>
    /// <param name="cancellationToken">Cancels the first probe.</param>
    Task<YtDlpHealthStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Runs the health probe now, whatever it answered before.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    Task<YtDlpHealthStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// A field the environment sets (<c>APP__YOUTUBE__…</c>) is refused rather than silently overridden:
/// the environment source is added after the YAML one, so a write to the file would be invisible to
/// the app and the user would see their change "not stick".
/// </para>
/// <para>
/// The cookies path and the PO-token URL are not secrets — they name a file and a service on the
/// user's own egress — but a log line carries the keys that were written, never their values, the
/// way every <c>config.yml</c> write is logged.
/// </para>
/// </remarks>
public sealed partial class YouTubeSettingsService : IYouTubeSettingsService
{
    /// <summary>Section of <c>config.yml</c> these settings live in.</summary>
    public const string Section = "youtube";

    /// <summary>The <c>setting</c> row the default output policy is stored under.</summary>
    public const string OutputPolicyDefaultKey = "youtube.output_policy_default";

    /// <summary>The environment variables the YouTube settings are read from.</summary>
    private const string EnvironmentPrefix = "APP__YOUTUBE__";

    /// <summary>
    /// Every field this page owns: its <c>config.yml</c> key (a nested one uses the configuration
    /// separator), the environment variable's suffix and the camelCase name the API and the UI use.
    /// </summary>
    private static readonly Field[] Fields =
    [
        new("enabled", "ENABLED"),
        new("cookies_path", "COOKIES_PATH"),
        new("po_token_base_url", "PO_TOKEN_BASE_URL"),
        new("allow_videos", "ALLOW_VIDEOS"),
        new("search_limit", "SEARCH_LIMIT"),
        new("ytdlp:sleep_requests_seconds", "YTDLP__SLEEP_REQUESTS_SECONDS"),
        new("ytdlp:sleep_interval_seconds", "YTDLP__SLEEP_INTERVAL_SECONDS"),
        new("ytdlp:max_sleep_interval_seconds", "YTDLP__MAX_SLEEP_INTERVAL_SECONDS"),
        new("ytdlp:retries", "YTDLP__RETRIES"),
    ];

    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly IReadOnlyCollection<IValidateOptions<YouTubeOptions>> _validators;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IProcessRunner _runner;
    private readonly YtDlpAvailability _availability;
    private readonly ILogger<YouTubeSettingsService> _logger;
    private readonly ILogger<YtDlpAvailability> _availabilityLogger;

    /// <summary>The <c>config.yml</c> keys the environment owns, normalised as the binder sees them.</summary>
    private readonly IReadOnlySet<string> _locked;

    /// <summary>Initialises a new instance of the <see cref="YouTubeSettingsService"/> class.</summary>
    /// <param name="options">The live YouTube options, which already include the environment.</param>
    /// <param name="writer">Writes the changed keys back to <c>config.yml</c>.</param>
    /// <param name="validators">The options validators, so a change is judged by the same rules as start-up.</param>
    /// <param name="settingsRepository">Reads and writes the default output policy.</param>
    /// <param name="runner">Runs the binaries without a shell, for the probe.</param>
    /// <param name="availability">The process-lifetime probe the health checks share.</param>
    /// <param name="environment">The process environment, used to find the fields the environment owns.</param>
    /// <param name="logger">Receives a Debug line naming the keys that were written.</param>
    /// <param name="availabilityLogger">The probe's own logger, for the one-off probe a Test runs.</param>
    public YouTubeSettingsService(
        IOptionsMonitor<YouTubeOptions> options,
        IConfigFileWriter writer,
        IEnumerable<IValidateOptions<YouTubeOptions>> validators,
        ISettingsRepository settingsRepository,
        IProcessRunner runner,
        YtDlpAvailability availability,
        IDictionary environment,
        ILogger<YouTubeSettingsService> logger,
        ILogger<YtDlpAvailability> availabilityLogger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(validators);
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(availabilityLogger);

        _options = options;
        _writer = writer;
        _validators = [.. validators];
        _settingsRepository = settingsRepository;
        _runner = runner;
        _availability = availability;
        _logger = logger;
        _availabilityLogger = availabilityLogger;
        _locked = EnvironmentOverrides.SectionKeys(environment, Section);
    }

    /// <inheritdoc />
    public async Task<YouTubeSettings> GetAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        return new YouTubeSettings(
            options.Enabled,
            options.CookiesPath,
            options.PoTokenBaseUrl,
            options.AllowVideos,
            options.SearchLimit,
            await OutputPolicyAsync(cancellationToken).ConfigureAwait(false),
            new YtDlpPacingSettings(
                options.Ytdlp.SleepRequestsSeconds,
                options.Ytdlp.SleepIntervalSeconds,
                options.Ytdlp.MaxSleepIntervalSeconds,
                options.Ytdlp.Retries),
            [.. Fields.Where(field => IsLocked(field.Key)).Select(field => CamelCase(field.Key))]);
    }

    /// <inheritdoc />
    public async Task<YouTubeSettingsUpdateResult> UpdateAsync(
        YouTubeSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = _options.CurrentValue;
        var candidate = new YouTubeOptions
        {
            Enabled = current.Enabled,
            CookiesPath = current.CookiesPath,
            PoTokenBaseUrl = current.PoTokenBaseUrl,
            AllowVideos = current.AllowVideos,
            SearchLimit = current.SearchLimit,
            BaseUrl = current.BaseUrl,
            DownloadsDir = current.DownloadsDir,
            Ytdlp = new YtDlpOptions
            {
                BinaryPath = current.Ytdlp.BinaryPath,
                TimeoutSeconds = current.Ytdlp.TimeoutSeconds,
                SleepRequestsSeconds = current.Ytdlp.SleepRequestsSeconds,
                SleepIntervalSeconds = current.Ytdlp.SleepIntervalSeconds,
                MaxSleepIntervalSeconds = current.Ytdlp.MaxSleepIntervalSeconds,
                Retries = current.Ytdlp.Retries,
                Concurrency = current.Ytdlp.Concurrency,
            },
        };

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? outputPolicy = null;
        var errors = new List<string>();

        Apply(update, current, candidate, changes, errors, ref outputPolicy);

        if (errors.Count > 0)
        {
            return new YouTubeSettingsUpdateResult(false, errors);
        }

        // The same rules as start-up: a change that the app would refuse to boot with is refused here.
        foreach (var validator in _validators)
        {
            var validation = validator.Validate(Options.DefaultName, candidate);

            if (validation.Failed)
            {
                return new YouTubeSettingsUpdateResult(false, [.. validation.Failures ?? []]);
            }
        }

        if (changes.Count > 0)
        {
            await _writer.UpdateSectionAsync(Section, changes, cancellationToken).ConfigureAwait(false);

            // Keys only: the section names a cookie file and a service on the user's network, and
            // every config.yml write is logged the same way.
            var keys = string.Join(", ", changes.Keys);
            LogUpdated(_logger, keys);
        }

        if (outputPolicy is not null)
        {
            // The default policy is a setting row, not a config.yml key: it is the same JSON a
            // library's output_policy column holds, and the library API reads it the same way.
            await _settingsRepository
                .SetAsync(OutputPolicyDefaultKey, outputPolicy, cancellationToken)
                .ConfigureAwait(false);

            LogUpdated(_logger, OutputPolicyDefaultKey);
        }

        return new YouTubeSettingsUpdateResult(true, []);
    }

    /// <inheritdoc />
    public Task<YtDlpHealthStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        _availability.GetStatusAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<YtDlpHealthStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        // A probe of one's own: the shared availability caches its answer for the process lifetime,
        // and a Test is asked precisely because the user wants to know how it answers now.
        using var probe = new YtDlpAvailability(_runner, _options, _availabilityLogger);

        return await probe.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The default output policy, or the built-in one when no row is stored.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task<OutputPolicy> OutputPolicyAsync(CancellationToken cancellationToken)
    {
        var json = await _settingsRepository
            .GetAsync<string>(OutputPolicyDefaultKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return OutputPolicy.Default;
        }

        try
        {
            return OutputPolicy.Parse(json);
        }
        catch (ProfileValidationException)
        {
            // A policy that no longer parses degrades to the default rather than failing the read,
            // the way a library row with the same problem does.
            return OutputPolicy.Default;
        }
    }

    /// <summary>
    /// Copies the supplied fields into <paramref name="candidate"/> and records the ones that
    /// actually differ. A read-only field is an error only when the value really changes: the UI
    /// posts the whole form back, so an untouched read-only field must not make saving impossible.
    /// </summary>
    /// <param name="outputPolicy">
    /// The default policy to store, normalised to the JSON the setting row holds, or
    /// <see langword="null"/> when the request did not carry one.
    /// </param>
    private void Apply(
        YouTubeSettingsUpdate update,
        YouTubeOptions current,
        YouTubeOptions candidate,
        Dictionary<string, object?> changes,
        List<string> errors,
        ref string? outputPolicy)
    {
        ApplyFlag(update.Enabled, "enabled", current.Enabled, changes, errors, value => candidate.Enabled = value);
        ApplyFlag(update.AllowVideos, "allow_videos", current.AllowVideos, changes, errors, value => candidate.AllowVideos = value);
        ApplyNumber(update.SearchLimit, "search_limit", current.SearchLimit, changes, errors, value => candidate.SearchLimit = value);

        // Null means "keep what is stored"; an empty string means "clear it". Either way the value
        // stops here: it is written to config.yml and never logged.
        ApplyText(update.CookiesPath, "cookies_path", current.CookiesPath, changes, errors, value => candidate.CookiesPath = value);
        ApplyText(update.PoTokenBaseUrl, "po_token_base_url", current.PoTokenBaseUrl, changes, errors, value => candidate.PoTokenBaseUrl = value);

        if (update.Ytdlp is { } pacing)
        {
            ApplyNumber(
                pacing.SleepRequestsSeconds,
                "ytdlp:sleep_requests_seconds",
                current.Ytdlp.SleepRequestsSeconds,
                changes,
                errors,
                value => candidate.Ytdlp.SleepRequestsSeconds = value);
            ApplyNumber(
                pacing.SleepIntervalSeconds,
                "ytdlp:sleep_interval_seconds",
                current.Ytdlp.SleepIntervalSeconds,
                changes,
                errors,
                value => candidate.Ytdlp.SleepIntervalSeconds = value);
            ApplyNumber(
                pacing.MaxSleepIntervalSeconds,
                "ytdlp:max_sleep_interval_seconds",
                current.Ytdlp.MaxSleepIntervalSeconds,
                changes,
                errors,
                value => candidate.Ytdlp.MaxSleepIntervalSeconds = value);
            ApplyNumber(
                pacing.Retries,
                "ytdlp:retries",
                current.Ytdlp.Retries,
                changes,
                errors,
                value => candidate.Ytdlp.Retries = value);
        }

        if (update.OutputPolicyJson is not { } json)
        {
            return;
        }

        try
        {
            // Parsed rather than trusted: the same rules a library's policy is judged by, and the
            // same JSON shape, so the two forms cannot drift apart.
            outputPolicy = OutputPolicy.Parse(json).ToJson();
        }
        catch (ProfileValidationException exception)
        {
            errors.AddRange(exception.Errors.Select(error => $"{error.Property}: {error.Message}"));
        }
    }

    /// <summary>Records a numeric change, unless it is absent, unchanged or owned by the environment.</summary>
    private void ApplyNumber<T>(
        T? supplied,
        string key,
        T current,
        Dictionary<string, object?> changes,
        List<string> errors,
        Action<T> write)
        where T : struct
    {
        if (supplied is not { } value || EqualityComparer<T>.Default.Equals(value, current) || Refuse(key, errors))
        {
            return;
        }

        write(value);
        changes[key] = value;
    }

    /// <summary>Records a boolean change, unless it is absent, unchanged or owned by the environment.</summary>
    private void ApplyFlag(
        bool? supplied,
        string key,
        bool current,
        Dictionary<string, object?> changes,
        List<string> errors,
        Action<bool> write)
    {
        if (supplied is not { } value || value == current || Refuse(key, errors))
        {
            return;
        }

        write(value);
        changes[key] = value;
    }

    /// <summary>
    /// Records a text change, unless it is absent, unchanged or owned by the environment. An empty
    /// string clears the value.
    /// </summary>
    private void ApplyText(
        string? supplied,
        string key,
        string? current,
        Dictionary<string, object?> changes,
        List<string> errors,
        Action<string?> write)
    {
        if (supplied is null || Refuse(key, errors))
        {
            return;
        }

        var replacement = supplied.Length == 0 ? null : supplied;

        if (string.Equals(replacement, current, StringComparison.Ordinal))
        {
            return;
        }

        write(replacement);
        changes[key] = replacement;
    }

    /// <summary>Refuses a change to a field the environment owns, naming the variable that sets it.</summary>
    /// <returns><see langword="true"/> when the change was refused.</returns>
    private bool Refuse(string key, List<string> errors)
    {
        if (!IsLocked(key))
        {
            return false;
        }

        var field = Fields.First(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));

        errors.Add(
            $"{CamelCase(key)} is set by the environment variable {EnvironmentPrefix}{field.EnvironmentKey} "
            + "and cannot be changed here");

        return true;
    }

    private bool IsLocked(string key) => _locked.Contains(
        key.Replace("_", string.Empty, StringComparison.Ordinal).Replace('.', ':'));

    /// <summary>Turns a <c>config.yml</c> key into the camelCase name the API and the UI use.</summary>
    private static string CamelCase(string key)
    {
        var parts = key.Split('_', '.');

        if (parts.Length == 1)
        {
            return parts[0];
        }

        var name = parts[0];

        for (var index = 1; index < parts.Length; index++)
        {
            name += char.ToUpperInvariant(parts[index][0]) + parts[index][1..];
        }

        return name;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the YouTube settings ({Keys})")]
    private static partial void LogUpdated(ILogger logger, string keys);

    /// <summary>One editable field: its <c>config.yml</c> key and the environment variable that owns it.</summary>
    private sealed record Field(string Key, string EnvironmentKey);
}
