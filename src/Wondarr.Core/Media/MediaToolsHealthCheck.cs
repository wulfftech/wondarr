using Wondarr.Core.HealthCheck;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;

namespace Wondarr.Core.Media;

/// <summary>Which of the three media tools answered, and which fpcalc version.</summary>
/// <param name="Missing">The tools that could not be run, in the order ffprobe, ffmpeg, fpcalc.</param>
/// <param name="FpcalcVersion">The version the first line of <c>fpcalc -version</c> reports, or <c>null</c>.</param>
public sealed record MediaToolsStatus(IReadOnlyList<string> Missing, string? FpcalcVersion)
{
    /// <summary>Whether every tool answered.</summary>
    public bool AllAvailable => Missing.Count == 0;
}

/// <summary>
/// Runs <c>ffprobe -version</c>, <c>ffmpeg -version</c> and <c>fpcalc -version</c> once per process
/// lifetime and remembers the answer: the binaries do not move while Wondarr runs, and the health
/// checks run on a schedule.
/// </summary>
public sealed partial class MediaToolAvailability : IDisposable
{
    private static readonly string[] Tools = ["ffprobe", "ffmpeg", "fpcalc"];

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _options;
    private readonly ILogger<MediaToolAvailability> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaToolsStatus? _status;

    /// <summary>Initialises a new instance of the <see cref="MediaToolAvailability"/> class.</summary>
    /// <param name="runner">Runs the tools without a shell.</param>
    /// <param name="options">The configured binary paths and timeout.</param>
    /// <param name="logger">Logs a tool that fails to answer at Debug.</param>
    public MediaToolAvailability(
        IProcessRunner runner,
        IOptionsMonitor<MediaToolsOptions> options,
        ILogger<MediaToolAvailability> logger)
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

    /// <summary>Checks the tools, running them only the first time.</summary>
    /// <param name="cancellationToken">Cancels the first check.</param>
    /// <returns>The cached status.</returns>
    public async Task<MediaToolsStatus> GetStatusAsync(CancellationToken cancellationToken)
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

    private async Task<MediaToolsStatus> RunAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        var missing = new List<string>();
        string? fpcalcVersion = null;

        foreach (var tool in Tools)
        {
            var executable = tool switch
            {
                "ffprobe" => options.FfprobePath,
                "ffmpeg" => options.FfmpegPath,
                _ => options.FpcalcPath,
            };

            try
            {
                var result = await _runner.RunAsync(executable, ["-version"], timeout, cancellationToken)
                    .ConfigureAwait(false);

                if (result.TimedOut || result.ExitCode != 0)
                {
                    LogVersionFailed(_logger, executable, result.ExitCode);
                    missing.Add(tool);
                    continue;
                }

                if (tool == "fpcalc")
                {
                    fpcalcVersion = VersionOf(result.StandardOutput);
                }
            }
            catch (MediaToolMissingException)
            {
                missing.Add(tool);
            }
        }

        return new MediaToolsStatus(missing, fpcalcVersion);
    }

    /// <summary>The version on the first line of <c>-version</c>, for example <c>1.6.1</c>.</summary>
    private static string? VersionOf(string standardOutput)
    {
        foreach (var line in standardOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return tokens.Length == 0 ? null : tokens[^1];
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "'{Tool} -version' exited {ExitCode}.")]
    private static partial void LogVersionFailed(ILogger logger, string tool, int exitCode);
}

/// <summary>
/// Reports whether the bundled ffprobe, ffmpeg and fpcalc are runnable. Without them a download cannot
/// be measured or fingerprinted, so the import pipeline cannot tell a real file from a corrupt one.
/// </summary>
public sealed class MediaToolsHealthCheck : IHealthCheck
{
    private readonly MediaToolAvailability _availability;

    /// <summary>Initialises a new instance of the <see cref="MediaToolsHealthCheck"/> class.</summary>
    /// <param name="availability">The process-lifetime cache of the three version checks.</param>
    public MediaToolsHealthCheck(MediaToolAvailability availability)
    {
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
    }

    /// <inheritdoc />
    public string Name => nameof(MediaToolsHealthCheck);

    /// <inheritdoc />
    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await _availability.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.AllAvailable)
        {
            var version = string.IsNullOrEmpty(status.FpcalcVersion) ? string.Empty : $" (fpcalc {status.FpcalcVersion})";

            return new HealthReport(
                Name,
                HealthCheckResult.Ok,
                $"ffprobe, ffmpeg and fpcalc available{version}",
                null);
        }

        return new HealthReport(
            Name,
            HealthCheckResult.Warning,
            $"Media tools missing: {string.Join(", ", status.Missing)} — downloads cannot be verified",
            null);
    }
}
