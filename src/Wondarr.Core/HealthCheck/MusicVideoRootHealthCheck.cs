using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;

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

    /// <summary>Initialises a new instance of the <see cref="MusicVideoRootHealthCheck"/> class.</summary>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="roots">Checks the root on the disk, bounded at five seconds.</param>
    /// <param name="database">Where the library roots are, for the overlap rules.</param>
    /// <param name="paths">Where Wondarr's data directory is.</param>
    /// <param name="importOptions">Where the recycle bin is.</param>
    public MusicVideoRootHealthCheck(
        IOptionsMonitor<MusicVideoOptions> options,
        ILibraryRoots roots,
        WondarrDbContext database,
        WondarrPaths paths,
        IOptionsMonitor<ImportOptions> importOptions)
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
            .ProblemsAsync(options.RootPath, _database, _paths, _importOptions, cancellationToken)
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

        return message is null
            ? new HealthCheck(Name, HealthCheckResult.Ok, "The music video folder is ready", null)
            : new HealthCheck(Name, HealthCheckResult.Error, message, null);
    }
}
