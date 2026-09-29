using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>The answer to "what is this file really?" (MATCHING_ENGINE.md §6.5 step 1).</summary>
/// <param name="Decodable">Whether ffprobe found audio and ffmpeg decoded all of it.</param>
/// <param name="Info">What was measured, or <c>null</c> when the file is not decodable.</param>
/// <param name="Error">The line that explains a failure, or <c>null</c> when there is none.</param>
public sealed record MediaProbeResult(bool Decodable, MediaInfo? Info, string? Error);

/// <summary>Measures a downloaded file.</summary>
public interface IMediaProbe
{
    /// <summary>Probes one file: does it decode, and what is it?</summary>
    /// <param name="path">The file to probe.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The measured format, or why the file cannot be used.</returns>
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the bundled ffprobe (and, when <see cref="MediaToolsOptions.DecodeCheck"/> is on, ffmpeg) to
/// measure what a downloaded file actually is. A file that does not decode is never imported: real
/// format and real bitrate are what the import decision runs on, not the file name
/// (MATCHING_ENGINE.md §6.5).
/// </summary>
public sealed partial class MediaProbe : IMediaProbe
{
    private static readonly string[] ProbeArguments =
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams"];

    private static readonly string[] DecodeArguments = ["-v", "error", "-xerror", "-i"];

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
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = _options.CurrentValue;
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        var arguments = new List<string>(ProbeArguments) { path };
        var probe = await _runner.RunAsync(options.FfprobePath, arguments, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (probe.TimedOut)
        {
            return Failed(FirstErrorLine(probe.StandardError) ?? "ffprobe timed out");
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
            return Failed(FirstErrorLine(decode.StandardError) ?? "ffmpeg timed out");
        }

        return decode.ExitCode == 0
            ? new MediaProbeResult(true, info, null)
            : Failed(FirstErrorLine(decode.StandardError) ?? $"ffmpeg exited {decode.ExitCode}");
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
