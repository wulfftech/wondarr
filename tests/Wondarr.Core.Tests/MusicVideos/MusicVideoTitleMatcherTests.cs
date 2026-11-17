using FluentAssertions;
using Wondarr.Core.Decisions;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>How a video's title is judged against the song's (ADR-0014).</summary>
public sealed class MusicVideoTitleMatcherTests
{
    [Theory]
    [InlineData("Teardrop (Official Video)")]
    [InlineData("Teardrop (Official Music Video)")]
    [InlineData("Teardrop [Official Video]")]
    [InlineData("Teardrop (Official HD Video)")]
    [InlineData("Teardrop (Video)")]
    [InlineData("TEARDROP (OFFICIAL VIDEO)")]
    [InlineData("Teardrop")]
    public void A_decorated_title_is_the_song_once_the_decoration_is_stripped(string videoTitle)
    {
        MusicVideoTitleMatcher.Strip(videoTitle).Title.Should().BeEquivalentTo("Teardrop");
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", videoTitle).Should().BeTrue();
    }

    [Theory]
    [InlineData("Teardrop (Lyric Video)")]
    [InlineData("Teardrop (Visualizer)")]
    public void A_lyric_video_or_a_visualizer_is_never_the_song(string videoTitle)
    {
        var stripped = MusicVideoTitleMatcher.Strip(videoTitle);

        stripped.Title.Should().Be("Teardrop");
        stripped.IsLyricOrVisualizer.Should().BeTrue();
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", videoTitle).Should().BeFalse();
    }

    [Fact]
    public void A_bracket_that_is_not_a_known_decoration_stays()
    {
        MusicVideoTitleMatcher.Strip("Teardrop (Live at Glastonbury)").Title.Should().Be("Teardrop (Live at Glastonbury)");
    }

    [Fact]
    public void An_artist_before_a_dash_does_not_stop_the_title_matching()
    {
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", "Massive Attack - Teardrop (Official Video)").Should().BeTrue();
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", "Teardrop - Massive Attack").Should().BeTrue();
    }

    [Fact]
    public void A_different_song_is_below_the_decision_engines_floor()
    {
        MusicVideoTitleMatcher.Similarity("Teardrop", "Angel (Official Video)").Should().BeLessThan(DecisionEngine.TitleFloor);
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", "Angel (Official Video)").Should().BeFalse();
    }

    [Fact]
    public void A_live_video_is_not_a_studio_songs_video()
    {
        MusicVideoTitleMatcher.Similarity("Teardrop", "Teardrop (Live)").Should().Be(0);
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", "Teardrop (Live)").Should().BeFalse();
    }

    [Fact]
    public void A_live_song_matches_its_live_video()
    {
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop (Live)", "Teardrop (Live) (Official Video)").Should().BeTrue();
    }

    [Fact]
    public void A_title_with_nothing_left_matches_nothing()
    {
        MusicVideoTitleMatcher.Similarity("Video", "(Official Video)").Should().Be(0);
    }
}
