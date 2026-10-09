using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>Decodes a window of an audio file and checks its spectrum for a lossy encoder's low-pass.</summary>
public interface ISpectralAnalyzer
{
    /// <summary>Analyses a 60 second window of <paramref name="path"/>.</summary>
    /// <param name="path">The audio file to read; never modified.</param>
    /// <param name="durationMs">The file's duration in milliseconds, as probed; it places the window.</param>
    /// <param name="cancellationToken">Cancels the decode.</param>
    /// <returns>
    /// The verdict. A decode that fails, times out or yields nothing is <see cref="SpectralOutcome.Inconclusive"/>,
    /// never an exception: only cancellation propagates.
    /// </returns>
    Task<SpectralVerdict> AnalyzeAsync(string path, int durationMs, CancellationToken cancellationToken);
}

/// <summary>
/// The decode half of the fake-lossless check (DECISIONS build session 10 #2): one <c>ffmpeg</c> run
/// writes a 60 second mono 44.1 kHz float window to a temporary file, which <see cref="SpectrumAnalysis"/>
/// then reads. The window starts 30 % into the track, moved earlier so it ends inside the file.
/// </summary>
public sealed partial class SpectralAnalyzer : ISpectralAnalyzer
{
    private const int SampleRate = 44_100;
    private const int WindowSeconds = 60;
    private const double StartFraction = 0.3;

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _tools;
    private readonly ILogger<SpectralAnalyzer> _logger;

    /// <summary>Initialises a new instance of the <see cref="SpectralAnalyzer"/> class.</summary>
    /// <param name="runner">Runs ffmpeg.</param>
    /// <param name="tools">Where the <c>ffmpeg</c> binary is and how long it may run.</param>
    /// <param name="logger">Logs a failed decode at Warning.</param>
    public SpectralAnalyzer(IProcessRunner runner, IOptionsMonitor<MediaToolsOptions> tools, ILogger<SpectralAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _tools = tools;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SpectralVerdict> AnalyzeAsync(string path, int durationMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = _tools.CurrentValue;
        var temp = Path.Combine(Path.GetTempPath(), "wondarr-spectral-" + Guid.NewGuid().ToString("N") + ".f32");

        try
        {
            var run = await _runner
                .RunAsync(
                    options.FfmpegPath,
                    BuildArguments(path, durationMs, temp),
                    TimeSpan.FromSeconds(options.TimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            if (run.TimedOut || run.ExitCode != 0)
            {
                LogDecodeFailed(_logger, path, run.TimedOut ? -1 : run.ExitCode, LastLine(run.StandardError));

                return Inconclusive();
            }

            if (!File.Exists(temp))
            {
                LogDecodeFailed(_logger, path, run.ExitCode, "ffmpeg wrote no output");

                return Inconclusive();
            }

            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < sizeof(float))
            {
                LogDecodeFailed(_logger, path, run.ExitCode, "ffmpeg wrote no samples");

                return Inconclusive();
            }

            return SpectrumAnalysis.Analyse(ToSamples(bytes), SampleRate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MediaToolMissingException or IOException or UnauthorizedAccessException)
        {
            LogDecodeFailed(_logger, path, -1, exception.Message);

            return Inconclusive();
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// The decode command. <c>-nostdin</c> keeps ffmpeg from reading our stdin, and <c>-map 0:a:0</c>
    /// skips an embedded cover image. A file of a minute or less is read whole.
    /// </summary>
    private static List<string> BuildArguments(string path, int durationMs, string output)
    {
        var arguments = new List<string> { "-nostdin", "-v", "error" };

        var seconds = durationMs / 1000.0;
        if (seconds > WindowSeconds)
        {
            var start = (int)Math.Floor(seconds * StartFraction);
            start = Math.Min(start, (int)Math.Floor(seconds - WindowSeconds));

            arguments.Add("-ss");
            arguments.Add(start.ToString(CultureInfo.InvariantCulture));
        }

        arguments.AddRange(["-t", WindowSeconds.ToString(CultureInfo.InvariantCulture), "-i", path]);
        arguments.AddRange(["-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-f", "f32le", "-y", output]);

        return arguments;
    }

    /// <summary>Reads little-endian float32 samples, whatever the host's byte order.</summary>
    private static ReadOnlySpan<float> ToSamples(byte[] bytes)
    {
        var whole = bytes.AsSpan(0, bytes.Length - (bytes.Length % sizeof(float)));

        if (BitConverter.IsLittleEndian)
        {
            return MemoryMarshal.Cast<byte, float>(whole);
        }

        var samples = new float[whole.Length / sizeof(float)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadSingleLittleEndian(whole[(index * sizeof(float))..]);
        }

        return samples;
    }

    private static SpectralVerdict Inconclusive() => new(SpectralOutcome.Inconclusive, null, 0);

    private static string LastLine(string standardError)
    {
        var lines = standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0 ? "no error output" : lines[^1];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temp file that cannot be removed now is the temp folder's to clean up.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Spectral check of {Path} was inconclusive: ffmpeg exit {ExitCode}: {Detail}")]
    private static partial void LogDecodeFailed(ILogger logger, string path, int exitCode, string detail);
}
