using FluentAssertions;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Parsing;

/// <summary>
/// Golden tests over real-world-style release names: every quality mapping Wondarr promises,
/// including the examples named in the task.
/// </summary>
public class ReleaseQualityParserTests
{
    [Theory]
    [InlineData("Daft Punk - Discovery (2001) [FLAC 24bit-96kHz]", 40L)]
    [InlineData("Massive Attack-Mezzanine-CD-FLAC-1998-FATHEAD", 36L)]
    [InlineData("Artist - Album (2010) MP3 320", 29L)]
    [InlineData("Artist - Album [V0]", 30L)]
    [InlineData("Artist-Album-WEB-2019-MP3", 1L)]
    [InlineData("Artist - Album (2020) [AAC 256]", 25L)]
    // FLAC and its 24-bit spellings.
    [InlineData("Artist - Album (2010) FLAC", 36L)]
    [InlineData("Artist - Album (2010) [FLAC]", 36L)]
    [InlineData("Artist - Album (2010) FLAC 24-bit", 40L)]
    [InlineData("Artist - Album (2010) [FLAC 24 bit]", 40L)]
    [InlineData("Artist - Album (2010) [FLAC 24/96]", 40L)]
    [InlineData("Artist - Album (2010) [FLAC] [24-44]", 40L)]
    [InlineData("Artist - Album (2010) TR24-96", 40L)]
    [InlineData("Artist - Album (2010) [FLAC Hi-Res]", 40L)]
    [InlineData("Artist - Album (2010) [24bit FLAC]", 40L)]
    // ALAC, APE, WavPack, WAV/PCM, AIFF.
    [InlineData("Artist - Album (2010) ALAC", 37L)]
    [InlineData("Artist - Album (2010) [ALAC 24bit]", 41L)]
    [InlineData("Artist - Album (2010) [APE]", 38L)]
    [InlineData("Artist - Album (2010) [Monkey's Audio]", 38L)]
    [InlineData("Artist - Album (2010) [WavPack]", 39L)]
    [InlineData("Artist - Album (2010) WV", 39L)]
    [InlineData("Artist - Album (2010) [WAV]", 42L)]
    [InlineData("Artist - Album (2010) PCM", 42L)]
    [InlineData("Artist - Album (2010) [AIFF]", 43L)]
    [InlineData("Artist - Album (2010) AIFF", 43L)]
    // MP3 CBR by bitrate.
    [InlineData("Artist - Album (2010) MP3 320kbps", 29L)]
    [InlineData("Artist - Album (2010) MP3 256", 23L)]
    [InlineData("Artist - Album (2010) MP3 224", 18L)]
    [InlineData("Artist - Album (2010) MP3 192", 17L)]
    [InlineData("Artist - Album (2010) [MP3 192]", 17L)]
    [InlineData("Artist - Album (2010) MP3 160", 14L)]
    [InlineData("Artist - Album (2010) MP3 128", 13L)]
    [InlineData("Artist - Album (2010) MP3 112", 12L)]
    [InlineData("Artist - Album (2010) [MP3 96]", 11L)]
    [InlineData("Artist - Album (2010) MP3 64", 9L)]
    [InlineData("Artist - Album (2010) MP3 32", 5L)]
    // MP3 VBR presets and the no-bitrate fallback to Unknown.
    [InlineData("Artist - Album [V2]", 24L)]
    [InlineData("Artist - Album (2010) MP3 VBR V0", 30L)]
    [InlineData("Artist - Album (2010) MP3 VBR", 1L)]
    [InlineData("Artist - Album (2010) MP3", 1L)]
    // AAC.
    [InlineData("Artist - Album (2020) AAC 320", 31L)]
    [InlineData("Artist - Album (2020) AAC 192", 19L)]
    [InlineData("Artist - Album (2020) [M4A 256]", 25L)]
    [InlineData("Artist - Album (2020) AAC VBR", 32L)]
    [InlineData("Artist - Album (2020) AAC", 32L)]
    // Vorbis and Opus by Lidarr's bitrate labels.
    [InlineData("Artist - Album (2010) OGG Q8", 27L)]
    [InlineData("Artist - Album (2010) OGG Q6", 20L)]
    [InlineData("Artist - Album (2010) OGG 500", 34L)]
    [InlineData("Artist - Album (2010) OGG", 1L)]
    [InlineData("Artist - Album (2010) [Opus]", 1L)]
    // WMA.
    [InlineData("Artist - Album (2010) [WMA]", 21L)]
    [InlineData("Artist - Album (2010) WMA", 21L)]
    // No codec claimed: Lidarr's bitrate and WEB fallbacks.
    [InlineData("Artist - Album (2010) 320", 29L)]
    [InlineData("Artist - Album (2010) 256", 23L)]
    [InlineData("Artist - Album (2010) 192", 17L)]
    [InlineData("Artist-Album-WEB-2019", 29L)]
    [InlineData("Artist - Album (2010)", 1L)]
    [InlineData("", 1L)]
    public void Maps_the_release_name_to_the_seeded_quality_id(string releaseName, long expected)
    {
        ReleaseQualityParser.Parse(releaseName).Should().Be(expected);
    }
}
