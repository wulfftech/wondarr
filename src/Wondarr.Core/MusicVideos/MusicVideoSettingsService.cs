using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>What the music video settings page may read.</summary>
/// <param name="Enabled">Whether music videos are on.</param>
/// <param name="RootPath">The video root, or <see langword="null"/> when none is usable (the default would sit inside the container).</param>
/// <param name="Layout"><c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">The path of a video under the root.</param>
/// <param name="BesideTrackSuffix">What is added to the audio file's name under <c>beside_track</c>.</param>
/// <param name="MaxHeight">The tallest picture to download, or <see langword="null"/> for no cap.</param>
/// <param name="WriteNfo">Whether a <c>.nfo</c> file is written beside the video.</param>
/// <param name="WriteThumbnail">Whether a thumbnail is written beside the video.</param>
/// <param name="ScheduledSearchLimit">How many songs one run of the scheduled task may search for.</param>
/// <param name="ScheduledSearchEnabled">Whether the scheduled task that finds missing videos runs.</param>
/// <param name="ScheduledSearchIntervalHours">How often the scheduled task runs, in hours.</param>
/// <param name="ReadOnlyFields">camelCase names of the fields the environment sets through <c>APP__MUSIC_VIDEOS__…</c>.</param>
/// <param name="RootStatus">What the disk says about the root now, or <see langword="null"/> when no root is set.</param>
/// <param name="RootPathIsDefault">Whether the root is the default, <c>&lt;media root&gt;/music-videos</c>.</param>
/// <param name="DefaultRootPath">The default root, or <see langword="null"/> when it would sit inside the container instead of on a mount.</param>
public sealed record MusicVideoSettings(
    bool Enabled,
    string? RootPath,
    string Layout,
    string NamingTemplate,
    string BesideTrackSuffix,
    int? MaxHeight,
    bool WriteNfo,
    bool WriteThumbnail,
    int ScheduledSearchLimit,
    bool ScheduledSearchEnabled,
    int ScheduledSearchIntervalHours,
    IReadOnlyList<string> ReadOnlyFields,
    LibraryRootStatus? RootStatus,
    bool RootPathIsDefault = false,
    string? DefaultRootPath = null)
{
    /// <summary>
    /// Whether the root is set but not usable yet: it does not exist or holds no marker, so the user has to
    /// confirm it (<see cref="IMusicVideoSettingsService.ConfirmRootAsync"/>) before videos are written there.
    /// </summary>
    public bool RootNeedsConfirmation => RootStatus is LibraryRootStatus.Missing or LibraryRootStatus.Unmarked;
}

/// <summary>
/// A change to the music video settings. Every field is optional; an absent field is left alone.
/// </summary>
/// <param name="Enabled">New state of the switch.</param>
/// <param name="RootPath">New video root; an empty string goes back to the default.</param>
/// <param name="Layout">New layout, <c>folder</c> or <c>beside_track</c>.</param>
/// <param name="NamingTemplate">New naming template.</param>
/// <param name="BesideTrackSuffix">New suffix for <c>beside_track</c>.</param>
/// <param name="MaxHeight">New height cap in pixels; 0 removes the cap.</param>
/// <param name="WriteNfo">New state of the <c>.nfo</c> switch.</param>
/// <param name="WriteThumbnail">New state of the thumbnail switch.</param>
/// <param name="ScheduledSearchLimit">New per-run limit of the scheduled task.</param>
/// <param name="ScheduledSearchEnabled">New state of the switch for the scheduled task.</param>
/// <param name="ScheduledSearchIntervalHours">New interval of the scheduled task, in hours.</param>
public sealed record MusicVideoSettingsUpdate(
    bool? Enabled = null,
    string? RootPath = null,
    string? Layout = null,
    string? NamingTemplate = null,
    string? BesideTrackSuffix = null,
    int? MaxHeight = null,
    bool? WriteNfo = null,
    bool? WriteThumbnail = null,
    int? ScheduledSearchLimit = null,
    bool? ScheduledSearchEnabled = null,
    int? ScheduledSearchIntervalHours = null);

/// <summary>One reason a change was refused.</summary>
/// <param name="Field">The camelCase name of the field the reason is about, or <c>settings</c> when it is about no single field.</param>
/// <param name="Message">What is wrong.</param>
public sealed record MusicVideoSettingsError(string Field, string Message);

/// <summary>The outcome of a settings change or of confirming the root.</summary>
/// <param name="Success">Whether the change was made. On failure nothing was written.</param>
/// <param name="Errors">Why it was refused.</param>
/// <param name="TimedOut">Whether the disk did not answer in time; the confirm endpoint answers 504 then.</param>
public sealed record MusicVideoSettingsResult(bool Success, IReadOnlyList<MusicVideoSettingsError> Errors, bool TimedOut = false);

/// <summary>Reads and writes the <c>music_videos</c> section of <c>config.yml</c> for the settings page.</summary>
public interface IMusicVideoSettingsService
{
    /// <summary>Reads the current settings and what the disk says about the root.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<MusicVideoSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates a change and, when it is valid, writes only the changed keys to <c>config.yml</c>; the options
    /// reload, so nothing has to be restarted. A new root is marked when its folder exists and is empty, or
    /// already holds the marker; otherwise <see cref="MusicVideoSettings.RootNeedsConfirmation"/> says it needs
    /// confirming.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<MusicVideoSettingsResult> UpdateAsync(MusicVideoSettingsUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the marker into the saved root, creating the folder when it is not there: the user has seen that it
    /// is missing or unmarked and says it is the right place.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<MusicVideoSettingsResult> ConfirmRootAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// A field the environment sets (<c>APP__MUSIC_VIDEOS__…</c>) is refused rather than silently overridden: the
/// environment source is added after the YAML one, so a write to the file would be invisible to the app.
/// </remarks>
public sealed partial class MusicVideoSettingsService : IMusicVideoSettingsService
{
    /// <summary>Section of <c>config.yml</c> these settings live in.</summary>
    public const string Section = "music_videos";

    private const string EnvironmentPrefix = "APP__MUSIC_VIDEOS__";

    /// <summary>Every field this page owns: its <c>config.yml</c> key and the environment variable's suffix.</summary>
    private static readonly Field[] Fields =
    [
        new("enabled", "ENABLED"),
        new("root_path", "ROOT_PATH"),
        new("layout", "LAYOUT"),
        new("naming_template", "NAMING_TEMPLATE"),
        new("beside_track_suffix", "BESIDE_TRACK_SUFFIX"),
        new("max_height", "MAX_HEIGHT"),
        new("write_nfo", "WRITE_NFO"),
        new("write_thumbnail", "WRITE_THUMBNAIL"),
        new("scheduled_search_limit", "SCHEDULED_SEARCH_LIMIT"),
        new("scheduled_search_enabled", "SCHEDULED_SEARCH_ENABLED"),
        new("scheduled_search_interval_hours", "SCHEDULED_SEARCH_INTERVAL_HOURS"),
    ];

    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly IReadOnlyCollection<IValidateOptions<MusicVideoOptions>> _validators;
    private readonly WondarrDbContext _database;
    private readonly ILibraryRoots _roots;
    private readonly IDiskOperations _disk;
    private readonly WondarrPaths _paths;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly ILogger<MusicVideoSettingsService> _logger;
    private readonly IReadOnlySet<string> _locked;
    private readonly ContainerMounts? _containers;
    private readonly IMusicVideoScanTrigger? _scan;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoSettingsService"/> class.</summary>
    /// <param name="options">The live options, which already include the environment.</param>
    /// <param name="writer">Writes the changed keys back to <c>config.yml</c>.</param>
    /// <param name="validators">The options validators, so a change is judged by the rules start-up uses.</param>
    /// <param name="database">Where the library and reference-library roots are.</param>
    /// <param name="roots">Checks and marks the video root.</param>
    /// <param name="disk">Tells whether the root's folder is empty.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    /// <param name="importOptions">Where the recycle bin is.</param>
    /// <param name="environment">The process environment, used to find the fields the environment owns.</param>
    /// <param name="logger">Receives a Debug line naming the keys that were written.</param>
    /// <param name="containers">The container rule; when omitted the rule is not applied.</param>
    /// <param name="scan">Queues a scan of the video folder once the root is saved or confirmed; <see langword="null"/> queues none.</param>
    public MusicVideoSettingsService(
        IOptionsMonitor<MusicVideoOptions> options,
        IConfigFileWriter writer,
        IEnumerable<IValidateOptions<MusicVideoOptions>> validators,
        WondarrDbContext database,
        ILibraryRoots roots,
        IDiskOperations disk,
        WondarrPaths paths,
        IOptionsMonitor<ImportOptions> importOptions,
        IDictionary environment,
        ILogger<MusicVideoSettingsService> logger,
        ContainerMounts? containers = null,
        IMusicVideoScanTrigger? scan = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(validators);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _writer = writer;
        _validators = [.. validators];
        _database = database;
        _roots = roots;
        _disk = disk;
        _paths = paths;
        _importOptions = importOptions;
        _logger = logger;
        _containers = containers;
        _scan = scan;
        _locked = EnvironmentOverrides.SectionKeys(environment, Section);
    }

    /// <inheritdoc />
    public async Task<MusicVideoSettings> GetAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var defaultRoot = _paths.DefaultMusicVideoRoot;
        var defaultUsable = _containers?.IsOnContainerRoot(defaultRoot) != true;
        var root = string.IsNullOrWhiteSpace(options.RootPath) ? null : options.RootPath;
        var isDefault = root is not null && IsDefaultRoot(root);

        // A default inside the container is no folder at all as far as the page is concerned: it is not offered.
        if (isDefault && !defaultUsable)
        {
            root = null;
            isDefault = false;
        }

        LibraryRootStatus? status = root is null
            ? null
            : await _roots.CheckAsync(root, cancellationToken).ConfigureAwait(false);

        return new MusicVideoSettings(
            options.Enabled,
            root,
            options.Layout,
            options.NamingTemplate,
            options.BesideTrackSuffix,
            options.MaxHeight,
            options.WriteNfo,
            options.WriteThumbnail,
            options.ScheduledSearchLimit,
            options.ScheduledSearchEnabled,
            options.ScheduledSearchIntervalHours,
            [.. Fields.Where(field => IsLocked(field.Key)).Select(field => CamelCase(field.Key))],
            status,
            isDefault,
            defaultUsable ? defaultRoot : null);
    }

    /// <inheritdoc />
    public async Task<MusicVideoSettingsResult> UpdateAsync(
        MusicVideoSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = _options.CurrentValue;
        var candidate = new MusicVideoOptions
        {
            Enabled = current.Enabled,
            RootPath = current.RootPath,
            Layout = current.Layout,
            NamingTemplate = current.NamingTemplate,
            BesideTrackSuffix = current.BesideTrackSuffix,
            MaxHeight = current.MaxHeight,
            WriteNfo = current.WriteNfo,
            WriteThumbnail = current.WriteThumbnail,
            ScheduledSearchLimit = current.ScheduledSearchLimit,
            ScheduledSearchEnabled = current.ScheduledSearchEnabled,
            ScheduledSearchIntervalHours = current.ScheduledSearchIntervalHours,
        };

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<MusicVideoSettingsError>();

        ApplyFlag(update.Enabled, "enabled", current.Enabled, changes, errors, value => candidate.Enabled = value);
        ApplyFlag(update.WriteNfo, "write_nfo", current.WriteNfo, changes, errors, value => candidate.WriteNfo = value);
        ApplyFlag(update.WriteThumbnail, "write_thumbnail", current.WriteThumbnail, changes, errors, value => candidate.WriteThumbnail = value);

        if (update.ScheduledSearchLimit is { } limit && limit != current.ScheduledSearchLimit && !Refuse("scheduled_search_limit", errors))
        {
            candidate.ScheduledSearchLimit = limit;
            changes["scheduled_search_limit"] = limit;
        }

        ApplyFlag(update.ScheduledSearchEnabled, "scheduled_search_enabled", current.ScheduledSearchEnabled, changes, errors, value => candidate.ScheduledSearchEnabled = value);

        if (update.ScheduledSearchIntervalHours is { } hours
            && hours != current.ScheduledSearchIntervalHours
            && !Refuse("scheduled_search_interval_hours", errors))
        {
            candidate.ScheduledSearchIntervalHours = hours;
            changes["scheduled_search_interval_hours"] = hours;
        }

        ApplyMaxHeight(update.MaxHeight, current.MaxHeight, candidate, changes, errors);
        ApplyLayout(update.Layout, current.Layout, candidate, changes, errors);
        ApplyTemplate(update.NamingTemplate, current.NamingTemplate, candidate, changes, errors);
        ApplySuffix(update.BesideTrackSuffix, current.BesideTrackSuffix, candidate, changes, errors);

        var rootChanged = await ApplyRootAsync(update.RootPath, current.RootPath, candidate, changes, errors, cancellationToken)
            .ConfigureAwait(false);

        if (errors.Count > 0)
        {
            return new MusicVideoSettingsResult(false, errors);
        }

        // "On" needs a folder that passes every rule, whether or not this save changed the folder.
        if (candidate.Enabled)
        {
            if (string.IsNullOrWhiteSpace(candidate.RootPath))
            {
                return Failed("rootPath", "Choose a video folder before turning music videos on.");
            }

            if (!rootChanged)
            {
                var problems = await RootProblemsAsync(candidate.RootPath, cancellationToken).ConfigureAwait(false);

                if (problems.Count > 0)
                {
                    return new MusicVideoSettingsResult(false, [.. problems.Select(problem => new MusicVideoSettingsError("rootPath", problem))]);
                }
            }
        }

        // The same rules as start-up: a change that the app would refuse to boot with is refused here.
        foreach (var validator in _validators)
        {
            var validation = validator.Validate(Options.DefaultName, candidate);

            if (validation.Failed)
            {
                return new MusicVideoSettingsResult(
                    false,
                    [.. (validation.Failures ?? []).Select(failure => new MusicVideoSettingsError("settings", failure))]);
            }
        }

        var needsConfirmation = false;

        // Creating or marking is for a new folder or a feature being turned on. A save that changes neither never
        // touches the disk: the share may have dropped, and its empty mount point must not be marked (#40).
        var turningOn = !current.Enabled && candidate.Enabled;

        if ((rootChanged || turningOn) && !string.IsNullOrWhiteSpace(candidate.RootPath))
        {
            try
            {
                needsConfirmation = await PrepareRootAsync(candidate.RootPath, candidate.Enabled, cancellationToken).ConfigureAwait(false);
            }
            catch (MusicVideoTimeoutException)
            {
                return Failed("rootPath", $"{candidate.RootPath} did not answer in time, so its share may be hung; nothing was saved. Try again when it answers.");
            }
        }

        // The feature is on only once its folder is: a new folder, or a switch turned on, that still needs the user's
        // confirm is saved with the switch off. An unrelated change to a config that is already on goes through.
        var withheld = candidate.Enabled && needsConfirmation && (rootChanged || !current.Enabled);

        if (withheld)
        {
            if (current.Enabled)
            {
                changes["enabled"] = false;
            }
            else
            {
                changes.Remove("enabled");
            }
        }

        if (changes.Count > 0)
        {
            await _writer.UpdateSectionAsync(Section, changes, cancellationToken).ConfigureAwait(false);

            // Keys only: the root is a path on the user's own machine, and every config.yml write is logged this way.
            var keys = string.Join(", ", changes.Keys);
            LogUpdated(_logger, keys);

            if (rootChanged && !withheld && _scan is not null)
            {
                await _scan.EnqueueAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return withheld
            ? Failed("rootPath", "Confirm the folder first: use the button under the folder, then save again. The folder is saved, but music videos stay off until it is confirmed.")
            : new MusicVideoSettingsResult(true, []);
    }

    /// <inheritdoc />
    public async Task<MusicVideoSettingsResult> ConfirmRootAsync(CancellationToken cancellationToken)
    {
        var root = _options.CurrentValue.RootPath;

        if (string.IsNullOrWhiteSpace(root))
        {
            return Failed("rootPath", "rootPath is not set; save a root first.");
        }

        // A root set through config.yml or the environment never went through the save's checks.
        var problems = await RootProblemsAsync(root, cancellationToken).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            return new MusicVideoSettingsResult(false, [.. problems.Select(problem => new MusicVideoSettingsError("rootPath", problem))]);
        }

        if (await _roots.CheckAsync(root, cancellationToken).ConfigureAwait(false) == LibraryRootStatus.Unresponsive)
        {
            return Failed("rootPath", $"{root} did not answer in time, so its share may be hung; try again when it answers.");
        }

        try
        {
            await MusicVideoDisk.RunAsync(
                () =>
                {
                    _roots.Mark(root);

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            return new MusicVideoSettingsResult(false, [new MusicVideoSettingsError("rootPath", new MusicVideoTimeoutException().Message)], TimedOut: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("rootPath", $"the marker file could not be written into {root}: {exception.Message}");
        }

        if (_scan is not null)
        {
            await _scan.EnqueueAsync(cancellationToken).ConfigureAwait(false);
        }

        return new MusicVideoSettingsResult(true, []);
    }

    private static MusicVideoSettingsResult Failed(string field, string message) =>
        new(false, [new MusicVideoSettingsError(field, message)]);

    /// <summary>Turns a <c>config.yml</c> key into the camelCase name the API and the UI use.</summary>
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

    /// <summary>Whether a suffix can be part of a file name.</summary>
    private static bool IsFileNameText(string text) =>
        text.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0;

    /// <summary>Whether the folder holds nothing, asked of the disk with a time limit.</summary>
    /// <exception cref="MusicVideoTimeoutException">The disk did not answer in time.</exception>
    private async Task<bool> IsEmptyAsync(string root, CancellationToken cancellationToken) =>
        !await MusicVideoDisk.RunAsync(() => _disk.HasAnyEntry(root), cancellationToken).ConfigureAwait(false);

    private Task<List<string>> RootProblemsAsync(string root, CancellationToken cancellationToken) =>
        MusicVideoRootRules.ProblemsAsync(root, _database, _paths, _importOptions, cancellationToken, _containers);

    private bool IsDefaultRoot(string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root.Trim()), _paths.DefaultMusicVideoRoot, PathRules.Comparison);

    /// <summary>
    /// Marks a root the user just saved when that is safe: an existing empty folder, or, while the feature is being
    /// turned on, the default folder, which is Wondarr's own and is created when missing. A folder that already
    /// holds the marker needs nothing; any other missing or non-empty unmarked one is left for the user to confirm,
    /// because an empty mount point and a share that is not mounted look the same.
    /// </summary>
    /// <returns><see langword="true"/> when the folder still needs the user's confirm.</returns>
    /// <exception cref="MusicVideoTimeoutException">The disk did not answer in time.</exception>
    private async Task<bool> PrepareRootAsync(string root, bool enabling, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _roots.CheckAsync(root, cancellationToken).ConfigureAwait(false);

            if (status == LibraryRootStatus.Unresponsive)
            {
                throw new MusicVideoTimeoutException();
            }

            if (status == LibraryRootStatus.Unmarked && await IsEmptyAsync(root, cancellationToken).ConfigureAwait(false))
            {
                await MusicVideoDisk.RunAsync(() => _roots.MarkExisting(root), cancellationToken).ConfigureAwait(false);

                return false;
            }

            if (status == LibraryRootStatus.Missing && enabling && IsDefaultRoot(root))
            {
                await MusicVideoDisk.RunAsync(
                    () =>
                    {
                        _roots.Mark(root);

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);

                return false;
            }

            return status is LibraryRootStatus.Missing or LibraryRootStatus.Unmarked;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not a validation error: the root is saved, the health check reports it and the user can confirm it.
            LogMarkFailed(_logger, root, exception);

            return true;
        }
    }

    private async Task<bool> ApplyRootAsync(
        string? supplied,
        string? current,
        MusicVideoOptions candidate,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors,
        CancellationToken cancellationToken)
    {
        if (supplied is null)
        {
            return false;
        }

        // Empty, or the default's own path, means "the default": the key is removed so the default keeps following
        // the media root.
        var trimmed = supplied.Trim();
        var useDefault = trimmed.Length == 0 || IsDefaultRoot(trimmed);
        var replacement = useDefault ? _paths.DefaultMusicVideoRoot : trimmed;

        if (string.Equals(replacement, current, StringComparison.Ordinal) || Refuse("root_path", errors))
        {
            return false;
        }

        var problems = await RootProblemsAsync(replacement, cancellationToken).ConfigureAwait(false);

        // The default is Wondarr's own choice, so a container problem with it only matters once it is to be used:
        // turning the feature on is refused for it by the "on needs a usable folder" rule.
        if (problems.Count > 0 && !(useDefault && !candidate.Enabled))
        {
            errors.AddRange(problems.Select(problem => new MusicVideoSettingsError("rootPath", problem)));

            return false;
        }

        candidate.RootPath = replacement;
        changes["root_path"] = useDefault ? null : replacement;

        return true;
    }

    private void ApplyLayout(
        string? supplied,
        string current,
        MusicVideoOptions candidate,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors)
    {
        if (supplied is null || string.Equals(supplied, current, StringComparison.Ordinal) || Refuse("layout", errors))
        {
            return;
        }

        if (supplied is not (MusicVideoOptions.FolderLayout or MusicVideoOptions.BesideTrackLayout))
        {
            errors.Add(new MusicVideoSettingsError(
                "layout",
                $"layout must be {MusicVideoOptions.FolderLayout} or {MusicVideoOptions.BesideTrackLayout}."));

            return;
        }

        candidate.Layout = supplied;
        changes["layout"] = supplied;
    }

    private void ApplyTemplate(
        string? supplied,
        string current,
        MusicVideoOptions candidate,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors)
    {
        if (supplied is null || string.Equals(supplied, current, StringComparison.Ordinal) || Refuse("naming_template", errors))
        {
            return;
        }

        if (MusicVideoTemplate.Problem(supplied) is { } problem)
        {
            errors.Add(new MusicVideoSettingsError("namingTemplate", $"namingTemplate {problem}."));

            return;
        }

        candidate.NamingTemplate = supplied;
        changes["naming_template"] = supplied;
    }

    private void ApplySuffix(
        string? supplied,
        string current,
        MusicVideoOptions candidate,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors)
    {
        if (supplied is null || string.Equals(supplied, current, StringComparison.Ordinal) || Refuse("beside_track_suffix", errors))
        {
            return;
        }

        // An empty suffix would give the video the audio file's own name, and its .nfo and thumbnail would
        // collide with the audio's sidecars.
        if (string.IsNullOrWhiteSpace(supplied))
        {
            errors.Add(new MusicVideoSettingsError("besideTrackSuffix", "besideTrackSuffix must not be empty."));

            return;
        }

        if (!IsFileNameText(supplied))
        {
            errors.Add(new MusicVideoSettingsError("besideTrackSuffix", "besideTrackSuffix must be text that can be part of a file name."));

            return;
        }

        if (supplied.Contains("..", StringComparison.Ordinal) || supplied.IndexOfAny(['/', '\\']) >= 0)
        {
            errors.Add(new MusicVideoSettingsError("besideTrackSuffix", "besideTrackSuffix must not hold a path separator or '..'."));

            return;
        }

        candidate.BesideTrackSuffix = supplied;
        changes["beside_track_suffix"] = supplied;
    }

    private void ApplyMaxHeight(
        int? supplied,
        int? current,
        MusicVideoOptions candidate,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors)
    {
        if (supplied is not { } value)
        {
            return;
        }

        // 0 means "no cap", so a form can clear the setting: an absent field cannot say it.
        int? replacement = value == 0 ? null : value;

        if (replacement == current || Refuse("max_height", errors))
        {
            return;
        }

        if (value < 0)
        {
            errors.Add(new MusicVideoSettingsError("maxHeight", "maxHeight must be above 0, or 0 for no cap."));

            return;
        }

        candidate.MaxHeight = replacement;
        changes["max_height"] = replacement;
    }

    private void ApplyFlag(
        bool? supplied,
        string key,
        bool current,
        Dictionary<string, object?> changes,
        List<MusicVideoSettingsError> errors,
        Action<bool> write)
    {
        if (supplied is not { } value || value == current || Refuse(key, errors))
        {
            return;
        }

        write(value);
        changes[key] = value;
    }

    /// <summary>Refuses a change to a field the environment owns, naming the variable that sets it.</summary>
    /// <returns><see langword="true"/> when the change was refused.</returns>
    private bool Refuse(string key, List<MusicVideoSettingsError> errors)
    {
        if (!IsLocked(key))
        {
            return false;
        }

        var field = Fields.First(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));

        errors.Add(new MusicVideoSettingsError(
            CamelCase(key),
            $"{CamelCase(key)} is set by the environment variable {EnvironmentPrefix}{field.EnvironmentKey} and cannot be changed here"));

        return true;
    }

    private bool IsLocked(string key) => _locked.Contains(key.Replace("_", string.Empty, StringComparison.Ordinal));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated the music video settings ({Keys})")]
    private static partial void LogUpdated(ILogger logger, string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the marker file into the music video root {Root}; videos wait until it is there")]
    private static partial void LogMarkFailed(ILogger logger, string root, Exception exception);

    /// <summary>One editable field: its <c>config.yml</c> key and the environment variable that owns it.</summary>
    private sealed record Field(string Key, string EnvironmentKey);
}
