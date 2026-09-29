using System.Collections;
using Wondarr.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// What the Soulseek settings page may read. The password is never part of it: the page is told
/// only whether one is stored.
/// </summary>
/// <param name="Username">The Soulseek account name, if one is configured.</param>
/// <param name="PasswordSet">Whether a password is stored. Never the password itself.</param>
/// <param name="ListenPort">Port slskd listens on for peer connections.</param>
/// <param name="ShareLibrary">The "share my library" toggle.</param>
/// <param name="SharedFolders">Folders shared back to the network.</param>
/// <param name="UploadSlots">Concurrent upload slots.</param>
/// <param name="UploadSpeedLimitKib">Upload speed limit in KiB/s; 0 means unlimited.</param>
/// <param name="DistributedNetwork">Whether slskd joins the distributed network.</param>
/// <param name="DownloadsDir">Directory slskd downloads into.</param>
/// <param name="IncompleteDir">Directory slskd keeps partial transfers in.</param>
/// <param name="ReadOnlyFields">
/// camelCase names of the fields the environment sets through <c>APP__SOULSEEK__…</c>. The
/// environment wins over the file, so the UI shows these as read-only.
/// </param>
public sealed record SoulseekSettings(
    string? Username,
    bool PasswordSet,
    int ListenPort,
    bool ShareLibrary,
    IReadOnlyList<string> SharedFolders,
    int UploadSlots,
    int UploadSpeedLimitKib,
    bool DistributedNetwork,
    string DownloadsDir,
    string IncompleteDir,
    IReadOnlyList<string> ReadOnlyFields);

/// <summary>
/// A change to the Soulseek settings. Every field is optional; an absent field is left alone.
/// </summary>
/// <param name="Username">New account name, or <see langword="null"/> to keep the current one.</param>
/// <param name="Password">
/// New password. <see langword="null"/> keeps the stored one, an empty string clears it. Never logged.
/// </param>
/// <param name="ListenPort">New peer-connection port.</param>
/// <param name="ShareLibrary">New state of the "share my library" toggle.</param>
/// <param name="SharedFolders">New shared folders; an empty list shares nothing.</param>
/// <param name="UploadSlots">New upload slot count.</param>
/// <param name="UploadSpeedLimitKib">New upload speed limit.</param>
/// <param name="DistributedNetwork">New distributed-network state.</param>
/// <param name="DownloadsDir">New download directory.</param>
/// <param name="IncompleteDir">New incomplete-transfer directory.</param>
public sealed record SoulseekSettingsUpdate(
    string? Username = null,
    string? Password = null,
    int? ListenPort = null,
    bool? ShareLibrary = null,
    IReadOnlyList<string>? SharedFolders = null,
    int? UploadSlots = null,
    int? UploadSpeedLimitKib = null,
    bool? DistributedNetwork = null,
    string? DownloadsDir = null,
    string? IncompleteDir = null);

/// <summary>The outcome of a settings change.</summary>
/// <param name="Success">Whether the change was written. On failure nothing was written.</param>
/// <param name="Errors">Why it was refused: a validation failure, or a field the environment owns.</param>
/// <param name="RestartsSlskd">
/// Whether the change is one the supervisor will restart slskd for. Informational: the supervisor
/// decides for itself, this is what the UI tells the user.
/// </param>
public sealed record SoulseekSettingsUpdateResult(bool Success, IReadOnlyList<string> Errors, bool RestartsSlskd);

/// <summary>
/// Reads and writes the <c>soulseek</c> section of <c>config.yml</c> for the settings page.
/// </summary>
public interface ISoulseekSettingsService
{
    /// <summary>Reads the current settings.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The settings API is named Get/Update by design.")]
    SoulseekSettings Get();

    /// <summary>
    /// Validates a change and, when it is valid, writes only the changed keys to <c>config.yml</c>.
    /// The write reloads the configuration, so the supervisor sees the new options and re-renders
    /// <c>slskd.yml</c> by itself.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<SoulseekSettingsUpdateResult> UpdateAsync(SoulseekSettingsUpdate update, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Mode, binary path and web port are deliberately not editable here: this page is about the
/// Soulseek account and how slskd shares and transfers, not about where the process lives.
/// </para>
/// <para>
/// A field the environment sets (<c>APP__SOULSEEK__…</c>) is refused rather than silently
/// overridden: the environment source is added after the YAML one, so a write to the file would be
/// invisible to the app and the user would see their change "not stick".
/// </para>
/// </remarks>
public sealed partial class SoulseekSettingsService : ISoulseekSettingsService
{
    /// <summary>Section of <c>config.yml</c> these settings live in.</summary>
    public const string Section = "soulseek";

    /// <summary>The environment variables the Soulseek settings are read from.</summary>
    private const string EnvironmentPrefix = "APP__SOULSEEK__";

    /// <summary>
    /// Every field this page owns: its <c>config.yml</c> key, the environment variable's suffix and
    /// the camelCase name the API and the UI use.
    /// </summary>
    private static readonly Field[] Fields =
    [
        new("username", "USERNAME"),
        new("password", "PASSWORD"),
        new("listen_port", "LISTEN_PORT"),
        new("share_library", "SHARE_LIBRARY"),
        new("shared_folders", "SHARED_FOLDERS"),
        new("upload_slots", "UPLOAD_SLOTS"),
        new("upload_speed_limit_kib", "UPLOAD_SPEED_LIMIT_KIB"),
        new("distributed_network", "DISTRIBUTED_NETWORK"),
        new("downloads_dir", "DOWNLOADS_DIR"),
        new("incomplete_dir", "INCOMPLETE_DIR"),
    ];

    /// <summary>
    /// The keys slskd only reads at start: changing one of them is what the supervisor restarts for.
    /// </summary>
    private static readonly HashSet<string> RestartingKeys = new(StringComparer.Ordinal)
    {
        "username",
        "password",
        "listen_port",
        "share_library",
        "shared_folders",
        "upload_slots",
        "distributed_network",
    };

    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly IValidateOptions<SoulseekOptions> _validator;
    private readonly ILogger<SoulseekSettingsService> _logger;

    /// <summary>The <c>config.yml</c> keys the environment owns, normalised as the binder sees them.</summary>
    private readonly IReadOnlySet<string> _locked;

    /// <summary>Initialises a new instance of the <see cref="SoulseekSettingsService"/> class.</summary>
    /// <param name="options">The live Soulseek options, which already include the environment.</param>
    /// <param name="writer">Writes the changed keys back to <c>config.yml</c>.</param>
    /// <param name="validator">The options validator, so a change is judged by the same rules as start-up.</param>
    /// <param name="environment">The process environment, used to find the fields the environment owns.</param>
    /// <param name="logger">Receives a Debug line naming the keys that were written.</param>
    public SoulseekSettingsService(
        IOptionsMonitor<SoulseekOptions> options,
        IConfigFileWriter writer,
        IValidateOptions<SoulseekOptions> validator,
        IDictionary environment,
        ILogger<SoulseekSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _writer = writer;
        _validator = validator;
        _logger = logger;
        _locked = EnvironmentOverrides.SectionKeys(environment, Section);
    }

    /// <inheritdoc />
    // The name is part of the API this service was specified with. A member called Get only trips
    // CA1716 for consumers in languages that reserve the keyword, which is not a constraint here,
    // so the rule is suppressed rather than the spelling changed.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The settings API is named Get/Update by design.")]
    public SoulseekSettings Get()
    {
        var options = _options.CurrentValue;

        return new SoulseekSettings(
            options.Username,
            !string.IsNullOrEmpty(options.Password),
            options.ListenPort,
            options.ShareLibrary,
            [.. options.SharedFolders],
            options.UploadSlots,
            options.UploadSpeedLimitKib,
            options.DistributedNetwork,
            options.DownloadsDir,
            options.IncompleteDir,
            [.. Fields.Where(field => IsLocked(field.Key)).Select(field => CamelCase(field.Key))]);
    }

    /// <inheritdoc />
    public async Task<SoulseekSettingsUpdateResult> UpdateAsync(
        SoulseekSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = _options.CurrentValue;
        var candidate = new SoulseekOptions
        {
            Mode = current.Mode,
            Username = current.Username,
            Password = current.Password,
            ListenPort = current.ListenPort,
            ShareLibrary = current.ShareLibrary,
            SharedFolders = [.. current.SharedFolders],
            UploadSlots = current.UploadSlots,
            UploadSpeedLimitKib = current.UploadSpeedLimitKib,
            DistributedNetwork = current.DistributedNetwork,
            DownloadsDir = current.DownloadsDir,
            IncompleteDir = current.IncompleteDir,
            BinaryPath = current.BinaryPath,
            WebPort = current.WebPort,

            // Read by the validator only, never mutated: the candidate is not handed to the supervisor.
            Search = current.Search,
        };

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<string>();

        Apply(update, current, candidate, changes, errors);

        if (errors.Count > 0)
        {
            return new SoulseekSettingsUpdateResult(false, errors, false);
        }

        // The same rules as start-up: a change that the app would refuse to boot with is refused here.
        var validation = _validator.Validate(Options.DefaultName, candidate);
        if (validation.Failed)
        {
            var failures = validation.Failures ?? [];

            return new SoulseekSettingsUpdateResult(false, [.. failures], false);
        }

        if (changes.Count > 0)
        {
            await _writer.UpdateSectionAsync(Section, changes, cancellationToken).ConfigureAwait(false);

            // Key names only: the section holds the Soulseek password.
            var keys = string.Join(", ", changes.Keys);
            LogUpdated(_logger, keys);
        }

        return new SoulseekSettingsUpdateResult(
            true,
            [],
            changes.Keys.Any(RestartingKeys.Contains));
    }

    /// <summary>
    /// Copies the supplied fields into <paramref name="candidate"/> and records the ones that
    /// actually differ. A read-only field is an error only when the value really changes: the UI
    /// posts the whole form back, so an untouched read-only field must not make saving impossible.
    /// </summary>
    private void Apply(
        SoulseekSettingsUpdate update,
        SoulseekOptions current,
        SoulseekOptions candidate,
        Dictionary<string, object?> changes,
        List<string> errors)
    {
        ApplyNumber(update.ListenPort, "listen_port", current.ListenPort, changes, errors, value => candidate.ListenPort = value);
        ApplyFlag(update.ShareLibrary, "share_library", current.ShareLibrary, changes, errors, value => candidate.ShareLibrary = value);
        ApplyNumber(update.UploadSlots, "upload_slots", current.UploadSlots, changes, errors, value => candidate.UploadSlots = value);
        ApplyNumber(update.UploadSpeedLimitKib, "upload_speed_limit_kib", current.UploadSpeedLimitKib, changes, errors, value => candidate.UploadSpeedLimitKib = value);
        ApplyFlag(update.DistributedNetwork, "distributed_network", current.DistributedNetwork, changes, errors, value => candidate.DistributedNetwork = value);
        ApplyText(update.Username, "username", current.Username, changes, errors, value => candidate.Username = value);
        ApplyText(update.DownloadsDir, "downloads_dir", current.DownloadsDir, changes, errors, value => candidate.DownloadsDir = value);
        ApplyText(update.IncompleteDir, "incomplete_dir", current.IncompleteDir, changes, errors, value => candidate.IncompleteDir = value);

        if (update.SharedFolders is { } folders)
        {
            var replaced = folders.ToList();

            if (!replaced.SequenceEqual(current.SharedFolders, StringComparer.Ordinal)
                && !Refuse("shared_folders", errors))
            {
                candidate.SharedFolders = replaced;
                changes["shared_folders"] = replaced;
            }
        }

        // Null means "keep what is stored"; an empty string means "clear it". Either way the value
        // stops here: it is written to config.yml and never logged or returned.
        if (update.Password is { } password)
        {
            var replacement = password.Length == 0 ? null : password;

            if (!string.Equals(replacement, current.Password, StringComparison.Ordinal)
                && !Refuse("password", errors))
            {
                candidate.Password = replacement;
                changes["password"] = replacement;
            }
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

    /// <summary>Records a text change, unless it is absent, unchanged or owned by the environment.</summary>
    private void ApplyText(
        string? supplied,
        string key,
        string? current,
        Dictionary<string, object?> changes,
        List<string> errors,
        Action<string> write)
    {
        if (supplied is null
            || string.Equals(supplied, current, StringComparison.Ordinal)
            || Refuse(key, errors))
        {
            return;
        }

        write(supplied);
        changes[key] = supplied;
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

    private bool IsLocked(string key) => _locked.Contains(key.Replace("_", string.Empty, StringComparison.Ordinal));

    /// <summary>Where the value lives in the update's <c>T?</c> overload: the boxes differ per type.</summary>
    private static string CamelCase(string key)
    {
        var parts = key.Split('_');
        var name = parts[0];

        for (var index = 1; index < parts.Length; index++)
        {
            name += char.ToUpperInvariant(parts[index][0]) + parts[index][1..];
        }

        return name;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the Soulseek settings ({Keys})")]
    private static partial void LogUpdated(ILogger logger, string keys);

    /// <summary>One editable field: its <c>config.yml</c> key and the environment variable that owns it.</summary>
    private sealed record Field(string Key, string EnvironmentKey);
}