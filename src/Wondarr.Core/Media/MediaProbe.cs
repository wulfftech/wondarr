using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>The answer to "what is this file really?" (MATCHING_ENGINE.md §6.5 step 1).</summary>
/// <param name="Decodable">Whether ffprobe found audio and ffmpeg decoded all of it.</param>
/// <param name="Info">What was measured, or <c>null</c> when the file is not decodable.</param>
/// <param name="Error">The line that explains a failure, or <c>null</c> when there is none.</param>
/// <param name="TimedOut">
/// Whether the failure is a tool that ran out of time. That says nothing about the file (a long FLAC
/// on a loaded NAS), so the verdict on it is "ask again later", never a rejection.
/// </param>
/// <param name="Tolerated">
/// Set only by <see cref="IMediaProbe.ProbeReferenceAsync"/>: the file did not decode cleanly, but within
/// what a reference scan accepts. <see cref="Decodable"/> is <c>true</c> then.
/// </param>
public sealed record MediaProbeResult(
    bool Decodable,
    MediaInfo? Info,
    string? Error,
    bool TimedOut = false,
    ToleratedDecodeErrors? Tolerated = null);

/// <summary>What a reference scan put up with in a file that does not decode cleanly.</summary>
/// <param name="Errors">How many error lines ffmpeg wrote while decoding the file (one bad frame is two or three).</param>
/// <param name="DecodedSeconds">How much of the audio ffmpeg got through, in seconds.</param>
/// <param name="DurationSeconds">The duration ffprobe measured, in seconds.</param>
public sealed record ToleratedDecodeErrors(int Errors, double DecodedSeconds, double DurationSeconds);

/// <summary>Measures a downloaded file.</summary>
public interface IMediaProbe
{
    /// <summary>
    /// Probes one file: does it decode, and what is it? Any decode error fails the file, which is what a
    /// file Wondarr writes needs: a damaged download is replaced, never kept.
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The measured format, or why the file cannot be used.</returns>
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Probes a file of a reference library, which is the user's own and is only ever read. A handful of
    /// decode errors in a file of normal length does not stop it being identified (the fingerprint decides
    /// what it is): see <see cref="MediaProbe.ReferenceMaxErrorLines"/> and
    /// <see cref="MediaProbe.ReferenceMinDecodedFraction"/>. A file that decodes cleanly is answered exactly
    /// as <see cref="ProbeAsync"/> answers it, and so is every file by a probe that does not tell the two
    /// apart (this default).
    /// </summary>
    /// <param name="path">The file to probe.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// The measured format (with <see cref="MediaProbeResult.Tolerated"/> set when errors were tolerated),
    /// or why the file cannot be used.
    /// </returns>
    Task<MediaProbeResult> ProbeReferenceAsync(string path, CancellationToken cancellationToken) =>
        ProbeAsync(path, cancellationToken);
}

/// <summary>
/// Wraps the bundled ffprobe (and, when <see cref="MediaToolsOptions.DecodeCheck"/> is on, ffmpeg) to
/// measure what a downloaded file actually is. A file that does not decode is never imported: real
/// format and real bitrate are what the import decision runs on, not the file name
/// (MATCHING_ENGINE.md §6.5). A reference library's files are the one exception
/// (<see cref="ProbeReferenceAsync"/>): they are the user's own, nothing is written to them, and a few
/// bad frames in a file that plays do not stop it being identified.
/// </summary>
public sealed partial class MediaProbe : IMediaProbe
{
    /// <summary>
    /// The most ffmpeg error lines (<c>-v error</c>, one per line) a reference file may produce and still
    /// be scanned. One bad frame is two or three lines, so this is a handful of frames, not a damaged file.
    /// </summary>
    internal const int ReferenceMaxErrorLines = 20;

    /// <summary>
    /// The share of the probed duration ffmpeg must have decoded for a reference file with errors to be
    /// scanned. A file cut short, or one the decoder gave up on part-way, falls below it.
    /// </summary>
    internal const double ReferenceMinDecodedFraction = 0.98;

    private static readonly string[] ProbeArguments =
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams"];

    // -nostdin: ffmpeg must never read a terminal it may have inherited (a prompt would block until
    // the timeout kills it).
    private static readonly string[] DecodeArguments = ["-nostdin", "-v", "error", "-xerror", "-i"];

    // The decode a reference file gets after it failed the strict one: no -xerror, so ffmpeg skips past a
    // bad frame and carries on to the end, and -progress writes key=value lines to stdout, the last
    // out_time_us of which is how far into the file the decode got.
    private static readonly string[] LenientDecodeArguments =
        ["-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", "-i"];

    private readonly IProcessRunner _runner;
    private readonly IOptionsMonitor<MediaToolsOptions> _options;
    private readonly ILogger<MediaProbe> _logger;

    /// <summary>Initialises a new instance of the <see cref="MediaProbe"/> class.</summary>
    /// <param name="runner">Runs ffprobe and ffmpeg without a shell.</param>
    /// <param name="options">The configured binary paths, timeout and decode-check switch.</param>
    /// <param name="logger">Logs parse failures at Debug; never logs stdout at Information.</param>
    public MediaProbe(
        IProcessRunner runner,
        IOptionsMonitor<MediaToolsOptions> options,
        ILogger<MediaProbe> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        ProbeCoreAsync(path, tolerateDecodeErrors: false, cancellationToken);

    /// <inheritdoc />
    public Task<MediaProbeResult> ProbeReferenceAsync(string path, CancellationToken cancellationToken) =>
        ProbeCoreAsync(path, tolerateDecodeErrors: true, cancellationToken);

    private async Task<MediaProbeResult> ProbeCoreAsync(
        string path,
        bool tolerateDecodeErrors,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = _options.CurrentValue;
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        var arguments = new List<string>(ProbeArguments) { path };
        var probe = await _runner.RunAsync(options.FfprobePath, arguments, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (probe.TimedOut)
        {
            return new MediaProbeResult(false, null, FirstErrorLine(probe.StandardError) ?? "ffprobe timed out", TimedOut: true);
        }

        if (probe.ExitCode != 0)
        {
            return Failed(FirstErrorLine(probe.StandardError) ?? $"ffprobe exited {probe.ExitCode}");
        }

        var info = Parse(probe.StandardOutput);
        if (info is null)
        {
            return Failed(FirstErrorLine(probe.StandardError) ?? "the file has no audio stream");
        }

        if (!options.DecodeCheck)
        {
            return new MediaProbeResult(true, info, null);
        }

        // -xerror makes ffmpeg stop at the first decode error instead of skipping past it; -f null
        // throws the decoded audio away. Exit 0 means every sample decoded.
        var decode = await _runner.RunAsync(
                options.FfmpegPath,
                [.. DecodeArguments, path, "-f", "null", "-"],
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (decode.TimedOut)
        {
            return new MediaProbeResult(false, null, FirstErrorLine(decode.StandardError) ?? "ffmpeg timed out", TimedOut: true);
        }

        if (decode.ExitCode == 0)
        {
            return new MediaProbeResult(true, info, null);
        }

        var failure = Failed(FirstErrorLine(decode.StandardError) ?? $"ffmpeg exited {decode.ExitCode}");

        // A download that fails the strict decode is rejected, with that reason. A reference file gets one
        // more look; whatever it does not pass, the verdict stays the strict one.
        return tolerateDecodeErrors
            ? await ToleratedAsync(path, info, options, timeout, cancellationToken).ConfigureAwait(false) ?? failure
            : failure;
    }

    /// <summary>
    /// Decodes a reference file that failed the strict check again, skipping past its bad frames, and says
    /// whether it is within what a reference scan accepts: ffmpeg finished, wrote at most
    /// <see cref="ReferenceMaxErrorLines"/> error lines, and got through at least
    /// <see cref="ReferenceMinDecodedFraction"/> of the duration ffprobe measured.
    /// </summary>
    /// <returns>The file's result, or <c>null</c> when it is beyond that (the strict verdict stands).</returns>
    private async Task<MediaProbeResult?> ToleratedAsync(
        string path,
        MediaInfo info,
        MediaToolsOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Without a duration there is nothing to measure the decode against.
        if (info.DurationMs <= 0)
        {
            return null;
        }

        var decode = await _runner.RunAsync(
                options.FfmpegPath,
                [.. LenientDecodeArguments, path, "-f", "null", "-"],
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (decode.TimedOut)
        {
            return new MediaProbeResult(false, null, FirstErrorLine(decode.StandardError) ?? "ffmpeg timed out", TimedOut: true);
        }

        var errors = ErrorLineCount(decode.StandardError);
        var decoded = DecodedSeconds(decode.StandardOutput);
        var duration = info.DurationMs / 1000.0;

        // A time ffmpeg did not report cannot be measured, so the file is not tolerated.
        if (decode.ExitCode != 0 || errors > ReferenceMaxErrorLines || decoded is not { } seconds
            || seconds < duration * ReferenceMinDecodedFraction)
        {
            return null;
        }

        // Nothing to put up with: the strict run failed for something that did not come back (a share that
        // hiccuped), and this one read the whole file without a word.
        return errors == 0
            ? new MediaProbeResult(true, info, null)
            : new MediaProbeResult(true, info, null, Tolerated: new ToleratedDecodeErrors(errors, seconds, duration));
    }

    /// <summary>The first line of stderr that names a cause rather than an ffmpeg component address.</summary>
    /// <param name="standardError">Everything the tool wrote to stderr.</param>
    /// <remarks>
    /// ffmpeg prefixes decoder chatter with <c>[mp3 @ 0x7d98…]</c> before the line the user needs
    /// (<c>/m/garbage.mp3: Invalid data found when processing input</c>), and a hex address is not a
    /// message: the first line without that prefix is the error.
    /// </remarks>
    internal static string? FirstErrorLine(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        string? firstLine = null;

        foreach (var line in standardError.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            firstLine ??= trimmed;

            if (!trimmed.StartsWith('['))
            {
                return trimmed;
            }
        }

        return firstLine;
    }

    /// <summary>How far into the file ffmpeg got, from the last <c>out_time_us</c> of its <c>-progress</c> output.</summary>
    /// <param name="progress">Everything ffmpeg wrote to stdout.</param>
    /// <returns>The seconds decoded, or <c>null</c> when the output carries no usable time.</returns>
    internal static double? DecodedSeconds(string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
        {
            return null;
        }

        const string Key = "out_time_us=";
        long? microseconds = null;

        foreach (var line in progress.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(Key, StringComparison.Ordinal)
                && long.TryParse(trimmed.AsSpan(Key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                microseconds = value;
            }
        }

        return microseconds / 1_000_000.0;
    }

    /// <summary>How many lines ffmpeg wrote to stderr, which at <c>-v error</c> are all errors.</summary>
    /// <param name="standardError">Everything ffmpeg wrote to stderr.</param>
    internal static int ErrorLineCount(string? standardError) =>
        string.IsNullOrWhiteSpace(standardError)
            ? 0
            : standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static MediaProbeResult Failed(string error) => new(false, null, error);

    /// <summary>Reads what ffprobe printed, or <c>null</c> when there is no audio stream to read.</summary>
    private MediaInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            LogUnreadableOutput(_logger, exception);

            return null;
        }
    }

    private static MediaInfo? Parse(JsonElement root)
    {
        var stream = FirstAudioStream(root);
        if (stream is null)
        {
            return null;
        }

        var format = root.TryGetProperty("format", out var formatElement)
            && formatElement.ValueKind == JsonValueKind.Object
                ? formatElement
                : (JsonElement?)null;

        var codec = Text(stream.Value, "codec_name") ?? "unknown";
        var container = (Text(format, "format_name") ?? string.Empty).Split(',')[0];
        var lossless = MediaCodecs.IsLossless(codec);

        return new MediaInfo(
            codec,
            container,
            BitrateKbps(stream.Value, format, lossless),
            Integer(stream.Value, "sample_rate"),
            BitDepth(stream.Value, lossless),
            Integer(stream.Value, "channels"),
            DurationMs(stream.Value, format),
            lossless,
            Long(format, "size") ?? 0);
    }

    /// <summary>The first audio stream that is not cover art embedded as a video stream.</summary>
    private static JsonElement? FirstAudioStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object
                || !string.Equals(Text(stream, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // An MP3 with a cover is an audio stream and an attached-picture video stream.
            if (stream.TryGetProperty("disposition", out var disposition)
                && Integer(disposition, "attached_pic") == 1)
            {
                continue;
            }

            return stream;
        }

        return null;
    }

    /// <summary>
    /// The overall bitrate in kbps. For a lossless codec the stream bitrate is left out or is the
    /// decoded rate, so the container's figure is the one that describes the file.
    /// </summary>
    private static int? BitrateKbps(JsonElement stream, JsonElement? format, bool lossless)
    {
        var streamBitrate = Long(stream, "bit_rate");
        var formatBitrate = Long(format, "bit_rate");
        var bits = lossless ? formatBitrate ?? streamBitrate : streamBitrate ?? formatBitrate;

        return bits is null || bits <= 0
            ? null
            : (int)Math.Round(bits.Value / 1000.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The bit depth of a lossless codec: the raw sample depth wins over the container's.</summary>
    private static int? BitDepth(JsonElement stream, bool lossless)
    {
        if (!lossless)
        {
            return null;
        }

        var raw = Integer(stream, "bits_per_raw_sample");
        if (raw is > 0)
        {
            return raw;
        }

        var declared = Integer(stream, "bits_per_sample");

        return declared is > 0 ? declared : null;
    }

    private static int DurationMs(JsonElement stream, JsonElement? format)
    {
        var seconds = Decimal(stream, "duration") ?? Decimal(format, "duration");

        return seconds is null ? 0 : (int)Math.Round(seconds.Value * 1000, MidpointRounding.AwayFromZero);
    }

    private static string? Text(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static int? Integer(JsonElement? element, string name) =>
        int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? Long(JsonElement? element, string name) =>
        long.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static decimal? Decimal(JsonElement? element, string name) =>
        decimal.TryParse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffprobe did not print JSON.")]
    private static partial void LogUnreadableOutput(ILogger logger, Exception exception);
}
