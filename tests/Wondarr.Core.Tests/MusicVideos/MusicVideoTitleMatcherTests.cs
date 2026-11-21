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

    [Theory]
    [InlineData("Paramore - Fences (Official Audio)")]
    [InlineData("Paramore - Born For This (Official Audio)")]
    [InlineData("Paramore - We Are Broken (Official Audio)")]
    [InlineData("Paramore -  When It Rains (Official Audio)")]
    [InlineData("Matchbox Twenty - 3AM // Lyrics")]
    [InlineData("Teardrop (Lyric Video)")]
    [InlineData("Teardrop [Lyrics]")]
    [InlineData("Teardrop | Visualiser")]
    [InlineData("Teardrop - Audio")]
    [InlineData("Teardrop (Static Video)")]
    [InlineData("Teardrop (Official Visualizer)")]
    public void A_title_that_says_audio_or_lyrics_in_a_bracket_or_after_a_separator_is_audio_only(string realTitle)
    {
        MusicVideoTitleMatcher.IsAudioOnly("Teardrop", realTitle).Should().BeTrue();
    }

    [Theory]
    [InlineData("Teardrop (Official Video)")]
    [InlineData("Massive Attack - Teardrop")]
    [InlineData("Audio Bullys - Teardrop")]
    [InlineData("Queen - Don't Stop Me Now (Official Video Remastered 4K)")]
    public void A_video_title_is_not_audio_only(string realTitle)
    {
        MusicVideoTitleMatcher.IsAudioOnly("Teardrop", realTitle).Should().BeFalse();
    }

    [Fact]
    public void A_song_whose_own_title_has_the_audio_word_is_judged_without_it()
    {
        MusicVideoTitleMatcher.IsAudioOnly("Audio Girl", "Artist - Audio Girl (Official Video)").Should().BeFalse();
        MusicVideoTitleMatcher.IsAudioOnly("Lyric", "Artist - Lyric (Official Video)").Should().BeFalse();
        MusicVideoTitleMatcher.IsAudioOnly("Audio Girl", "Artist - Audio Girl (Lyrics)").Should().BeTrue();
    }

    [Theory]
    [InlineData("HueyLewisTheNewsVEVO", "Huey Lewis & The News", true)]
    [InlineData("Queen Official", "Queen", true)]
    [InlineData("ogafroman", "Afroman", true)]
    [InlineData("Paramore", "Paramore", true)]
    [InlineData("UB40Official", "UB40", true)]
    [InlineData("Millennial Pause", "Matchbox Twenty", false)]
    [InlineData("Some Random Channel", "Massive Attack", false)]
    [InlineData("Peppa's Kisses", "UB40", false)]
    public void A_channel_is_the_artists_when_the_normalised_names_hold_each_other(string channel, string artist, bool expected)
    {
        MusicVideoTitleMatcher.ChannelNamesArtist(channel, [artist]).Should().Be(expected);
    }

    [Fact]
    public void A_very_short_name_is_not_found_inside_another_channel()
    {
        MusicVideoTitleMatcher.ChannelNamesArtist("Aloha Channel", ["Al"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Joyride (single edit)", "Joyride (Official Video)")]
    [InlineData("Joyride (single edit)", "Roxette - Joyride (Single Version)")]
    [InlineData("Joyride (single edit)", "Roxette - Joyride (7\" Version) (1991)")]
    [InlineData("Joyride (single edit)", "Roxette - Joyride (Radio Edit)")]
    [InlineData("Joyride (single edit)", "Joyride")]
    [InlineData("Joyride", "Roxette - Joyride (Single Edit)")]
    [InlineData("Joyride (Radio Edit)", "Roxette - Joyride (Official Video)")]
    public void An_edit_or_single_version_marker_is_soft_for_a_music_video(string songTitle, string videoTitle)
    {
        MusicVideoTitleMatcher.Similarity(songTitle, videoTitle).Should().BeGreaterThanOrEqualTo(DecisionEngine.TitleFloor);
        MusicVideoTitleMatcher.Evaluate(songTitle, ["Roxette"], videoTitle).IsTitledAsTheSong.Should().BeTrue();
    }

    [Fact]
    public void A_live_or_remix_video_is_still_another_version_of_a_song_with_an_edit_marker()
    {
        MusicVideoTitleMatcher.Evaluate("Joyride (single edit)", ["Roxette"], "Roxette - Joyride (Live)").IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.Evaluate("Joyride (single edit)", ["Roxette"], "Roxette - Joyride (Remix)").IsTitledAsTheSong.Should().BeFalse();
    }

    [Theory]
    [InlineData("Joyride (single edit)", "Joyride")]
    [InlineData("Joyride (Radio Edit)", "Joyride")]
    [InlineData("Joyride - Single Edit", "Joyride")]
    [InlineData("Joyride (7\" Version)", "Joyride")]
    [InlineData("Joyride (Live)", "Joyride (Live)")]
    [InlineData("Joyride (Remix)", "Joyride (Remix)")]
    [InlineData("Joyride", "Joyride")]
    public void The_search_title_drops_the_edit_markers_only(string songTitle, string expected)
    {
        MusicVideoTitleMatcher.SearchTitle(songTitle).Should().Be(expected);
    }

    [Theory]
    [InlineData("UB40 - Kingston Town - Cover By Peppa's Kisses")]
    [InlineData("Kingston Town (Covered by Someone)")]
    [InlineData("Kingston Town (cover)")]
    [InlineData("Kingston Town cover by Someone")]
    public void A_cover_is_another_version(string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion("Kingston Town", videoTitle, ["UB40"]).Should().BeTrue();
    }

    [Fact]
    public void A_song_whose_own_title_has_cover_is_not_held_against_the_video()
    {
        MusicVideoTitleMatcher.IsOtherVersion("Cover Me", "Bruce Springsteen - Cover Me", ["Bruce Springsteen"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("ROXETTE *  Joyride    1991          HQ")]
    [InlineData("Roxette * Joyride")]
    [InlineData("Roxette | Joyride")]
    [InlineData("Roxette • Joyride")]
    [InlineData("Roxette · Joyride")]
    [InlineData("Roxette // Joyride")]
    [InlineData("Roxette ~ Joyride")]
    [InlineData("Roxette: Joyride")]
    [InlineData("Roxette - Joyride")]
    [InlineData("Roxette – Joyride")]
    [InlineData("Roxette — Joyride")]
    public void Every_separator_between_artist_and_title_names_the_artist(string videoTitle)
    {
        MusicVideoTitleMatcher.TitleAfterArtist(videoTitle, ["Roxette"]).Should().Be("Joyride");
        MusicVideoTitleMatcher.Evaluate("Joyride", ["Roxette"], videoTitle).ArtistInTitle.Should().BeTrue();
    }

    [Theory]
    [InlineData("Mr. Mister * Broken Wings", "Mr. Mister")]
    [InlineData("AC * DC - Thunderstruck", "AC * DC")]
    [InlineData("Therapy? | Screamager", "Therapy?")]
    [InlineData("Sunn O))) // Alice", "Sunn O)))")]
    [InlineData("Godspeed | You | Black Emperor : Storm", "Godspeed | You | Black Emperor")]
    public void An_artist_whose_own_name_holds_a_separator_still_parses(string videoTitle, string artist)
    {
        MusicVideoTitleMatcher.TitleAfterArtist(videoTitle, [artist]).Should().NotBeNull();
    }

    [Fact]
    public void A_title_that_does_not_name_the_artist_is_not_read_as_naming_one()
    {
        MusicVideoTitleMatcher.TitleAfterArtist("Someone Else * Joyride", ["Roxette"]).Should().BeNull();
    }

    [Fact]
    public void An_official_video_with_remaster_and_resolution_after_the_decoration_is_the_song()
    {
        MusicVideoTitleMatcher.Evaluate("Don't Stop Me Now", ["Queen"], "Queen - Don't Stop Me Now (Official Video Remastered 4K)").IsTitledAsTheSong.Should().BeTrue();
    }
}
