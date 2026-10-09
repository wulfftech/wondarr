using System.Collections;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.LastFm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Metadata;

/// <summary>A metadata service whose key the settings page manages.</summary>
public enum MetadataKeyService
{
    /// <summary>AcoustID, keyed by <c>acoustid.client_key</c>.</summary>
    AcoustId,

    /// <summary>Last.fm, keyed by <c>lastfm.api_key</c>.</summary>
    LastFm,
}

/// <summary>
/// Which of the optional metadata keys are set. A key itself is never part of this: a settings page
/// that can read a key back would put it in every response, log and browser cache on the way.
/// </summary>
/// <param name="AcoustIdKeySet">Whether <c>acoustid.client_key</c> holds a key.</param>
/// <param name="AcoustIdLocked">Whether the environment sets the AcoustID key, so it cannot be changed here.</param>
/// <param name="LastFmKeySet">Whether <c>lastfm.api_key</c> holds a key.</param>
/// <param name="LastFmLocked">Whether the environment sets the Last.fm key, so it cannot be changed here.</param>
public sealed record MetadataSettings(bool AcoustIdKeySet, bool AcoustIdLocked, bool LastFmKeySet, bool LastFmLocked);

/// <summary>
/// A change to the keys. A <see langword="null"/> field is left alone; an empty string removes the key.
/// </summary>
/// <param name="AcoustIdClientKey">The new AcoustID client key.</param>
/// <param name="LastFmApiKey">The new Last.fm API key.</param>
public sealed record MetadataSettingsUpdate(string? AcoustIdClientKey = null, string? LastFmApiKey = null);

/// <summary>How an update ended.</summary>
/// <param name="Success">Whether the keys were written.</param>
/// <param name="Errors">Why it was refused: a key that is too long, or a field the environment owns.</param>
public sealed record MetadataSettingsUpdateResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>What a key test found.</summary>
/// <param name="Ok">Whether the service accepted the key.</param>
/// <param name="Message">A sentence the settings page shows; never the key.</param>
public sealed record MetadataKeyTestResult(bool Ok, string Message);

/// <summary>Reads and writes the optional AcoustID and Last.fm keys, and tests them.</summary>
public interface IMetadataSettingsService
{
    /// <summary>Reports which keys are set and which the environment owns.</summary>
    MetadataSettings Current();

    /// <summary>Writes the keys named in <paramref name="update"/> to <c>config.yml</c>; the options reload, so no restart is needed.</summary>
    /// <param name="update">The keys to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<MetadataSettingsUpdateResult> UpdateAsync(MetadataSettingsUpdate update, CancellationToken cancellationToken);

    /// <summary>Makes one call to the service with the typed key, or the stored one when none is typed.</summary>
    /// <param name="service">Which service to ask.</param>
    /// <param name="key">The key to try, or <see langword="null"/> for the stored one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<MetadataKeyTestResult> TestAsync(MetadataKeyService service, string? key, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IMetadataSettingsService"/>. A key set by an environment variable
/// (<c>APP__ACOUSTID__CLIENT_KEY</c>, <c>APP__LASTFM__API_KEY</c>) is refused rather than silently
/// overridden: the environment source is added after the YAML one, so a write to the file would be
/// invisible. The refusal reads like the other settings pages'.
/// </summary>
public sealed partial class MetadataSettingsService : IMetadataSettingsService
{
    /// <summary>The longest key accepted; real keys are 32 characters.</summary>
    public const int MaxKeyLength = 200;

    private const string AcoustIdSection = "acoustid";
    private const string AcoustIdKey = "client_key";
    private const string AcoustIdEnvironment = "APP__ACOUSTID__CLIENT_KEY";
    private const string LastFmSection = "lastfm";
    private const string LastFmKey = "api_key";
    private const string LastFmEnvironment = "APP__LASTFM__API_KEY";

    private readonly IOptionsMonitor<AcoustIdOptions> _acoustId;
    private readonly IOptionsMonitor<LastFmOptions> _lastFm;
    private readonly IConfigFileWriter _writer;
    private readonly IAcoustIdClient _acoustIdClient;
    private readonly ILastFmClient _lastFmClient;
    private readonly ISecretRegistry _secrets;
    private readonly ILogger<MetadataSettingsService> _logger;
    private readonly bool _acoustIdLocked;
    private readonly bool _lastFmLocked;

    /// <summary>Initialises a new instance of the <see cref="MetadataSettingsService"/> class.</summary>
    /// <param name="acoustId">The live AcoustID options, which already include the environment.</param>
    /// <param name="lastFm">The live Last.fm options, which already include the environment.</param>
    /// <param name="writer">Writes <c>config.yml</c> and reloads the configuration.</param>
    /// <param name="acoustIdClient">Checks an AcoustID key.</param>
    /// <param name="lastFmClient">Checks a Last.fm key.</param>
    /// <param name="secrets">Told about a new key before it is written, so no log line can print it.</param>
    /// <param name="environment">The process environment, used to find the keys the environment owns.</param>
    /// <param name="logger">Receives Debug lines naming the keys changed, never their values.</param>
    public MetadataSettingsService(
        IOptionsMonitor<AcoustIdOptions> acoustId,
        IOptionsMonitor<LastFmOptions> lastFm,
        IConfigFileWriter writer,
        IAcoustIdClient acoustIdClient,
        ILastFmClient lastFmClient,
        ISecretRegistry secrets,
        IDictionary environment,
        ILogger<MetadataSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(acoustId);
        ArgumentNullException.ThrowIfNull(lastFm);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(acoustIdClient);
        ArgumentNullException.ThrowIfNull(lastFmClient);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _acoustId = acoustId;
        _lastFm = lastFm;
        _writer = writer;
        _acoustIdClient = acoustIdClient;
        _lastFmClient = lastFmClient;
        _secrets = secrets;
        _logger = logger;
        _acoustIdLocked = EnvironmentOverrides.SectionKeys(environment, "ACOUSTID").Contains("clientkey");
        _lastFmLocked = EnvironmentOverrides.SectionKeys(environment, "LASTFM").Contains("apikey");
    }

    /// <inheritdoc />
    public MetadataSettings Current() => new(
        !string.IsNullOrWhiteSpace(_acoustId.CurrentValue.ClientKey),
        _acoustIdLocked,
        !string.IsNullOrWhiteSpace(_lastFm.CurrentValue.ApiKey),
        _lastFmLocked);

    /// <inheritdoc />
    public async Task<MetadataSettingsUpdateResult> UpdateAsync(
        MetadataSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var errors = new List<string>();

        var acoustId = Check(update.AcoustIdClientKey, "acoustIdClientKey", _acoustIdLocked, AcoustIdEnvironment, errors);
        var lastFm = Check(update.LastFmApiKey, "lastFmApiKey", _lastFmLocked, LastFmEnvironment, errors);

        if (errors.Count > 0)
        {
            return new MetadataSettingsUpdateResult(false, errors);
        }

        // Before the write, so the reload that follows cannot log the new key before it is known as one.
        _secrets.Register(acoustId.Value);
        _secrets.Register(lastFm.Value);

        // Both keys in one write: the file changes once, so a failure cannot leave one key saved.
        var sections = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        if (acoustId.Present)
        {
            sections[AcoustIdSection] = new Dictionary<string, object?> { [AcoustIdKey] = acoustId.Value };
        }

        if (lastFm.Present)
        {
            sections[LastFmSection] = new Dictionary<string, object?> { [LastFmKey] = lastFm.Value };
        }

        if (sections.Count > 0)
        {
            await _writer.UpdateSectionsAsync(sections, cancellationToken).ConfigureAwait(false);
            LogUpdated(_logger, acoustId.Present, lastFm.Present);
        }

        return new MetadataSettingsUpdateResult(true, []);
    }

    /// <inheritdoc />
    public async Task<MetadataKeyTestResult> TestAsync(
        MetadataKeyService service,
        string? key,
        CancellationToken cancellationToken)
    {
        var typed = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

        if (typed is { Length: > MaxKeyLength })
        {
            return new MetadataKeyTestResult(false, "That is too long to be a key.");
        }

        return service == MetadataKeyService.LastFm
            ? await TestLastFmAsync(typed ?? _lastFm.CurrentValue.ApiKey, cancellationToken).ConfigureAwait(false)
            : await TestAcoustIdAsync(typed ?? _acoustId.CurrentValue.ClientKey, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the metadata keys (AcoustID: {AcoustId}, Last.fm: {LastFm})")]
    private static partial void LogUpdated(ILogger logger, bool acoustId, bool lastFm);

    /// <summary>Validates one key of the update; the trimmed value, with an empty one meaning "remove".</summary>
    private static (bool Present, string? Value) Check(
        string? supplied,
        string field,
        bool locked,
        string variable,
        List<string> errors)
    {
        if (supplied is null)
        {
            return (false, null);
        }

        if (locked)
        {
            errors.Add($"{field} is set by the environment variable {variable} and cannot be changed here");

            return (false, null);
        }

        var trimmed = supplied.Trim();

        if (trimmed.Length > MaxKeyLength)
        {
            errors.Add($"{field} is too long to be a key");

            return (false, null);
        }

        return (true, trimmed.Length == 0 ? null : trimmed);
    }

    private async Task<MetadataKeyTestResult> TestLastFmAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new MetadataKeyTestResult(false, "There is no Last.fm key to test; type one first.");
        }

        var check = await _lastFmClient.CheckKeyAsync(key, cancellationToken).ConfigureAwait(false);

        return new MetadataKeyTestResult(check.Accepted, check.Message);
    }

    private async Task<MetadataKeyTestResult> TestAcoustIdAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new MetadataKeyTestResult(false, "There is no AcoustID key to test; type one first.");
        }

        var result = await _acoustIdClient.CheckKeyAsync(key, cancellationToken).ConfigureAwait(false);

        if (result.Status == AcoustIdStatus.Ok)
        {
            // Only a key that worked is a real secret; a mistyped one is not worth remembering.
            _secrets.Register(key);

            return new MetadataKeyTestResult(
                true,
                result.Error is { Length: > 0 } refusal
                    ? $"AcoustID accepted the key. It refused the probe fingerprint itself, as expected: {refusal}"
                    : "AcoustID accepted the key.");
        }

        return result.Status switch
        {
            AcoustIdStatus.InvalidKey => new MetadataKeyTestResult(false, "AcoustID rejected the key."),
            AcoustIdStatus.RateLimited => new MetadataKeyTestResult(false, "AcoustID is rate limiting this server; try again in a minute."),
            AcoustIdStatus.Unavailable => new MetadataKeyTestResult(false, result.Error ?? "AcoustID could not be reached."),
            _ => new MetadataKeyTestResult(false, result.Error ?? "AcoustID answered something Wondarr could not use."),
        };
    }
}
