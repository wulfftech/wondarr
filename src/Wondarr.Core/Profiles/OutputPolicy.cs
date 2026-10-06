using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wondarr.Core.Profiles;

/// <summary>What a library turns a YouTube-sourced file into (ADR-0008).</summary>
public enum OutputCodec
{
    /// <summary>Import the Opus remux as it is; no transcode runs.</summary>
    KeepOpus,

    /// <summary>Transcode to AAC in an <c>.m4a</c>.</summary>
    Aac,

    /// <summary>Transcode to MP3, CBR or LAME VBR.</summary>
    Mp3,
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
/// The per-library output policy for YouTube-sourced files (ADR-0008): a YouTube download is the
/// lossless remux of itag 251 (<c>.opus</c>), and the policy says what that file becomes before it is
/// verified, tagged and placed. Stored as JSON text in <c>library.output_policy</c>; a library
/// without one uses <see cref="Default"/> — AAC 256 kbps CBR in an <c>.m4a</c>, the source sample
/// rate kept. A Soulseek file is never transcoded.
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

    /// <summary>Gets the container the target file is written in.</summary>
    public string Container => Codec switch
    {
        OutputCodec.KeepOpus => "opus",
        OutputCodec.Aac => "m4a",
        _ => "mp3",
    };

    /// <summary>Gets the policy a library without one uses: AAC 256 CBR, <c>.m4a</c>, sample rate kept.</summary>
    public static OutputPolicy Default { get; } = new();

    /// <summary>
    /// Parses the JSON a <c>library.output_policy</c> column holds. Missing keys keep their
    /// defaults, so <c>{}</c> and <see langword="null"/> both mean <see cref="Default"/>.
    /// </summary>
    /// <param name="json">The stored JSON text, or <see langword="null"/>.</param>
    /// <returns>The policy it describes.</returns>
    /// <exception cref="ProfileValidationException">
    /// The JSON is not an object, or a key holds a value the policy cannot: every problem names the
    /// JSON key it came from.
    /// </exception>
    public static OutputPolicy Parse(string? json)
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
                        policy.Codec = ParseCodec(entry.Value, errors);
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

                    default:
                        errors.Add(("outputPolicy", $"outputPolicy has an unknown key '{entry.Name}'."));
                        break;
                }
            }

            // The LAME quality scale is MP3's; AAC is written at a bitrate, so the combination
            // would silently mean something the JSON does not say.
            if (policy.Codec == OutputCodec.Aac && policy.Mode == OutputMode.Vbr)
            {
                errors.Add(("mode", "mode 'vbr' is only supported for codec 'mp3' (the LAME quality scale); use 'cbr' for aac."));
            }

            if (errors.Count > 0)
            {
                throw new ProfileValidationException(errors);
            }

            return policy;
        }
    }

    /// <summary>Serialises the policy back to the JSON the column holds, every key spelled out.</summary>
    /// <returns>CamelCase JSON, one line, no whitespace.</returns>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("codec", Codec switch
            {
                OutputCodec.KeepOpus => "keepOpus",
                OutputCodec.Aac => "aac",
                _ => "mp3",
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

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static OutputCodec ParseCodec(JsonElement value, List<(string Property, string Message)> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(("codec", "codec must be one of keepOpus, aac or mp3."));

            return OutputCodec.Aac;
        }

        var codec = value.GetString()!;

        if (string.Equals(codec, "keepOpus", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.KeepOpus;
        }

        if (string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Aac;
        }

        if (string.Equals(codec, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return OutputCodec.Mp3;
        }

        // A lossless target is refused with the reason said out loud (ADR-0006): the YouTube source
        // is already lossy, so a lossless target would only waste space.
        if (LosslessContainers.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add((
                "codec",
                $"codec '{codec.ToLowerInvariant()}' is lossless: the YouTube source is already lossy, so a lossless target is refused (ADR-0006)."));
        }
        else
        {
            errors.Add(("codec", $"codec must be one of keepOpus, aac or mp3 (was '{codec}')."));
        }

        return OutputCodec.Aac;
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
