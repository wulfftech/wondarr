using Microsoft.Extensions.Options;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;

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

    /// <summary>Initialises a new instance of the <see cref="MusicVideoRootHealthCheck"/> class.</summary>
    /// <param name="options">The <c>music_videos</c> settings.</param>
    /// <param name="roots">Checks the root on the disk, bounded at five seconds.</param>
    public MusicVideoRootHealthCheck(IOptionsMonitor<MusicVideoOptions> options, ILibraryRoots roots)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roots);

        _options = options;
        _roots = roots;
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
