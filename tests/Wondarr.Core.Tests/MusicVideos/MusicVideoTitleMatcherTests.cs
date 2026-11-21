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

    [Theory]
    [InlineData("Teardrop (Official Lyric Video)")]
    [InlineData("Teardrop - Lyric Video")]
    [InlineData("Teardrop | Lyrics")]
    [InlineData("Teardrop (Visualiser)")]
    [InlineData("Teardrop - Official Audio")]
    [InlineData("Teardrop (Official Audio)")]
    [InlineData("Teardrop [Lyrics]")]
    [InlineData("Teardrop - Live at Glastonbury")]
    [InlineData("Teardrop - Remix")]
    public void A_title_that_says_lyrics_visualizer_audio_or_another_version_is_not_confident(string videoTitle)
    {
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", videoTitle).Should().BeFalse();
    }

    [Fact]
    public void An_official_video_is_still_confident()
    {
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", "Teardrop (Official Video)").Should().BeTrue();
    }

    [Fact]
    public void A_song_whose_own_title_has_the_word_is_not_held_against_the_video()
    {
        MusicVideoTitleMatcher.IsTitledAsTheSong("Lyrics", "Lyrics (Official Video)").Should().BeTrue();
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

    [Theory]
    [InlineData("The Saddle Club - Hello World (Music Video) (1080p Remaster by aTunes)", "Hello World")]
    [InlineData("hello world - the saddle club", "hello world")]
    [InlineData("The Saddle Club \"Hello World\"", "Hello World")]
    [InlineData("The Saddle Club & Friends – Hello World", "Hello World")]
    [InlineData("The Saddle Club — Hello World", "Hello World")]
    public void The_artist_is_found_in_the_title_in_each_of_the_known_forms(string videoTitle, string rest)
    {
        MusicVideoTitleMatcher.TitleAfterArtist(videoTitle, ["The Saddle Club"])!
            .Should().StartWith(rest);
        MusicVideoTitleMatcher.Evaluate("Hello World", ["The Saddle Club"], videoTitle).ArtistInTitle.Should().BeTrue();
    }

    [Fact]
    public void A_title_that_names_another_artist_or_none_has_no_artist_in_it()
    {
        MusicVideoTitleMatcher.TitleAfterArtist("Hello World", ["The Saddle Club"]).Should().BeNull();
        MusicVideoTitleMatcher.TitleAfterArtist("Someone Else - Hello World", ["The Saddle Club"]).Should().BeNull();
    }

    [Fact]
    public void A_fan_upload_with_the_artist_in_its_title_is_titled_as_the_song_once_the_artist_is_taken_off()
    {
        var evaluation = MusicVideoTitleMatcher.Evaluate(
            "Hello World",
            ["The Saddle Club"],
            "The Saddle Club - Hello World (Music Video) (1080p Remaster by aTunes)");

        evaluation.ArtistInTitle.Should().BeTrue();
        evaluation.IsTitledAsTheSong.Should().BeTrue();
        evaluation.IsOtherVersion.Should().BeFalse();
    }

    [Theory]
    [InlineData("Queen - Under Pressure 1981 Live Video Full HD")]
    [InlineData("Queen - Under Pressure (Vocals Only)")]
    [InlineData("Queen & David Bowie - Under Pressure (Juno Ray Club Mix) [Audio]")]
    [InlineData("Under Pressure (Rah Mix) (feat. David Bowie)")]
    [InlineData("Queen - Under Pressure (Cover)")]
    [InlineData("Queen - Under Pressure (Instrumental)")]
    [InlineData("Queen - Under Pressure REACTION")]
    public void A_live_vocals_only_mix_cover_instrumental_or_reaction_title_is_another_version_and_never_the_song(string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion("Under Pressure", videoTitle).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("Under Pressure", ["Queen"], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void A_version_word_in_the_songs_own_title_is_not_another_version()
    {
        MusicVideoTitleMatcher.IsOtherVersion("Live and Let Die", "Paul McCartney - Live and Let Die").Should().BeFalse();
    }

    [Theory]
    [InlineData("Teardrop (HD Live)")]
    [InlineData("Teardrop (4K Remix)")]
    [InlineData("Teardrop (1080p Live at Glastonbury)")]
    [InlineData("Teardrop (Live at Wembley 1986 - Remastered)")]
    [InlineData("Teardrop (Acoustic, Remastered 2011)")]
    public void A_bracket_that_holds_a_version_word_is_never_stripped_as_noise(string videoTitle)
    {
        MusicVideoTitleMatcher.Strip(videoTitle).Title.Should().Be(videoTitle);
        MusicVideoTitleMatcher.IsTitledAsTheSong("Teardrop", videoTitle).Should().BeFalse();
    }

    [Theory]
    [InlineData("Teardrop (HD)")]
    [InlineData("Teardrop (1080p, Remastered 2011)")]
    [InlineData("Teardrop (Music Video) (1080p Remaster by aTunes)")]
    [InlineData("Teardrop [4K]")]
    public void A_bracket_that_is_only_noise_is_stripped(string videoTitle)
    {
        MusicVideoTitleMatcher.Strip(videoTitle).Title.Should().Be("Teardrop");
    }

    [Fact]
    public void A_version_word_in_the_artists_name_is_not_a_version()
    {
        MusicVideoTitleMatcher.IsOtherVersion("Lightning Crashes", "Live - Lightning Crashes", ["Live"]).Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Shout Out to My Ex", "Little Mix - Shout Out to My Ex (Official Video)", ["Little Mix"]).Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Lightning Crashes", "Live - Lightning Crashes (Live)", ["Live"]).Should().BeTrue();
    }

    [Fact]
    public void A_featured_artist_after_the_dash_is_not_the_artist_of_the_video()
    {
        MusicVideoTitleMatcher.TitleAfterArtist("Band B - Song X (feat. Band A)", ["Band A"]).Should().BeNull();
        MusicVideoTitleMatcher.TitleAfterArtist("Song X - Band A (feat. Someone)", ["Band A"]).Should().Be("Song X");
    }
}
