using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.References;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// The likely-YouTube-rip rule and its rating (DECISIONS build session 14 #32). The probes are the shapes
/// measured on the owner's library: Hip to Be Square (an ffmpeg MP3 at 48 kHz), an Opus download, a CD rip
/// and a 48 kHz studio FLAC.
/// </summary>
public sealed class YouTubeRipsTests
{
    private static readonly MediaInfo Rip = new("mp3", "mp3", 268, 48_000, null, 2, 231_811, false, 7_857_630);
    private static readonly MediaInfo Opus = new("opus", "ogg", 160, 48_000, null, 2, 231_811, false, 4_600_000);
    private static readonly MediaInfo CdRip = new("mp3", "mp3", 320, 44_100, null, 2, 335_973, false, 13_400_000);
    private static readonly MediaInfo StudioFlac = new("flac", "flac", 2_300, 48_000, 24, 2, 231_811, true, 66_000_000);
    private static readonly MediaInfo PoorRip = new("mp3", "mp3", 128, 48_000, null, 2, 231_811, false, 3_700_000);

    [Fact]
    public void A_lossy_file_at_48_kHz_is_a_likely_rip_and_nothing_else_is()
    {
        YouTubeRips.IsLikely(Rip).Should().BeTrue();
        YouTubeRips.IsLikely(Opus).Should().BeTrue();
        YouTubeRips.IsLikely(PoorRip).Should().BeTrue();
        YouTubeRips.IsLikely(CdRip).Should().BeFalse("CDs and stores are 44.1 kHz");
        YouTubeRips.IsLikely(StudioFlac).Should().BeFalse("48 kHz is a real rate for a lossless file");
    }

    [Fact]
    public void A_rip_that_measures_better_than_opus_160_is_rated_opus_160()
    {
        MeasuredQuality.FromMediaInfo(Rip).Should().NotBe(SeedData.Opus160QualityId, "by bitrate it measures as V0");
        YouTubeRips.Rate(Rip).Should().Be(SeedData.Opus160QualityId);
    }

    [Fact]
    public void A_rip_that_measures_worse_keeps_its_own_quality_and_other_files_are_rated_as_measured()
    {
        YouTubeRips.Rate(PoorRip).Should().Be(MeasuredQuality.FromMediaInfo(PoorRip), "MP3-128 is already below OPUS-160");
        YouTubeRips.Rate(CdRip).Should().Be(MeasuredQuality.FromMediaInfo(CdRip));
        YouTubeRips.Rate(StudioFlac).Should().Be(MeasuredQuality.FromMediaInfo(StudioFlac));
    }
}
