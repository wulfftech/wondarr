using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Media;
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
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly ILogger<YtDlpAvailability> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private YtDlpHealthStatus? _status;

    /// <summary>Initialises a new instance of the <see cref="YtDlpAvailability"/> class.</summary>
    /// <param name="runner">Runs the binaries without a shell.</param>
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">Logs a probe that fails at Debug.</param>
    public YtDlpAvailability(
        IProcessRunner runner,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpAvailability> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Checks the binaries, running them only the first time.</summary>
    /// <param name="cancellationToken">Cancels the first check.</param>
    /// <returns>The cached status.</returns>
    public async Task<YtDlpHealthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_status is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _status ??= await RunAsync(cancellationToken).ConfigureAwait(false);

            return _status;
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
    /// <param name="options">The <c>youtube</c> section of <c>config.yml</c>.</param>
    /// <param name="logger">The probe's own logger.</param>
    /// <returns>A fresh availability whose first check runs the binaries again.</returns>
    public static YtDlpAvailability CreateProbe(
        IProcessRunner runner,
        IOptionsMonitor<YouTubeOptions> options,
        ILogger<YtDlpAvailability> logger) => new(runner, options, logger);

    private async Task<YtDlpHealthStatus> RunAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        // A --version probe answers in well under a second; cap it at 30s so a huge configured
        // download timeout cannot make a health check hang, without adding a second setting.
        var timeout = TimeSpan.FromSeconds(Math.Min(options.Ytdlp.TimeoutSeconds, 30));

        var version = await ProbeAsync(options.Ytdlp.BinaryPath, timeout, cancellationToken).ConfigureAwait(false);
        var denoVersion = await ProbeAsync("deno", timeout, cancellationToken).ConfigureAwait(false);

        return new YtDlpHealthStatus(version, denoVersion is not null);
    }

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
    private readonly YtDlpAvailability _availability;

    /// <summary>Initialises a new instance of the <see cref="YtDlpHealthCheck"/> class.</summary>
    /// <param name="availability">The process-lifetime cache of the version checks.</param>
    public YtDlpHealthCheck(YtDlpAvailability availability)
    {
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
    }

    /// <inheritdoc />
    public string Name => "youtube";

    /// <inheritdoc />
    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await _availability.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!status.BinaryAvailable)
        {
            return new HealthReport(
                Name,
                HealthCheckResult.Warning,
                "yt-dlp could not be run: it is not installed or not on PATH — YouTube downloads cannot start",
                null);
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
}
