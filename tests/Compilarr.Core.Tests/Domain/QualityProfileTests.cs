using Compilarr.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Compilarr.Core.Tests.Domain;

/// <summary>
/// The cutoff and upgrade rules the wanted list and the decision engine are built on, checked against
/// the seeded profiles so a change to the seed data is caught here.
/// </summary>
public class QualityProfileTests
{
    // Quality ids, from SeedData: 1 Unknown, 17 MP3-192, 23 MP3-256, 25 AAC-256, 26 Vorbis Q7,
    // 28 OPUS-160, 29 MP3-320, 36 FLAC, 40 FLAC 24-bit.
    private const long Unknown = 1;
    private const long Mp3192 = 17;
    private const long Mp3256 = 23;
    private const long Aac256 = 25;
    private const long VorbisQ7 = 26;
    private const long Opus160 = 28;
    private const long Mp3320 = 29;
    private const long Flac = 36;
    private const long Flac24Bit = 40;

    [Theory]
    [InlineData(Aac256, true)]
    [InlineData(Mp3320, true)]
    [InlineData(Flac, true)]
    [InlineData(Mp3256, false)]
    [InlineData(Opus160, false)]
    public void Standard_320_meets_its_cutoff_from_the_high_lossy_group_up(long qualityId, bool meets)
    {
        Standard320().MeetsCutoff(qualityId).Should().Be(meets);
    }

    [Theory]
    [InlineData(Opus160, true)]
    [InlineData(Mp3192, true)]
    [InlineData(VorbisQ7, false)]
    [InlineData(Unknown, false)]
    [InlineData(Flac, true)]
    public void Standard_320_allows_the_grabbable_tiers_only(long qualityId, bool allowed)
    {
        Standard320().IsAllowed(qualityId).Should().Be(allowed);
    }

    [Fact]
    public void Standard_320_treats_a_quality_it_does_not_list_as_unallowed_and_below_cutoff()
    {
        var profile = Standard320();

        profile.GroupIndexOf(999).Should().BeNull();
        profile.IsAllowed(999).Should().BeFalse();
        profile.MeetsCutoff(999).Should().BeFalse();
        profile.IsUpgrade(999, 999).Should().BeFalse();
    }

    [Theory]
    [InlineData(Opus160, Mp3320, true)] // Mid lossy → High lossy
    [InlineData(Mp3320, Mp3320, false)] // the same group is not an upgrade
    [InlineData(Mp3320, Aac256, false)] // AAC-256 is in the cutoff group too
    [InlineData(Mp3320, Flac, true)] // High lossy → Lossless
    [InlineData(Flac, Mp3320, false)] // and never the other way round
    [InlineData(Unknown, Mp3192, true)] // an unlisted current quality is worse than everything
    [InlineData(Mp3192, Opus160, true)]
    [InlineData(Opus160, VorbisQ7, false)] // not an allowed quality
    public void Standard_320_upgrades_only_to_an_allowed_quality_in_a_better_group(
        long currentQualityId,
        long candidateQualityId,
        bool isUpgrade)
    {
        Standard320().IsUpgrade(currentQualityId, candidateQualityId).Should().Be(isUpgrade);
    }

    [Theory]
    [InlineData(Opus160, Mp3320)]
    [InlineData(Unknown, Flac)]
    [InlineData(Mp3320, Flac)]
    public void With_upgrades_off_nothing_is_an_upgrade(long currentQualityId, long candidateQualityId)
    {
        var profile = Standard320();
        profile.UpgradeAllowed = false;

        profile.IsUpgrade(currentQualityId, candidateQualityId).Should().BeFalse();
    }

    [Fact]
    public void With_upgrades_off_the_cutoff_still_holds()
    {
        var profile = Standard320();
        profile.UpgradeAllowed = false;

        profile.MeetsCutoff(Mp3320).Should().BeTrue();
        profile.MeetsCutoff(Mp3256).Should().BeFalse();
    }

    [Fact]
    public void Lossless_allows_mp3_320_but_the_cutoff_sits_above_it()
    {
        var profile = Lossless();

        profile.IsAllowed(Mp3320).Should().BeTrue();
        profile.MeetsCutoff(Mp3320).Should().BeFalse();
        profile.IsUpgrade(Mp3320, Flac).Should().BeTrue();
    }

    [Fact]
    public void Lossless_meets_its_cutoff_at_flac_and_above()
    {
        var profile = Lossless();

        profile.MeetsCutoff(Flac).Should().BeTrue();
        profile.MeetsCutoff(Flac24Bit).Should().BeTrue();
        profile.MeetsCutoff(Mp3256).Should().BeFalse();
    }

    [Fact]
    public void A_seeded_profile_lists_its_items_worst_to_best()
    {
        var profile = Standard320();

        profile.GroupIndexOf(Unknown).Should().Be(0);
        profile.GroupIndexOf(Flac).Should().Be(9);
        profile.GroupIndexOf(Flac24Bit).Should().Be(11);
        profile.GroupIndexOf(Mp3320).Should().Be(7);
    }

    /// <summary>
    /// A copy, so a test that flips <see cref="QualityProfile.UpgradeAllowed"/> cannot leak into
    /// another test through the shared <see cref="SeedData"/> instance.
    /// </summary>
    private static QualityProfile Standard320() => Copy(SeedData.StandardProfileId);

    private static QualityProfile Lossless() => Copy(SeedData.LosslessProfileId);

    private static QualityProfile Copy(long profileId)
    {
        var source = SeedData.QualityProfiles.Single(profile => profile.Id == profileId);

        return new QualityProfile
        {
            Id = source.Id,
            Name = source.Name,
            Items = source.Items,
            CutoffQualityId = source.CutoffQualityId,
            UpgradeAllowed = source.UpgradeAllowed,
            MinScore = source.MinScore,
            DurationToleranceMs = source.DurationToleranceMs,
        };
    }
}