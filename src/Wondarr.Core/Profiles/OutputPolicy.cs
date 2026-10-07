using System.Globalization;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Profiles;

/// <summary>What one output rule turns a file into (ADR-0008).</summary>
public enum OutputCodec
{
    /// <summary>Import the file as it is; no transcode runs.</summary>
    Keep,

    /// <summary>Transcode to AAC in an <c>.m4a</c>.</summary>
    Aac,

    /// <summary>Transcode to MP3, CBR or LAME VBR.</summary>
    Mp3,

    /// <summary>Transcode to Opus in an <c>.opus</c> or <c>.ogg</c> container.</summary>
    Opus,

    /// <summary>Transcode to FLAC in a <c>.flac</c> (lossless sources only).</summary>
    Flac,

    /// <summary>Transcode to ALAC in an <c>.m4a</c> (lossless sources only).</summary>
    Alac,
}

/// <summary>How the target bitrate is chosen.</summary>
public enum OutputMode
{
    /// <summary>A fixed bitrate, <c>-b:a</c>.</summary>
    Cbr,

    /// <summary>The LAME quality scale 0–9, <c>-q:a</c> (MP3 only).</summary>
    Vbr,
}

/// <summary>
/// One output rule of a library's output policy (ADR-0008): what a file of one source class —
/// YouTube, lossy, lossless — becomes before it is verified, tagged and placed. Stored as JSON
/// text inside <c>library.output_policy</c> (see <see cref="LibraryOutputPolicy"/>); the YouTube
/// settings' default policy is a single rule of this shape.
/// </summary>
public sealed class OutputPolicy
{
    /// <summary>The lowest accepted <see cref="BitrateKbps"/>.</summary>
    public const int MinBitrateKbps = 64;

    /// <summary>The highest accepted <see cref="BitrateKbps"/>.</summary>
    public const int MaxBitrateKbps = 320;

    /// <summary>The lowest LAME VBR quality number (best quality).</summary>
    public const int MinVbrQuality = 0;

    /// <summary>The highest LAME VBR quality number (smallest file).</summary>
    public const int MaxVbrQuality = 9;

    /// <summary>The container extensions a lossless target is refused in (ADR-0006: never lossless from lossy).</summary>
    internal static readonly string[] LosslessContainers = ["flac", "alac", "wav", "ape", "wv"];

    /// <summary>Gets or sets what the file becomes.</summary>
    public OutputCodec Codec { get; set; } = OutputCodec.Aac;

    /// <summary>Gets or sets how the target bitrate is chosen.</summary>
    public OutputMode Mode { get; set; } = OutputMode.Cbr;

    /// <summary>Gets or sets the constant bitrate in kbps.</summary>
    public int BitrateKbps { get; set; } = 256;

    /// <summary>Gets or sets the LAME VBR quality, 0 (best) to 9 (smallest).</summary>
    public int VbrQuality { get; set; }

    /// <summary>
    /// Gets or sets the target sample rate in Hz, or <see langword="null"/> to keep the source's.
    /// </summary>
    public int? SampleRateHz { get; set; }

    /// <summary>
    /// Gets or sets the container an Opus target — or a kept Opus file — is named with:
    /// <c>opus</c> or <c>ogg</c>. An <c>.opus</c> file is already an Ogg container, so
    /// <c>ogg</c> is a rename, never a re-encode.
    /// </summary>
    public string OpusContainer { get; set; } = "opus";

    /// <summary>
    /// Gets the container the target file is written in, without the dot. A <see cref="OutputCodec.Keep"/>
    /// rule converts nothing, so it answers <c>keep</c>: callers must check
    /// <c>Codec == OutputCodec.Keep</c> before they use this as an extension.
    /// </summary>
    public string Container => Codec switch
    {
        OutputCodec.Keep => "keep",
        OutputCodec.Aac => "m4a",
        OutputCodec.Mp3 => "mp3",
        OutputCodec.Opus => OpusContainer,
        OutputCodec.Flac => "flac",
        _ => "m4a",
    };

    /// <summary>Gets the rule a missing one falls back to: AAC 256 CBR, <c>.m4a</c>, sample rate kept.</summary>
    public static OutputPolicy Default { get; } = new();

    /// <summary>
    /// Parses the JSON one rule is stored as. Missing keys keep their defaults, so <c>{}</c> and
    /// <see langword="null"/> both mean <see cref="Default"/>.
    /// </summary>
    /// <param name="json">The stored JSON text, or <see langword="null"/>.</param>
    /// <returns>The rule it describes.</returns>
    /// <exception cref="ProfileValidationException">
    /// The JSON is not an object, or a key holds a value the rule cannot: every problem names the
    /// JSON key it came from.
    /// </exception>
    public static OutputPolicy Parse(string? json) => Parse(json, allowLossless: false);

    /// <summary>
    /// Parses the JSON one rule is stored as, optionally allowing a lossless target: a lossless
    /// source may be converted to FLAC or ALAC, a lossy one never is (ADR-0006).
    /// </summary>
    /// <param name="json">The stored JSON text, or <see langword="null"/>.</param>
    /// <param name="allowLossless">Whether the lossless codecs <c>flac</c> and <c>alac</c> are accepted.</param>
    /// <returns>The rule it describes.</returns>
    /// <exception cref="ProfileValidationException">
    /// The JSON is not an object, or a key holds a value the rule cannot: every problem names the
    /// JSON key it came from.
    /// </exception>
    public static OutputPolicy Parse(string? json, bool allowLossless)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ProfileValidationException([("outputPolicy", $"outputPolicy is not valid JSON: {exception.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProfileValidationException([("outputPolicy", "outputPolicy must be a JSON object.")]);
            }

            var policy = new OutputPolicy();
            var errors = new List<(string Property, string Message)>();

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                switch (entry.Name)
                {
                    case "codec":
                        policy.Codec = ParseCodec(entry.Value, allowLossless, errors);
                        break;

                    case "mode":
                        policy.Mode = ParseMode(entry.Value, errors);
                        break;

                    case "bitrateKbps":
                        policy.BitrateKbps = ParseNumber(
                            entry.Value,
                            "bitrateKbps",
                            MinBitrateKbps,
                            MaxBitrateKbps,
                            errors);
                        break;

                    case "vbrQuality":
                        policy.VbrQuality = ParseNumber(
                            entry.Value,
                            "vbrQuality",
                            MinVbrQuality,
                            MaxVbrQuality,
                            errors);
                        break;

                    case "sampleRate":
                        policy.SampleRateHz = ParseSampleRate(entry.Value, errors);
                        break;

                    case "opusContainer":
                        policy.OpusContainer = ParseOpusContainer(entry.Value, errors);
                        break;

                    default:
                        errors.Add(("outputPolicy", $"outputPolicy has an unknown key '{entry.Name}'."));
                        break;
                }
            }

            // The LAME quality scale is MP3's; AAC and Opus are written at a bitrate, so the
            // combination would silently mean something the JSON does not say. FLAC and ALAC
            // ignore the mode entirely — their encoders have no bitrate to choose.
            if (policy.Mode == OutputMode.Vbr
                && policy.Codec is OutputCodec.Aac or OutputCodec.Opus)
            {
                errors.Add(("mode", "mode 'vbr' is only supported for codec 'mp3' (the LAME quality scale); use 'cbr'."));
            }

            if (errors.Count > 0)
            {
                throw new ProfileValidationException(errors);
            }

            return policy;
        }
    }

    /// <summary>Serialises the rule back to the JSON the column holds, every key spelled out.</summary>
    /// <returns>CamelCase JSON, one line, no whitespace.</returns>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("codec", Codec switch
            {
                OutputCodec.Keep => "keep",
                OutputCodec.Aac => "aac",
                OutputCodec.Mp3 => "mp3",
                OutputCodec.Opus => "opus",
                OutputCodec.Flac => "flac",
                _ => "alac",
            });
            writer.WriteString("mode", Mode == OutputMode.Cbr ? "cbr" : "vbr");
            writer.WriteNumber("bitrateKbps", BitrateKbps);
            writer.WriteNumber("vbrQuality", VbrQuality);

            if (SampleRateHz is { } rate)
            {
                writer.WriteNumber("sampleRate", rate);
            }
            else
            {
                writer.WriteString("sampleRate", "keep");
            }

            // The container only matters to the two codecs that can name an Opus file .ogg.
            if (Codec is OutputCodec.Keep or OutputCodec.Opus)
            {
                writer.WriteString("opusContainer", OpusContainer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static OutputCodec ParseCodec(
        JsonElement value,
        bool allowLossless,
        List<(string Property, string Message)> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(("codec", "codec must be one of keep, aac, mp3, opus, flac or alac."));

            return OutputCodec.Aac;
        }

        var codec = value.GetString()!;

        // The pre-v2 wire name of "keep", still accepted so a stored policy keeps working.
        if (string.Equals(codec, "keepOpus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codec, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Keep;
        }

        if (string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Aac;
        }

        if (string.Equals(codec, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Mp3;
        }

        if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Opus;
        }

        if (string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase))
        {
            return LosslessTarget("flac", allowLossless, OutputCodec.Flac, errors);
        }

        if (string.Equals(codec, "alac", StringComparison.OrdinalIgnoreCase))
        {
            return LosslessTarget("alac", allowLossless, OutputCodec.Alac, errors);
        }

        // A lossless target is refused with the reason said out loud (ADR-0006): the source is
        // already lossy, so a lossless target would only waste space.
        if (LosslessContainers.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add((
                "codec",
                $"codec '{codec.ToLowerInvariant()}' is lossless: the source is already lossy, so a lossless target is refused (ADR-0006)."));
        }
        else
        {
            errors.Add(("codec", $"codec must be one of keep, aac, mp3, opus, flac or alac (was '{codec}')."));
        }

        return OutputCodec.Aac;
    }

    /// <summary>
    /// Accepts a lossless codec only where a lossless source makes it honest (ADR-0006); the raw
    /// PCM and compressed-but-rare formats are never a target Wondarr writes.
    /// </summary>
    private static OutputCodec LosslessTarget(
        string codec,
        bool allowLossless,
        OutputCodec parsed,
        List<(string Property, string Message)> errors)
    {
        if (allowLossless)
        {
            return parsed;
        }

        errors.Add((
            "codec",
            $"codec '{codec}' is lossless: the source is already lossy, so a lossless target is refused (ADR-0006)."));

        return OutputCodec.Aac;
    }

    private static string ParseOpusContainer(JsonElement value, List<(string Property, string Message)> errors)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var container = value.GetString()!;

            if (string.Equals(container, "opus", StringComparison.OrdinalIgnoreCase))
            {
                return "opus";
            }

            if (string.Equals(container, "ogg", StringComparison.OrdinalIgnoreCase))
            {
                return "ogg";
            }
        }

        errors.Add(("opusContainer", $"opusContainer must be 'opus' or 'ogg' (was '{value.ToString()}')."));

        return "opus";
    }

    private static OutputMode ParseMode(JsonElement value, List<(string Property, string Message)> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(("mode", "mode must be cbr or vbr."));

            return OutputMode.Cbr;
        }

        var mode = value.GetString()!;

        if (string.Equals(mode, "cbr", StringComparison.OrdinalIgnoreCase))
        {
            return OutputMode.Cbr;
        }

        if (string.Equals(mode, "vbr", StringComparison.OrdinalIgnoreCase))
        {
            return OutputMode.Vbr;
        }

        errors.Add(("mode", $"mode must be cbr or vbr (was '{mode}')."));

        return OutputMode.Cbr;
    }

    private static int ParseNumber(
        JsonElement value,
        string key,
        int minimum,
        int maximum,
        List<(string Property, string Message)> errors)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            errors.Add((key, $"{key} must be a whole number."));

            return minimum;
        }

        if (number < minimum || number > maximum)
        {
            errors.Add((key, $"{key} must be between {minimum} and {maximum} (was {number})."));

            return minimum;
        }

        return number;
    }

    private static int? ParseSampleRate(JsonElement value, List<(string Property, string Message)> errors)
    {
        if (value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), "keep", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var rate))
        {
            if (rate <= 0)
            {
                errors.Add(("sampleRate", $"sampleRate must be 'keep' or a sample rate in Hz, above 0 (was {rate.ToString(CultureInfo.InvariantCulture)})."));

                return null;
            }

            return rate;
        }

        errors.Add(("sampleRate", "sampleRate must be 'keep' or a sample rate in Hz."));

        return null;
    }
}

/// <summary>
/// A library's output policy, version 2 (ADR-0008): one <see cref="OutputPolicy"/> rule per source
/// class — <c>youtube</c>, <c>lossy</c>, <c>lossless</c> — and the import applies the matching rule
/// to every download, whatever the source. Stored as JSON text in <c>library.output_policy</c>; a
/// library without one uses <see cref="Default"/>. A stored version-1 policy (the flat
/// YouTube-only shape) is still read: it is the <c>youtube</c> rule, the other two <c>keep</c>.
/// </summary>
public sealed class LibraryOutputPolicy
{
    /// <summary>Gets the rule a YouTube download is converted per.</summary>
    public OutputPolicy YouTube { get; private init; } = OutputPolicy.Default;

    /// <summary>Gets the rule a lossy download from any other source is converted per.</summary>
    public OutputPolicy Lossy { get; private init; } = Keep();

    /// <summary>Gets the rule a lossless download from any other source is converted per.</summary>
    public OutputPolicy Lossless { get; private init; } = Keep();

    /// <summary>
    /// Gets the policy a library without one uses: YouTube downloads become AAC 256 kbps CBR in an
    /// <c>.m4a</c>, everything else is imported as it was served.
    /// </summary>
    public static LibraryOutputPolicy Default { get; } = new();

    /// <summary>A rule that converts nothing.</summary>
    private static OutputPolicy Keep() => new() { Codec = OutputCodec.Keep };

    /// <summary>
    /// Parses the JSON a <c>library.output_policy</c> column holds. A JSON object with
    /// <c>version: 2</c> carries the three rules (<c>youtube</c>, <c>lossy</c>, <c>lossless</c>,
    /// each optional); an object without a <c>version</c> is a version-1 policy, which is the
    /// YouTube rule. Missing keys and a blank column both mean <see cref="Default"/>.
    /// </summary>
    /// <param name="json">The stored JSON text, or <see langword="null"/>.</param>
    /// <returns>The policy it describes.</returns>
    /// <exception cref="ProfileValidationException">
    /// The JSON is not an object, or a key holds a value a rule cannot: every problem names the
    /// JSON key it came from, prefixed with the rule (<c>youtube.codec</c>, <c>lossless.bitrateKbps</c>, …).
    /// </exception>
    public static LibraryOutputPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ProfileValidationException([("outputPolicy", $"outputPolicy is not valid JSON: {exception.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProfileValidationException([("outputPolicy", "outputPolicy must be a JSON object.")]);
            }

            if (document.RootElement.TryGetProperty("version", out var version))
            {
                if (version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var number)
                    || number != 2)
                {
                    throw new ProfileValidationException([("version", "version must be 2.")]);
                }

                return ParseVersion2(document.RootElement);
            }

            // A version-1 policy is the flat, YouTube-only shape: it is the youtube rule.
            return new LibraryOutputPolicy { YouTube = ParseRule(json, "youtube", allowLossless: false) };
        }
    }

    /// <summary>Reads the three rules of a version-2 object, each of them optional.</summary>
    private static LibraryOutputPolicy ParseVersion2(JsonElement root)
    {
        var youtube = OutputPolicy.Default;
        var lossy = Keep();
        var lossless = Keep();
        var errors = new List<(string Property, string Message)>();

        foreach (var entry in root.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "version":
                    // Checked by the caller: it is what made this a version-2 policy.
                    break;

                case "youtube":
                    youtube = ParseRule(entry.Value.GetRawText(), "youtube", allowLossless: false);
                    break;

                case "lossy":
                    lossy = ParseRule(entry.Value.GetRawText(), "lossy", allowLossless: false);
                    break;

                case "lossless":
                    lossless = ParseRule(entry.Value.GetRawText(), "lossless", allowLossless: true);
                    break;

                default:
                    errors.Add(("outputPolicy", $"outputPolicy has an unknown key '{entry.Name}'."));
                    break;
            }
        }

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        return new LibraryOutputPolicy { YouTube = youtube, Lossy = lossy, Lossless = lossless };
    }

    /// <summary>Parses one rule, prefixing every problem it reports with the rule's name.</summary>
    private static OutputPolicy ParseRule(string json, string rule, bool allowLossless)
    {
        try
        {
            return OutputPolicy.Parse(json, allowLossless);
        }
        catch (ProfileValidationException exception)
        {
            throw new ProfileValidationException(
                [.. exception.Errors.Select(error => ($"{rule}.{error.Property}", error.Message))]);
        }
    }

    /// <summary>Serialises the policy back to the JSON the column holds: version 2, every rule spelled out.</summary>
    /// <returns>CamelCase JSON, one line, no whitespace.</returns>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            WriteRule(writer, "youtube", YouTube);
            WriteRule(writer, "lossy", Lossy);
            WriteRule(writer, "lossless", Lossless);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes one rule as a nested object of the policy's JSON.</summary>
    private static void WriteRule(Utf8JsonWriter writer, string name, OutputPolicy rule)
    {
        writer.WritePropertyName(name);

        using var document = JsonDocument.Parse(rule.ToJson());

        document.RootElement.WriteTo(writer);
    }

    /// <summary>
    /// The rule a download of the given source class is converted per: YouTube downloads take the
    /// YouTube rule, everything else the lossless or lossy rule by what the probe measured.
    /// </summary>
    /// <param name="sourceType">The wire name of the source the file came from.</param>
    /// <param name="sourceIsLossless">Whether the downloaded file itself is lossless.</param>
    public OutputPolicy RuleFor(string sourceType, bool sourceIsLossless) =>
        sourceType == SourceTypes.YouTube
            ? YouTube
            : sourceIsLossless ? Lossless : Lossy;

    /// <summary>
    /// Whether a file is already in the rule's target codec, by the ffprobe codec name: such a
    /// file is kept as it is, never re-encoded. A <see cref="OutputCodec.Keep"/> rule has no target
    /// codec — callers check it first.
    /// </summary>
    /// <param name="rule">The rule the file would be converted per.</param>
    /// <param name="probedCodec">The codec name ffprobe measured, for example <c>flac</c>.</param>
    public static bool SameCodec(OutputPolicy rule, string probedCodec) => rule.Codec switch
    {
        OutputCodec.Aac => Is(probedCodec, "aac"),
        OutputCodec.Mp3 => Is(probedCodec, "mp3"),
        OutputCodec.Opus => Is(probedCodec, "opus"),
        OutputCodec.Flac => Is(probedCodec, "flac"),
        OutputCodec.Alac => Is(probedCodec, "alac"),
        _ => false,
    };

    private static bool Is(string probedCodec, string codec) =>
        string.Equals(probedCodec, codec, StringComparison.OrdinalIgnoreCase);
}
