using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Profiles;

namespace Wondarr.Core.Media;

/// <summary>One finished transcode: where the new file is and what it is.</summary>
/// <param name="Path">The file that was written.</param>
/// <param name="Extension">The container of the file at <paramref name="Path"/>, without the dot.</param>
public sealed record TranscodeResult(string Path, string Extension);

/// <summary>
/// Turns one audio file into another, per a library's <see cref="OutputPolicy"/>. The import
/// pipeline owns the decision; this only runs the encoder and report what it did.
/// </summary>
public interface ITranscoder
{
    /// <summary>
    /// Transcodes <paramref name="sourcePath"/> into <paramref name="destinationPath"/> as the
    /// policy says, or hands the source straight back when the policy keeps it.
    /// </summary>
    /// <param name="sourcePath">The file to read; never overwritten.</param>
    /// <param name="policy">What the target is: codec, mode, bitrate, sample rate.</param>
    /// <param name="destinationPath">Where the target is written; the folder must exist.</param>
    /// <param name="cancellationToken">Cancels the encoder run.</param>
    /// <returns>The file that now holds the audio, and its container.</returns>
    /// <exception cref="TranscodePolicyException">The policy asks for a target that is never written.</exception>
    /// <exception cref="TranscodeException">The encoder failed or timed out.</exception>
    Task<TranscodeResult> TranscodeAsync(
        string sourcePath,
        OutputPolicy policy,
        string destinationPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// A transcode the policy forbids: a lossless target from a lossy source (ADR-0006), which the
/// caller must refuse before anything is written.
/// </summary>
public sealed class TranscodePolicyException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="TranscodePolicyException"/> class.</summary>
    /// <param name="message">Why the target is refused, as the user reads it.</param>
    public TranscodePolicyException(string message)
        : base(message)
    {
    }
}

/// <summary>The encoder run itself failed: it exited non-zero, or it was killed on timeout.</summary>
public sealed class TranscodeException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="TranscodeException"/> class.</summary>
    /// <param name="message">Why the run failed, as the user reads it.</param>
    /// <param name="innerException">The underlying failure, when there is one.</param>
    public TranscodeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The transcode step of the import pipeline: one <c>ffmpeg</c> run per file, no <c>-y</c> — a
/// target that already exists is a bug to look at, not a file to overwrite — and the media tools'
/// shared timeout. The arguments are built one item per switch, so a file name is never re-parsed
/// by a shell.
/// </summary>
public sealed partial class Transcoder : ITranscoder
{
    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _tools;
    private readonly ILogger<Transcoder> _logger;

    /// <summary>Initialises a new instance of the <see cref="Transcoder"/> class.</summary>
    /// <param name="runner">Runs the encoder.</param>
    /// <param name="tools">Where the <c>ffmpeg</c> binary is and how long it may run.</param>
    /// <param name="logger">Logs one line per transcode at Information.</param>
    public Transcoder(IProcessRunner runner, IOptionsMonitor<MediaToolsOptions> tools, ILogger<Transcoder> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _tools = tools;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TranscodeResult> TranscodeAsync(
        string sourcePath,
        OutputPolicy policy,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // Keeping the Opus remux is not a transcode: the file is handed back untouched, and the
        // caller imports it as it is.
        if (policy.Codec == OutputCodec.KeepOpus)
        {
            return new TranscodeResult(sourcePath, policy.Container);
        }

        // Defence in depth: the policy cannot name a lossless codec, so this catches a caller that
        // built its own destination. Never lossless from lossy (ADR-0006).
        var extension = Path.GetExtension(destinationPath).TrimStart('.').ToLowerInvariant();

        if (OutputPolicy.LosslessContainers.Contains(extension))
        {
            throw new TranscodePolicyException(
                $"Never transcode to a lossless target (.{extension}): the source is lossy already (ADR-0006).");
        }

        var options = _tools.CurrentValue;
        var timeout = Timeout(options);
        var run = await _runner
            .RunAsync(options.FfmpegPath, BuildArguments(sourcePath, policy, destinationPath), timeout, cancellationToken)
            .ConfigureAwait(false);

        if (run.TimedOut)
        {
            throw new TranscodeException($"ffmpeg was killed after {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s: {sourcePath}");
        }

        if (run.ExitCode != 0)
        {
            throw new TranscodeException($"ffmpeg exited {run.ExitCode.ToString(CultureInfo.InvariantCulture)}: {Trim(run.StandardError)}");
        }

        LogTranscoded(_logger, sourcePath, destinationPath, policy.Container);

        return new TranscodeResult(destinationPath, policy.Container);
    }

    /// <summary>
    /// The encoder command: <c>-i</c> the source, <c>-vn</c> (a YouTube remux carries no video, and
    /// a cover image is embedded by the tag writer, not the encoder), the codec switches the policy
    /// chose, then the target.
    /// </summary>
    private static List<string> BuildArguments(string sourcePath, OutputPolicy policy, string destinationPath)
    {
        var arguments = new List<string> { "-i", sourcePath, "-vn" };

        switch (policy.Codec, policy.Mode)
        {
            case (OutputCodec.Aac, OutputMode.Cbr):
                arguments.Add("-c:a");
                arguments.Add("aac");
                arguments.Add("-b:a");
                arguments.Add(policy.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
                break;

            // The 0–9 quality scale is LAME's (MP3-only); a policy built by hand that asks for an
            // AAC VBR is refused rather than silently run as CBR.
            case (OutputCodec.Aac, OutputMode.Vbr):
                throw new TranscodePolicyException(
                    "outputPolicy: VBR is the LAME quality scale and exists for MP3 only; use mode \"cbr\" for AAC.");

            case (OutputCodec.Mp3, OutputMode.Cbr):
                arguments.Add("-c:a");
                arguments.Add("libmp3lame");
                arguments.Add("-b:a");
                arguments.Add(policy.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
                break;

            case (OutputCodec.Mp3, OutputMode.Vbr):
                arguments.Add("-c:a");
                arguments.Add("libmp3lame");
                arguments.Add("-q:a");
                arguments.Add(policy.VbrQuality.ToString(CultureInfo.InvariantCulture));
                break;
        }

        if (policy.SampleRateHz is { } rate)
        {
            arguments.Add("-ar");
            arguments.Add(rate.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add(destinationPath);

        return arguments;
    }

    private static TimeSpan Timeout(MediaToolsOptions options) => TimeSpan.FromSeconds(options.TimeoutSeconds);

    /// <summary>The first line of the encoder's stderr, which is where ffmpeg says why it failed.</summary>
    private static string Trim(string standardError)
    {
        var firstLine = standardError.Split('\n')[0].Trim();

        return firstLine.Length == 0 ? "the encoder wrote nothing" : firstLine;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcoded {Source} to {Target} ({Container})")]
    private static partial void LogTranscoded(ILogger logger, string source, string target, string container);
}
