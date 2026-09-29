using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>Which part of the file the fingerprint is taken from.</summary>
public enum FingerprintWindow
{
    /// <summary>The first <see cref="MediaToolsOptions.FingerprintLengthSeconds"/> seconds — the normal case.</summary>
    Start,

    /// <summary>The middle of the track, for files whose opening is talk-over, silence or a long intro.</summary>
    Middle,
}

/// <summary>One Chromaprint fingerprint, ready for the AcoustID lookup (MATCHING_ENGINE.md §6.5 step 3).</summary>
/// <param name="Success">Whether a fingerprint was produced.</param>
/// <param name="Fingerprint">The compressed base64 fingerprint, or <c>null</c>.</param>
/// <param name="DurationSeconds">The <b>whole track's</b> length in whole seconds, which is what AcoustID wants.</param>
/// <param name="Window">Which part of the file was fingerprinted.</param>
/// <param name="Error">The line that explains a failure, or <c>null</c>.</param>
public sealed record FingerprintResult(
    bool Success,
    string? Fingerprint,
    int DurationSeconds,
    FingerprintWindow Window,
    string? Error);

/// <summary>Fingerprints a downloaded file.</summary>
public interface IFingerprinter
{
    /// <summary>Fingerprints one window of a file.</summary>
    /// <param name="path">The file to fingerprint.</param>
    /// <param name="window">The start of the file, or its middle.</param>
    /// <param name="trackDurationMs">The track's known length, which sets the duration AcoustID is told.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The fingerprint, or why there is none.</returns>
    Task<FingerprintResult> FingerprintAsync(
        string path,
        FingerprintWindow window,
        int trackDurationMs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the bundled <c>fpcalc</c>. The first attempt covers the start of the file; when AcoustID does
/// not recognise it — DJ talk-over, a long intro, a cold open — the middle of the file is tried instead
/// (MATCHING_ENGINE.md §6.5 step 3).
/// </summary>
/// <remarks>
/// fpcalc has no offset option, so the middle window is cut out with ffmpeg into a temporary WAV first
/// (docs/research/research_metadata_plex.md §2.3). The temporary file is deleted whether or not fpcalc
/// succeeded.
/// </remarks>
public sealed partial class Fingerprinter : IFingerprinter
{
    /// <summary>The rate the middle window is cut at: Chromaprint's own reference rate.</summary>
    private const int WindowSampleRate = 44100;

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _options;
    private readonly ILogger<Fingerprinter> _logger;

    /// <summary>Initialises a new instance of the <see cref="Fingerprinter"/> class.</summary>
    /// <param name="runner">Runs fpcalc and ffmpeg without a shell.</param>
    /// <param name="options">The configured binary paths, timeout and fingerprint length.</param>
    /// <param name="logger">Logs unreadable fpcalc output at Debug.</param>
    public Fingerprinter(
        IProcessRunner runner,
        IOptionsMonitor<MediaToolsOptions> options,
        ILogger<Fingerprinter> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FingerprintResult> FingerprintAsync(
        string path,
        FingerprintWindow window,
        int trackDurationMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = _options.CurrentValue;
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        var length = options.FingerprintLengthSeconds;

        // AcoustID has to be told the length of the whole track, in whole seconds — never the length of
        // the window, and never fpcalc's own duration (0.00 when it read from a pipe).
        var durationSeconds = trackDurationMs / 1000;

        if (window == FingerprintWindow.Start)
        {
            var result = await _runner.RunAsync(
                    options.FpcalcPath,
                    ["-json", "-length", length.ToString(CultureInfo.InvariantCulture), path],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return Read(result, window, durationSeconds);
        }

        var start = Math.Max(0, (trackDurationMs / 2 - (length * 500)) / 1000);
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"wondarr-fpcalc-{Guid.NewGuid():N}.wav");

        try
        {
            var cut = await _runner.RunAsync(
                    options.FfmpegPath,
                    [
                        "-v", "error",
                        "-ss", start.ToString(CultureInfo.InvariantCulture),
                        "-t", length.ToString(CultureInfo.InvariantCulture),
                        "-i", path,
                        "-ac", "2",
                        "-ar", WindowSampleRate.ToString(CultureInfo.InvariantCulture),
                        "-f", "wav",
                        temporary,
                    ],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cut.TimedOut || cut.ExitCode != 0)
            {
                return Failure(
                    window,
                    durationSeconds,
                    MediaProbe.FirstErrorLine(cut.StandardError)
                        ?? (cut.TimedOut ? "ffmpeg timed out" : $"ffmpeg exited {cut.ExitCode}"));
            }

            // No -length: the temporary WAV is already exactly as long as the window should be.
            var result = await _runner.RunAsync(
                    options.FpcalcPath,
                    ["-json", temporary],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return Read(result, window, durationSeconds);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private FingerprintResult Read(ProcessResult result, FingerprintWindow window, int durationSeconds)
    {
        if (result.TimedOut)
        {
            return Failure(window, durationSeconds, "fpcalc timed out");
        }

        if (result.ExitCode != 0)
        {
            return Failure(
                window,
                durationSeconds,
                MediaProbe.FirstErrorLine(result.StandardError) ?? $"fpcalc exited {result.ExitCode}");
        }

        var fingerprint = Fingerprint(result.StandardOutput);

        return fingerprint is null
            ? Failure(
                window,
                durationSeconds,
                MediaProbe.FirstErrorLine(result.StandardError) ?? "fpcalc returned no fingerprint")
            : new FingerprintResult(true, fingerprint, durationSeconds, window, null);
    }

    private string? Fingerprint(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement.TryGetProperty("fingerprint", out var property)
                && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;

            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (JsonException exception)
        {
            LogUnreadableOutput(_logger, exception);

            return null;
        }
    }

    private static FingerprintResult Failure(FingerprintWindow window, int durationSeconds, string error) =>
        new(false, null, durationSeconds, window, error);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing an import over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "fpcalc did not print JSON.")]
    private static partial void LogUnreadableOutput(ILogger logger, Exception exception);
}
