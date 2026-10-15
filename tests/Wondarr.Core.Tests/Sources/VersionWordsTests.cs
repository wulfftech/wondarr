using FluentAssertions;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

/// <summary>Version words read out of release-name fields and file-name words (M-42a).</summary>
public sealed class VersionWordsTests
{
    [Theory]
    [InlineData("Live at Wembley", VersionFlags.Live)]
    [InlineData("Live.at.Wembley", VersionFlags.Live)]
    [InlineData("live", VersionFlags.Live)]
    [InlineData("Unplugged", VersionFlags.Live | VersionFlags.Acoustic)]
    [InlineData("In Concert", VersionFlags.Live)]
    [InlineData("Bootleg", VersionFlags.Live)]
    [InlineData("Bootlegs", VersionFlags.Live)]
    [InlineData("Acoustic Sessions", VersionFlags.Acoustic)]
    [InlineData("remix", VersionFlags.Remix)]
    [InlineData("Remixes", VersionFlags.Remix)]
    [InlineData("mix", VersionFlags.Remix)]
    [InlineData("Instrumental", VersionFlags.Instrumental)]
    [InlineData("Instrumentals", VersionFlags.Instrumental)]
    [InlineData("Demo", VersionFlags.Demo)]
    [InlineData("Demos", VersionFlags.Demo)]
    [InlineData("Karaoke", VersionFlags.Karaoke)]
    [InlineData("edit", VersionFlags.Edit)]
    public void Reads_the_hard_flags_of_a_version_word(string text, VersionFlags expected)
    {
        VersionWords.FlagsOf(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("Discovery")]
    [InlineData("Come Away with Me")]
    [InlineData("FLAC")]
    [InlineData("2002")]
    [InlineData("pH")]
    [InlineData("Remastered")]
    [InlineData("Explicit")]
    public void Finds_no_hard_flag_in_other_words(string text)
    {
        VersionWords.FlagsOf(text).Should().Be(VersionFlags.None);
        VersionWords.IsHardKeyword(text).Should().BeFalse();
    }

    [Fact]
    public void A_hard_keyword_is_told_from_a_group_tag()
    {
        VersionWords.IsHardKeyword("LIVE").Should().BeTrue();
        VersionWords.IsHardKeyword("GRP").Should().BeFalse();
    }
}
