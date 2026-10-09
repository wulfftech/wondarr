namespace Wondarr.Core.Sources;

/// <summary>
/// Infers the quality of a Soulseek result from the attributes slskd reports about it — the extension,
/// the bitrate, the bit depth, the length and the size — before anything is downloaded
/// (QUALITY_DEFINITIONS.md; the ids are the <c>quality</c> seed's, a stable contract).
/// </summary>
/// <remarks>
/// The rules and the golden cases are pinned by <c>tests/fixtures/soulseek-quality.json</c>. A result
/// that says nothing usable is <c>1</c> (Unknown) rather than a guess, because the decision engine
/// scores it as such; the measured quality replaces this one after the download.
/// </remarks>
public static class SoulseekQuality
{
    /// <summary>The quality a result nobody can say anything about gets.</summary>
    public const long Unknown = 1;

    /// <summary>
    /// The CBR bitrates an MP3 can declare, worst first, each with the quality it maps to in the seed.
    /// </summary>
    private static readonly (int Bitrate, long QualityId)[] Mp3CbrLadder =
    [
        (8, 2),
        (16, 3),
        (24, 4),
        (32, 5),
        (40, 6),
        (48, 7),
        (56, 8),
        (64, 9),
        (80, 10),
        (96, 11),
        (112, 12),
        (128, 13),
        (160, 14),
        (192, 17),
        (224, 18),
        (256, 23),
        (320, 29),
    ];

    /// <summary>A computed bitrate counts as a CBR value within this fraction of it.</summary>
    private const double CbrTolerance = 0.03;

    /// <summary>
    /// Looks up the quality id of one result (QUALITY_DEFINITIONS.md). Nothing is downloaded: the
    /// answer comes from what the source already reports.
    /// </summary>
    /// <param name="extension">Lower-case extension without the dot, or <c>null</c>.</param>
    /// <param name="bitRateKbps">The bitrate the source reports, in kbps.</param>
    /// <param name="isVariableBitRate">Whether the source says the file is variable bitrate.</param>
    /// <param name="sampleRate">The sample rate in Hz — reported for the record, not used by the rules.</param>
    /// <param name="bitDepth">The bit depth, which only lossless formats carry.</param>
    /// <param name="lengthSeconds">The duration in seconds, when the source reports one.</param>
    /// <param name="sizeBytes">The size in bytes, when the source reports one.</param>
    /// <returns>The quality id (<c>quality</c> seed, <c>1</c> = Unknown).</returns>
    public static long Infer(
        string? extension,
        int? bitRateKbps,
        bool? isVariableBitRate,
        int? sampleRate,
        int? bitDepth,
        int? lengthSeconds,
        long? sizeBytes)
    {
        var format = Normalize(extension);
        if (format is null)
        {
            return Unknown;
        }

        switch (format)
        {
            case "flac":
                return bitDepth >= 24 ? 40 : 36;

            case "m4a":
            case "alac":
                if (bitDepth is not null)
                {
                    return bitDepth >= 24 ? 41 : 37;
                }

                // No bit depth: an .m4a holds AAC or ALAC. No AAC encoder runs anywhere near 500 kbps, so a
                // file that dense is ALAC (archive.org's "Apple Lossless" items list just name and size).
                // The probe after the download measures the real codec either way.
                var m4aBitrate = EffectiveBitrate(bitRateKbps, lengthSeconds, sizeBytes);

                return m4aBitrate >= AlacMinimumKbps ? 37 : InferAac(m4aBitrate);

            case "aac":
                return InferAac(EffectiveBitrate(bitRateKbps, lengthSeconds, sizeBytes));

            case "wav":
                return 42;

            case "aif":
            case "aiff":
                return 43;

            case "ape":
                return 38;

            case "wv":
                return 39;

            case "wma":
                return 21;

            case "mp3":
                return InferMp3(bitRateKbps, isVariableBitRate, lengthSeconds, sizeBytes);

            case "ogg":
            case "oga":
                return InferVorbis(EffectiveBitrate(bitRateKbps, lengthSeconds, sizeBytes));

            case "opus":
                return InferOpus(EffectiveBitrate(bitRateKbps, lengthSeconds, sizeBytes));

            default:
                return Unknown;
        }
    }

    /// <summary>The MP3 rules: a declared CBR value wins, a computed one counts within ±3 %.</summary>
    private static long InferMp3(int? bitRateKbps, bool? isVariableBitRate, int? lengthSeconds, long? sizeBytes)
    {
        if (bitRateKbps is not null)
        {
            if (isVariableBitRate != true)
            {
                var declared = CbrQuality(bitRateKbps.Value);
                if (declared is not null)
                {
                    return declared.Value;
                }
            }

            return InferVariableBitrate(bitRateKbps.Value);
        }

        var computed = EffectiveBitrate(null, lengthSeconds, sizeBytes);
        if (computed is null)
        {
            return Unknown;
        }

        var rounded = NearestCbr((double)computed.Value);
        if (rounded is not null && Math.Abs(rounded.Value.Bitrate - computed.Value) <= rounded.Value.Bitrate * CbrTolerance)
        {
            return rounded.Value.QualityId;
        }

        return InferVariableBitrate(computed.Value);
    }

    /// <summary>V0 from 220 kbps up, V2 from 170, and the best CBR tier below that.</summary>
    private static long InferVariableBitrate(int bitrateKbps)
    {
        if (bitrateKbps >= 220)
        {
            return 30;
        }

        if (bitrateKbps >= 170)
        {
            return 24;
        }

        var best = Unknown;
        foreach (var entry in Mp3CbrLadder)
        {
            if (entry.Bitrate <= bitrateKbps)
            {
                best = entry.QualityId;
            }
        }

        return best;
    }

    /// <summary>The bitrate from which an .m4a without a bit depth is read as ALAC rather than AAC.</summary>
    private const int AlacMinimumKbps = 500;

    /// <summary>The AAC tiers. m4a and alac only get here when the file carries no bit depth.</summary>
    private static long InferAac(int? bitrateKbps) => bitrateKbps switch
    {
        null => Unknown,
        >= 300 => 31,
        >= 240 => 25,
        >= 180 => 19,
        _ => Unknown,
    };

    /// <summary>Vorbis quality tiers: Q5 below 180 kbps up to Q10 from 340.</summary>
    private static long InferVorbis(int? bitrateKbps) => bitrateKbps switch
    {
        null => Unknown,
        < 180 => 15,
        < 210 => 20,
        < 240 => 26,
        < 280 => 27,
        < 340 => 33,
        _ => 34,
    };

    /// <summary>Opus tiers, on the same ladder a transcoded YouTube file lands on.</summary>
    private static long InferOpus(int? bitrateKbps) => bitrateKbps switch
    {
        null => Unknown,
        < 112 => 16,
        < 144 => 22,
        < 176 => 28,
        _ => 35,
    };

    /// <summary>The quality of a declared CBR bitrate, or <c>null</c> when it is not one.</summary>
    private static long? CbrQuality(int bitrateKbps)
    {
        foreach (var entry in Mp3CbrLadder)
        {
            if (entry.Bitrate == bitrateKbps)
            {
                return entry.QualityId;
            }
        }

        return null;
    }

    /// <summary>The CBR bitrate nearest a computed one.</summary>
    private static (int Bitrate, long QualityId)? NearestCbr(double bitrateKbps)
    {
        (int Bitrate, long QualityId)? nearest = null;

        foreach (var entry in Mp3CbrLadder)
        {
            if (nearest is null || Math.Abs(entry.Bitrate - bitrateKbps) < Math.Abs(nearest.Value.Bitrate - bitrateKbps))
            {
                nearest = entry;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The bitrate to reason about: what the source reports, or what the size and the length imply
    /// (<c>size × 8 / length / 1000</c> kbps). <c>null</c> when neither is known.
    /// </summary>
    private static int? EffectiveBitrate(int? bitRateKbps, int? lengthSeconds, long? sizeBytes)
    {
        if (bitRateKbps is not null)
        {
            return bitRateKbps;
        }

        if (lengthSeconds is null or <= 0 || sizeBytes is null or <= 0)
        {
            return null;
        }

        return (int)Math.Round(sizeBytes.Value * 8.0 / lengthSeconds.Value / 1000.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Lower-cases the extension, drops a leading dot and treats "none" as none.</summary>
    private static string? Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var trimmed = extension.Trim().TrimStart('.');

        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }
}
