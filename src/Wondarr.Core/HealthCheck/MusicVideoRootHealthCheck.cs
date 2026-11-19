using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Reports the music video root while the feature is on: an error when it is not set, does not exist, holds
/// no marker file (so it may be an unmounted share) or did not answer in five seconds. While the feature is off
/// there is nothing to report. The message holds no counts or times, so the health notification does not repeat
/// itself.
/// </summary>
public sealed class MusicVideoRootHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ILibraryRoots _roots;
    private readonly WondarrDbContext _database;
    private readonly WondarrPaths _paths;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;
    private readonly ContainerMounts? _containers;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoRootHealthCheck"/> class.</summary>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="roots">Checks the root on the disk, bounded at five seconds.</param>
    /// <param name="database">Where the library roots are, for the overlap rules.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    /// <param name="importOptions">Where the recycle bin is.</param>
    /// <param name="containers">The container rule; when omitted the rule is not applied.</param>
    public MusicVideoRootHealthCheck(
        IOptionsMonitor<MusicVideoOptions> options,
        ILibraryRoots roots,
        WondarrDbContext database,
        WondarrPaths paths,
        IOptionsMonitor<ImportOptions> importOptions,
        ContainerMounts? containers = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(importOptions);

        _options = options;
        _roots = roots;
        _database = database;
        _paths = paths;
        _importOptions = importOptions;
        _containers = containers;
    }

    /// <inheritdoc />
    public string Name => nameof(MusicVideoRootHealthCheck);

    /// <inheritdoc />
    public async Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return new HealthCheck(Name, HealthCheckResult.Ok, "Music videos are turned off", null);
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return new HealthCheck(
                Name,
                HealthCheckResult.Error,
                "Music videos are on but no video folder is set, so no video is written. Set it under Settings → Music videos.",
                null);
        }

        // A root set through config.yml or the environment never went through the settings save's rules.
        var problems = await MusicVideoRootRules
            .ProblemsAsync(options.RootPath, _database, _paths, _importOptions, cancellationToken, _containers)
            .ConfigureAwait(false);

        if (problems.Count > 0)
        {
            return new HealthCheck(
                Name,
                HealthCheckResult.Error,
                $"Music video folder {options.RootPath} cannot be used: {string.Join(' ', problems)}",
                null);
        }

        var status = await _roots.CheckAsync(options.RootPath, cancellationToken).ConfigureAwait(false);

        var message = status switch
        {
            LibraryRootStatus.Missing =>
                $"Music video folder {options.RootPath} does not exist. Videos wait until it is back; if the share is unmounted, mount it.",
            LibraryRootStatus.Unmarked =>
                $"Music video folder {options.RootPath} has no {LibraryRoots.MarkerFileName} file, so it looks like an unmounted share. Videos wait. If this is the right folder, confirm it under Settings → Music videos.",
            LibraryRootStatus.Unresponsive =>
                $"Music video folder {options.RootPath} did not answer within 5 seconds, so its share may be hung. Videos wait until it answers.",
            _ => null,
        };

        if (message is not null)
        {
            return new HealthCheck(Name, HealthCheckResult.Error, message, null);
        }

        // The scan lets no video go while this many are missing at once (a share that is half unmounted looks like this),
        // so it says so: the count is what tells a person whether the share or the folder is at fault.
        var total = await _database.MusicVideoFiles.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        var missing = await _database.MusicVideoFiles
            .AsNoTracking()
            .CountAsync(
                file => file.State == MusicVideoFileState.Missing
                    || (file.State == MusicVideoFileState.Ignored && file.MissingSince != null),
                cancellationToken)
            .ConfigureAwait(false);

        return ReferenceMissingRules.IsMountProblem(total, missing)
            ? new HealthCheck(
                Name,
                HealthCheckResult.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{missing} of the {total} videos in the music video folder {options.RootPath} are missing at once, so the scan lets none of them go. If the share is not fully mounted, mount it; if you deleted them, they are let go once the folder is back under the limit."),
                null)
            : new HealthCheck(Name, HealthCheckResult.Ok, "The music video folder is ready", null);
    }
}
