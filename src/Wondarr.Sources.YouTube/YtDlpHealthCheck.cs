using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Sources.YouTube;

/// <summary>What the yt-dlp health probe found: the version, and whether the EJS runtime answered.</summary>
/// <param name="Version">The version <c>yt-dlp --version</c> printed, or <c>null</c> when it did not answer.</param>
/// <param name="HasJsRuntime">Whether the Deno runtime (the EJS interpreter yt-dlp needs) is runnable.</param>
public sealed record YtDlpHealthStatus(string? Version, bool HasJsRuntime)
{
    /// <summary>Whether the yt-dlp binary answered.</summary>
    public bool BinaryAvailable => Version is not null;
}

/// <summary>
/// Runs <c>yt-dlp --version</c> and <c>deno --version</c> once per process lifetime and remembers the
/// answer, like <see cref="MediaToolAvailability"/>: the binaries do not move while Wondarr runs, and
/// the health checks run on a schedule.
/// </summary>
public sealed partial class YtDlpAvailability : IDisposable
{
    private readonly IProcessRunner _runner;
    private readonly IYtDlpBinary _binary;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly ILogger<YtDlpAvailability> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedStatus? _cached;

    /// <summary>Initialises a new instance of the <see cref="YtDlpAvailability"/> class.</summary>
    /// <param name="runner">Runs the binaries without a shell.</param>
    /// <param name="binary">Says which yt-dlp binary to probe.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">Logs a probe that fails at Debug.</param>
    public YtDlpAvailability(
        IProcessRunner runner,
        IYtDlpBinary binary,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpAvailability> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _binary = binary ?? throw new ArgumentNullException(nameof(binary));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Checks the binaries, running them only the first time, and again whenever the binary in use changed
    /// (an update, a fallback, a settings change): the cache belongs to one <see cref="YtDlpResolution.Generation"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The cached status.</returns>
    public async Task<YtDlpHealthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var resolution = await _binary.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (Volatile.Read(ref _cached) is { } cached && cached.Generation == resolution.Generation)
        {
            return cached.Status;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cached is { } again && again.Generation == resolution.Generation)
            {
                return again.Status;
            }

            var status = await RunAsync(resolution.Path, cancellationToken).ConfigureAwait(false);

            // One write, so a reader never pairs a status with another generation.
            Volatile.Write(ref _cached, new CachedStatus(status, resolution.Generation));

            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A probe of one's own: an uncached availability sharing the same runner, options and logger
    /// wiring, for the settings page's Test button. The constructor's signature is this factory's
    /// contract — a new dependency lands here, not at the call site.
    /// </summary>
    /// <param name="runner">Runs the binaries without a shell.</param>
    /// <param name="binary">Says which yt-dlp binary to probe.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">The probe's own logger.</param>
    /// <returns>A fresh availability whose first check runs the binaries again.</returns>
    public static YtDlpAvailability CreateProbe(
        IProcessRunner runner,
        IYtDlpBinary binary,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpAvailability> logger) => new(runner, binary, options, logger);

    private async Task<YtDlpHealthStatus> RunAsync(string ytdlpPath, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        // A --version probe answers in well under a second; cap it at 30s so a huge configured
        // download timeout cannot make a health check hang, without adding a second setting.
        var timeout = TimeSpan.FromSeconds(Math.Min(options.Ytdlp.TimeoutSeconds, 30));

        var version = await ProbeAsync(ytdlpPath, timeout, cancellationToken).ConfigureAwait(false);
        var denoVersion = await ProbeAsync("deno", timeout, cancellationToken).ConfigureAwait(false);

        return new YtDlpHealthStatus(version, denoVersion is not null);
    }

    /// <summary>A probe's answer and the binary generation it was made for.</summary>
    private sealed record CachedStatus(YtDlpHealthStatus Status, int Generation);

    /// <summary>The first line a <c>--version</c> probe prints, or <c>null</c> when it did not answer.</summary>
    private async Task<string?> ProbeAsync(
        string executable,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(executable, ["--version"], timeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.TimedOut || result.ExitCode != 0)
            {
                LogVersionFailed(_logger, executable, result.ExitCode);

                return null;
            }

            var firstLine = result.StandardOutput.Split('\n').FirstOrDefault(line => line.Trim().Length > 0);

            return firstLine?.Trim();
        }
        catch (MediaToolMissingException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "'{Tool} --version' exited {ExitCode}.")]
    private static partial void LogVersionFailed(ILogger logger, string tool, int exitCode);
}

/// <summary>
/// Reports whether the bundled yt-dlp answers, and whether the Deno runtime it needs for YouTube
/// extraction is installed. Without yt-dlp the YouTube source cannot download anything.
/// </summary>
public sealed class YtDlpHealthCheck : IHealthCheck
{
    /// <summary>How many days older than the newest release the yt-dlp in use may be before the check warns.</summary>
    public const int MaxDaysBehind = 60;

    /// <summary>How many days the update may go without a successful check before the check warns about that.</summary>
    public const int MaxDaysWithoutCheck = 7;

    private readonly YtDlpAvailability _availability;
    private readonly IYtDlpBinary _binary;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly IOptionsMonitor<MusicVideoOptions> _musicVideos;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="YtDlpHealthCheck"/> class.</summary>
    /// <param name="availability">The cache of the version checks.</param>
    /// <param name="binary">Knows what the update task last found.</param>
    /// <param name="options">The YouTube settings: a source that is off has nothing to warn about.</param>
    /// <param name="musicVideos">The music video settings: music videos download with yt-dlp too.</param>
    /// <param name="time">The clock.</param>
    public YtDlpHealthCheck(
        YtDlpAvailability availability,
        IYtDlpBinary binary,
        IOptionsMonitor<YouTubeOptions> options,
        IOptionsMonitor<MusicVideoOptions> musicVideos,
        TimeProvider time)
    {
        _musicVideos = musicVideos ?? throw new ArgumentNullException(nameof(musicVideos));
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _binary = binary ?? throw new ArgumentNullException(nameof(binary));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public string Name => "youtube";

    /// <inheritdoc />
    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled && !_musicVideos.CurrentValue.Enabled)
        {
            // Both are off by default; a missing yt-dlp only matters once the user turns one on.
            return new HealthReport(Name, HealthCheckResult.Ok, "The YouTube source and music videos are off", null);
        }

        var status = await _availability.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!status.BinaryAvailable)
        {
            return new HealthReport(
                Name,
                HealthCheckResult.Warning,
                "yt-dlp could not be run: it is not installed or not on PATH — YouTube downloads cannot start",
                null);
        }

        var update = await _binary.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (update.JsRuntimeWarning is { } runtimeProblem)
        {
            return new HealthReport(
                Name,
                HealthCheckResult.Warning,
                $"yt-dlp {status.Version} reports a problem with the JS runtime (Deno), which Wondarr does not update: {runtimeProblem}",
                null);
        }

        if (update.UpdateActive && StaleWarning(update, status.Version) is { } stale)
        {
            return new HealthReport(Name, HealthCheckResult.Warning, stale, null);
        }

        var jsRuntime = status.HasJsRuntime
            ? string.Empty
            : " — the JS runtime (Deno) is missing: YouTube extraction will fail";

        return new HealthReport(
            Name,
            HealthCheckResult.Ok,
            $"yt-dlp {status.Version} available{jsRuntime}",
            null);
    }

    /// <summary>
    /// A sentence when the update has stopped working, or <see langword="null"/>. No successful check for a week
    /// comes first, because the newest release the last check saw is then out of date itself.
    /// </summary>
    private string? StaleWarning(YtDlpUpdateStatus update, string? inUse)
    {
        var now = _time.GetUtcNow();
        var since = update.LastSuccessAt ?? update.StartedAt;
        var quiet = (now - since).Days;

        if (quiet >= MaxDaysWithoutCheck)
        {
            return $"yt-dlp has not been checked for updates in {quiet} days — YouTube downloads may fail until it is";
        }

        if (YtDlpVersion.DateOf(update.LatestVersion) is { } newest && YtDlpVersion.DateOf(inUse) is { } current)
        {
            var behind = newest.DayNumber - current.DayNumber;

            if (behind > MaxDaysBehind)
            {
                return $"yt-dlp {inUse} is {behind} days older than the newest release ({update.LatestVersion}) — YouTube downloads may fail until it is updated";
            }
        }

        return null;
    }
}
