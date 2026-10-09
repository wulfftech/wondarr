using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>One file's ReplayGain 2.0 measurement.</summary>
/// <param name="GainDb">The track gain in dB: the reference loudness (-18 LUFS) minus the integrated loudness, two decimals.</param>
/// <param name="Peak">The true peak as a linear value (1.0 is full scale), six decimals.</param>
public sealed record ReplayGainValues(double GainDb, double Peak);

/// <summary>Measures a file's loudness for ReplayGain.</summary>
public interface IReplayGainAnalyzer
{
    /// <summary>Measures one file; the audio is only read.</summary>
    /// <param name="path">The file to measure.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The track gain and peak, or <see langword="null"/> when the file could not be measured (logged).</returns>
    Task<ReplayGainValues?> MeasureAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Measures with ffmpeg's <c>ebur128</c> filter: EBU R128 integrated loudness and true peak, from the
/// summary ffmpeg prints at the end of stderr. A failure never throws (except cancellation): the caller
/// carries on without ReplayGain tags.
/// </summary>
public sealed partial class ReplayGainAnalyzer : IReplayGainAnalyzer
{
    /// <summary>The ReplayGain 2.0 reference loudness in LUFS.</summary>
    internal const double ReferenceLufs = -18.0;

    /// <summary>ebur128 reports silence (below the absolute gate) as -70 LUFS or lower.</summary>
    private const double SilenceLufs = -70.0;

    private static readonly Regex IntegratedPattern = new(
        @"Integrated loudness:\s*[\r\n]+\s*I:\s*(?<v>-?inf|[-+]?\d+(?:\.\d+)?)\s*LUFS",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex TruePeakPattern = new(
        @"True peak:\s*[\r\n]+\s*Peak:\s*(?<v>-?inf|[-+]?\d+(?:\.\d+)?)\s*dBFS",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _options;
    private readonly ILogger<ReplayGainAnalyzer> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReplayGainAnalyzer"/> class.</summary>
    /// <param name="runner">Runs ffmpeg without a shell.</param>
    /// <param name="options">The ffmpeg path and timeout.</param>
    /// <param name="logger">Logs why a measurement failed, at Warning.</param>
    public ReplayGainAnalyzer(
        IProcessRunner runner,
        IOptionsMonitor<MediaToolsOptions> options,
        ILogger<ReplayGainAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReplayGainValues?> MeasureAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = _options.CurrentValue;

        ProcessResult result;

        try
        {
            result = await _runner.RunAsync(
                    options.FfmpegPath,
                    [
                        "-nostdin",
                        "-hide_banner",
                        "-nostats",
                        "-i", path,
                        "-map", "0:a:0",
                        "-af", "ebur128=peak=true",
                        "-f", "null",
                        "-",
                    ],
                    TimeSpan.FromSeconds(options.TimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MediaToolMissingException exception)
        {
            LogFailed(_logger, path, exception.Message);

            return null;
        }

        if (result.TimedOut)
        {
            LogFailed(_logger, path, "ffmpeg timed out");

            return null;
        }

        if (result.ExitCode != 0)
        {
            LogFailed(_logger, path, MediaProbe.FirstErrorLine(result.StandardError) ?? $"ffmpeg exited {result.ExitCode}");

            return null;
        }

        var values = Parse(result.StandardError);

        if (values is null)
        {
            LogFailed(_logger, path, "ffmpeg printed no usable loudness summary (silent or unreadable audio)");
        }

        return values;
    }

    /// <summary>Reads the summary at the end of ffmpeg's stderr.</summary>
    /// <param name="standardError">What ffmpeg wrote to stderr.</param>
    /// <returns>The values, or <see langword="null"/> for no summary, silence or an unreadable number.</returns>
    internal static ReplayGainValues? Parse(string standardError)
    {
        if (string.IsNullOrEmpty(standardError))
        {
            return null;
        }

        // The per-frame lines also carry "I:" and "TPK:", so only the closing summary is read.
        var summary = standardError.LastIndexOf("Summary:", StringComparison.Ordinal);

        if (summary < 0)
        {
            return null;
        }

        var text = standardError[summary..];

        if (!TryRead(IntegratedPattern, text, out var loudness) || !TryRead(TruePeakPattern, text, out var peakDb))
        {
            return null;
        }

        if (loudness <= SilenceLufs)
        {
            return null;
        }

        var gain = Math.Round(ReferenceLufs - loudness, 2, MidpointRounding.AwayFromZero);
        var peak = Math.Round(Math.Pow(10, peakDb / 20), 6, MidpointRounding.AwayFromZero);

        return double.IsFinite(gain) && double.IsFinite(peak) ? new ReplayGainValues(gain, peak) : null;
    }

    private static bool TryRead(Regex pattern, string text, out double value)
    {
        value = 0;

        try
        {
            var match = pattern.Match(text);

            // "-inf" does not parse as a finite number, which is exactly the answer wanted.
            return match.Success
                && double.TryParse(
                    match.Groups["v"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value)
                && double.IsFinite(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReplayGain could not be measured for {Path}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string path, string reason);
}
