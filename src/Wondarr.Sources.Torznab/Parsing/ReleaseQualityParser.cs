// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Parser/QualityParser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Dropped from the original: the QualityModel/Revision wrapper and the proper/repack/version/real
// modifiers (Wondarr has no release revisions), the tag/description path (Wondarr parses only a
// release name) and the file-extension fallback (it needs a file path, not a release name).

using System.Text.RegularExpressions;

namespace Wondarr.Sources.Torznab.Parsing;

/// <summary>
/// Reads the quality a release name claims and maps it to a seeded Wondarr quality id
/// (see <c>src/Wondarr.Core/Domain/SeedData.cs</c>). Pure name parsing: when the name claims
/// nothing the result is the Unknown quality and the probe decides after the download.
/// </summary>
public static class ReleaseQualityParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Seeded quality ids (src/Wondarr.Core/Domain/SeedData.cs).
    private const long QualityUnknown = 1;
    private const long Mp3_032 = 5;
    private const long Mp3_040 = 6;
    private const long Mp3_048 = 7;
    private const long Mp3_056 = 8;
    private const long Mp3_064 = 9;
    private const long Mp3_080 = 10;
    private const long Mp3_096 = 11;
    private const long Mp3_112 = 12;
    private const long Mp3_128 = 13;
    private const long Mp3_160 = 14;
    private const long Mp3_192 = 17;
    private const long Mp3_224 = 18;
    private const long Aac_192 = 19;
    private const long Wma = 21;
    private const long Mp3_256 = 23;
    private const long Mp3VbrV2 = 24;
    private const long Aac_256 = 25;
    private const long VorbisQ5 = 15;
    private const long VorbisQ6 = 20;
    private const long VorbisQ7 = 26;
    private const long VorbisQ8 = 27;
    private const long VorbisQ9 = 33;
    private const long VorbisQ10 = 34;
    private const long Mp3_320 = 29;
    private const long Mp3VbrV0 = 30;
    private const long Aac_320 = 31;
    private const long AacVbr = 32;
    private const long Flac = 36;
    private const long Alac = 37;
    private const long Ape = 38;
    private const long WavPack = 39;
    private const long Flac24 = 40;
    private const long Alac24 = 41;
    private const long Wav = 42;
    private const long Aiff = 43;

    // Lidarr's bitrate regex, extended downwards (32-112 kbps) so every seeded MP3 row has a name
    // that can reach it. The q5-q9 labels are Vorbis quality levels. Lidarr's bracketed
    // alternatives ("[\[\(].*320.*[\]\)]") are dropped: the trailing \b the whole group carries
    // only lets them match when a word character follows the closing bracket, so they never fire
    // in practice - but their nested .* backtracks catastrophically under the 1 s match timeout.
    private static readonly Regex BitRateRegex = new(
        @"\b(?:(?<B032>32[ ]?kbps|32)|
                (?<B040>40[ ]?kbps|40)|
                (?<B048>48[ ]?kbps|48)|
                (?<B056>56[ ]?kbps|56)|
                (?<B064>64[ ]?kbps|64)|
                (?<B080>80[ ]?kbps|80)|
                (?<B096>96[ ]?kbps|96)|
                (?<B112>112[ ]?kbps|112)|
                (?<B128>128[ ]?kbps|128)|
                (?<B160>160[ ]?kbps|160|q5)|
                (?<B192>192[ ]?kbps|192|q6)|
                (?<B224>224[ ]?kbps|224|q7)|
                (?<B256>256[ ]?kbps|256|itunes\splus|q8)|
                (?<B320>320[ ]?kbps|320|q9)|
                (?<B500>500[ ]?kbps|500|q10)|
                (?<VBRV0>V0[ ]?kbps|V0)|
                (?<VBRV2>V2[ ]?kbps|V2))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace,
        MatchTimeout);

    // Lidarr's sample-size regex, extended with the 24/96-style and Hi-Res spellings Wondarr
    // promises to recognise. Lidarr's bracketed alternative is dropped for the same reason as
    // the bitrate ones above: the plain "24bit" alternative already matches inside brackets.
    private static readonly Regex SampleSizeRegex = new(
        @"\b(?:(?<S24>24[-._ ]?bit|flac24(?:[-._ ]?bit)?|tr24|24[-/](?:44|48|96|192)|hi[-._ ]?res))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        MatchTimeout);

    // Lidarr's codec regex, extended with an AIFF group (Wondarr has a seeded AIFF quality).
    private static readonly Regex CodecRegex = new(
        @"\b(?:(?<MP1>MPEG Version \d(.5)? Audio, Layer 1|MP1)|(?<MP2>MPEG Version \d(.5)? Audio, Layer 2|MP2)|(?<MP3VBR>MP3.*VBR|MPEG Version \d(.5)? Audio, Layer 3 vbr)|(?<MP3CBR>MP3|MPEG Version \d(.5)? Audio, Layer 3)|(?<FLAC>(web)?flac(?:24(?:[-._ ]?bit)?)?|TR24)|(?<WAVPACK>wavpack|wv)|(?<ALAC>alac)|(?<WMA>WMA\d?)|(?<WAV>WAV|PCM)|(?<AIFF>AIFF)|(?<AAC>M4A|M4P|M4B|AAC|mp4a|MPEG-4 Audio(?!.*alac))|(?<OGG>OGG|OGA|Vorbis))\b|(?<APE>monkey's audio|[\[|\(].*\bape\b.*[\]|\)])|(?<OPUS>Opus Version \d(.5)? Audio|[\[|\(].*\bopus\b.*[\]|\)])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        MatchTimeout);

    private static readonly Regex WebRegex = new(
        @"\b(?<web>WEB)(?:\b|$|[ .])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        MatchTimeout);

    /// <summary>
    /// Parses the quality a release name claims and returns the matching seeded quality id,
    /// or <c>1</c> (Unknown) when the name claims nothing.
    /// </summary>
    /// <param name="releaseName">The release name as the indexer reported it.</param>
    /// <returns>A Wondarr quality id from <c>src/Wondarr.Core/Domain/SeedData.cs</c>.</returns>
    public static long Parse(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            return QualityUnknown;
        }

        var normalizedName = releaseName.Replace('_', ' ').Trim().ToLowerInvariant();

        var codec = ParseCodec(normalizedName);
        var bitrate = ParseBitRate(normalizedName);
        var sampleSize = ParseSampleSize(normalizedName);

        switch (codec)
        {
            case Codec.MP1:
            case Codec.MP2:
                return QualityUnknown;
            case Codec.MP3VBR:
                if (bitrate == BitRate.VBRV0)
                {
                    return Mp3VbrV0;
                }

                if (bitrate == BitRate.VBRV2)
                {
                    return Mp3VbrV2;
                }

                return QualityUnknown;
            case Codec.MP3CBR:
                switch (bitrate)
                {
                    case BitRate.B032: return Mp3_032;
                    case BitRate.B040: return Mp3_040;
                    case BitRate.B048: return Mp3_048;
                    case BitRate.B056: return Mp3_056;
                    case BitRate.B064: return Mp3_064;
                    case BitRate.B080: return Mp3_080;
                    case BitRate.B096: return Mp3_096;
                    case BitRate.B112: return Mp3_112;
                    case BitRate.B128: return Mp3_128;
                    case BitRate.B160: return Mp3_160;
                    case BitRate.B192: return Mp3_192;
                    case BitRate.B224: return Mp3_224;
                    case BitRate.B256: return Mp3_256;
                    case BitRate.B320: return Mp3_320;
                    default: return QualityUnknown;
                }

            case Codec.FLAC:
                return sampleSize == SampleSize.S24 ? Flac24 : Flac;
            case Codec.ALAC:
                return sampleSize == SampleSize.S24 ? Alac24 : Alac;
            case Codec.WAVPACK:
                return WavPack;
            case Codec.APE:
                return Ape;
            case Codec.WMA:
                return Wma;
            case Codec.WAV:
                return Wav;
            case Codec.AIFF:
                return Aiff;
            case Codec.AAC:
                if (bitrate == BitRate.B192)
                {
                    return Aac_192;
                }

                if (bitrate == BitRate.B256)
                {
                    return Aac_256;
                }

                if (bitrate == BitRate.B320)
                {
                    return Aac_320;
                }

                return AacVbr;
            case Codec.OGG:
            case Codec.OPUS:
                // Lidarr maps Vorbis/Opus by the same bitrate labels; Wondarr maps each onto the
                // nearest seeded Vorbis row.
                switch (bitrate)
                {
                    case BitRate.B160: return VorbisQ5;
                    case BitRate.B192: return VorbisQ6;
                    case BitRate.B224: return VorbisQ7;
                    case BitRate.B256: return VorbisQ8;
                    case BitRate.B320: return VorbisQ9;
                    case BitRate.B500: return VorbisQ10;
                    default: return QualityUnknown;
                }

            case Codec.Unknown:
                if (bitrate == BitRate.B192)
                {
                    return Mp3_192;
                }

                if (bitrate == BitRate.B256)
                {
                    return Mp3_256;
                }

                if (bitrate == BitRate.B320)
                {
                    return Mp3_320;
                }

                // Lidarr's plain-VBR branch never fired from a name (its regex has no bare VBR
                // group); Wondarr routes the V0/V2 presets to their seeded rows instead.
                if (bitrate == BitRate.VBRV0)
                {
                    return Mp3VbrV0;
                }

                if (bitrate == BitRate.VBRV2)
                {
                    return Mp3VbrV2;
                }

                return WebRegex.IsMatch(normalizedName) ? Mp3_320 : QualityUnknown;
            default:
                return QualityUnknown;
        }
    }

    private static Codec ParseCodec(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Codec.Unknown;
        }

        var match = CodecRegex.Match(name);

        if (!match.Success)
        {
            return Codec.Unknown;
        }

        if (match.Groups["FLAC"].Success)
        {
            return Codec.FLAC;
        }

        if (match.Groups["ALAC"].Success)
        {
            return Codec.ALAC;
        }

        if (match.Groups["WMA"].Success)
        {
            return Codec.WMA;
        }

        if (match.Groups["WAV"].Success)
        {
            return Codec.WAV;
        }

        if (match.Groups["AIFF"].Success)
        {
            return Codec.AIFF;
        }

        if (match.Groups["AAC"].Success)
        {
            return Codec.AAC;
        }

        if (match.Groups["OGG"].Success)
        {
            return Codec.OGG;
        }

        if (match.Groups["OPUS"].Success)
        {
            return Codec.OPUS;
        }

        if (match.Groups["MP1"].Success)
        {
            return Codec.MP1;
        }

        if (match.Groups["MP2"].Success)
        {
            return Codec.MP2;
        }

        if (match.Groups["MP3VBR"].Success)
        {
            return Codec.MP3VBR;
        }

        if (match.Groups["MP3CBR"].Success)
        {
            return Codec.MP3CBR;
        }

        if (match.Groups["WAVPACK"].Success)
        {
            return Codec.WAVPACK;
        }

        if (match.Groups["APE"].Success)
        {
            return Codec.APE;
        }

        return Codec.Unknown;
    }

    private static BitRate ParseBitRate(string name)
    {
        var match = BitRateRegex.Match(name);

        if (!match.Success)
        {
            return BitRate.Unknown;
        }

        if (match.Groups["B032"].Success)
        {
            return BitRate.B032;
        }

        if (match.Groups["B040"].Success)
        {
            return BitRate.B040;
        }

        if (match.Groups["B048"].Success)
        {
            return BitRate.B048;
        }

        if (match.Groups["B056"].Success)
        {
            return BitRate.B056;
        }

        if (match.Groups["B064"].Success)
        {
            return BitRate.B064;
        }

        if (match.Groups["B080"].Success)
        {
            return BitRate.B080;
        }

        if (match.Groups["B096"].Success)
        {
            return BitRate.B096;
        }

        if (match.Groups["B112"].Success)
        {
            return BitRate.B112;
        }

        if (match.Groups["B128"].Success)
        {
            return BitRate.B128;
        }

        if (match.Groups["B160"].Success)
        {
            return BitRate.B160;
        }

        if (match.Groups["B192"].Success)
        {
            return BitRate.B192;
        }

        if (match.Groups["B224"].Success)
        {
            return BitRate.B224;
        }

        if (match.Groups["B256"].Success)
        {
            return BitRate.B256;
        }

        if (match.Groups["B320"].Success)
        {
            return BitRate.B320;
        }

        if (match.Groups["B500"].Success)
        {
            return BitRate.B500;
        }

        if (match.Groups["VBRV0"].Success)
        {
            return BitRate.VBRV0;
        }

        if (match.Groups["VBRV2"].Success)
        {
            return BitRate.VBRV2;
        }

        return BitRate.Unknown;
    }

    private static SampleSize ParseSampleSize(string name)
    {
        var match = SampleSizeRegex.Match(name);

        if (!match.Success)
        {
            return SampleSize.Unknown;
        }

        if (match.Groups["S24"].Success)
        {
            return SampleSize.S24;
        }

        return SampleSize.Unknown;
    }

    // Lidarr also has an AACVBR codec value that ParseCodec can never return; it is dropped here.
    private enum Codec
    {
        MP1,
        MP2,
        MP3CBR,
        MP3VBR,
        FLAC,
        ALAC,
        APE,
        WAVPACK,
        WMA,
        AAC,
        OGG,
        OPUS,
        WAV,
        AIFF,
        Unknown,
    }

    private enum BitRate
    {
        B032,
        B040,
        B048,
        B056,
        B064,
        B080,
        B096,
        B112,
        B128,
        B160,
        B192,
        B224,
        B256,
        B320,
        B500,
        VBRV0,
        VBRV2,
        Unknown,
    }

    private enum SampleSize
    {
        S24,
        Unknown,
    }
}
