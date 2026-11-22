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
    [InlineData("QueenFanTV", "Queen", false)]
    [InlineData("lisa_covers", "Lisa", false)]
    [InlineData("UB40 Fan Club", "UB40", false)]
    [InlineData("Stevie", "Stevie Wonder", false)]
    [InlineData("won", "Stevie Wonder", false)]
    [InlineData("Paramore - Topic", "Paramore", false)]
    [InlineData("BeyoncéVEVO", "Beyoncé", true)]
    [InlineData("BeyonceVEVO", "Beyoncé", true)]
    [InlineData("TheSaddleClubVEVO", "The Saddle Club", true)]
    [InlineData("SaddleClub", "The Saddle Club", true)]
    [InlineData("SimonAndGarfunkelVEVO", "Simon & Garfunkel", true)]
    [InlineData("SimonGarfunkel", "Simon & Garfunkel", true)]
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
        MusicVideoTitleMatcher.ChannelNamesArtist("Alma", ["Al"]).Should().BeFalse("two extra characters are only allowed round a name of four or more letters");
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

    [Theory]
    [InlineData("Hello", "Beyoncé - Halo")]
    [InlineData("Friends to Be", "The Saddle Club - Be My Friend")]
    [InlineData("Friends to Be", "Be my friend")]
    public void A_title_that_is_only_alike_is_not_titled_as_the_song(string songTitle, string videoTitle)
    {
        MusicVideoTitleMatcher.Evaluate(songTitle, ["Beyoncé", "The Saddle Club"], videoTitle).IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.IsTitledAsTheSong(songTitle, videoTitle).Should().BeFalse();
        MusicVideoTitleMatcher.Similarity(songTitle, videoTitle).Should().BeGreaterThanOrEqualTo(DecisionEngine.TitleFloor, "the loose similarity still ranks it");
    }

    [Theory]
    [InlineData("The Power of Love", "Huey Lewis & The News - The Power Of Love (Official Music Video)", "Huey Lewis & The News")]
    [InlineData("Hip to Be Square", "Huey Lewis & The News - Hip To Be Square", "Huey Lewis & The News")]
    [InlineData("CAN'T STOP THE FEELING!", "Justin Timberlake - CAN'T STOP THE FEELING! [Official Video]", "Justin Timberlake")]
    [InlineData("Higher Ground", "Higher Ground (Official Music Video)", "Red Hot Chili Peppers")]
    [InlineData("(I Can't Help) Falling In Love With You", "UB40 - (I Can't Help) Falling In Love With You (Remastered 2002)", "UB40")]
    [InlineData("Joyride (single edit)", "Roxette - Joyride (Official Video)", "Roxette")]
    [InlineData("3 Am", "Matchbox Twenty - 3AM", "Matchbox Twenty")]
    [InlineData("Don't Stop Me Now", "Queen - Don't Stop Me Now (Official Video Remastered 4K)", "Queen")]
    [InlineData("Shout Out to My Ex", "Little Mix - Shout Out to My Ex (Official Video)", "Little Mix")]
    public void The_strict_reading_still_accepts_the_real_titles(string songTitle, string videoTitle, string artist)
    {
        MusicVideoTitleMatcher.Evaluate(songTitle, [artist], videoTitle).IsTitledAsTheSong.Should().BeTrue();
    }

    [Theory]
    [InlineData("Joyride (Club Edit)")]
    [InlineData("Joyride (DJ Edit)")]
    [InlineData("Joyride (Fan Edit)")]
    [InlineData("Joyride (Re-Edit)")]
    [InlineData("Joyride (12\" Mix)")]
    [InlineData("Joyride (Intro)")]
    [InlineData("Joyride (Outro)")]
    public void Only_the_explicit_single_album_radio_and_7_inch_forms_are_soft(string title)
    {
        MusicVideoTitleMatcher.SearchTitle(title).Should().Be(title);
        MusicVideoTitleMatcher.Strip(title).Title.Should().Be(title);
        MusicVideoTitleMatcher.IsTitledAsTheSong("Joyride", title).Should().BeFalse("a different cut is not the song's own title");
    }

    [Theory]
    [InlineData("Joyride (Single Edit)")]
    [InlineData("Joyride (Album Version)")]
    [InlineData("Joyride (Radio Edit)")]
    [InlineData("Joyride (7\" Version)")]
    [InlineData("Joyride (7-inch Mix)")]
    [InlineData("Joyride (Edit)")]
    public void The_explicit_forms_are_soft(string title)
    {
        MusicVideoTitleMatcher.SearchTitle(title).Should().Be("Joyride");
        MusicVideoTitleMatcher.Strip(title).Title.Should().Be("Joyride");
    }

    private const string TimberlakeTitle = "Justin Timberlake - CAN'T STOP THE FEELING! (from DreamWorks Animation's \"TROLLS\") (Official Video)";

    [Theory]
    [InlineData("Song (from DreamWorks Animation's \"TROLLS\")")]
    [InlineData("Song (From \"Frozen\")")]
    [InlineData("Song [From The Motion Picture Frozen]")]
    [InlineData("Song (Original Motion Picture Soundtrack)")]
    [InlineData("Song (From the Netflix Series Arcane)")]
    [InlineData("Song (from the movie Trolls)")]
    [InlineData("Song - From \"Moana\"")]
    [InlineData("Song (OST)")]
    public void A_note_on_where_the_song_comes_from_is_stripped(string videoTitle)
    {
        MusicVideoTitleMatcher.Strip(videoTitle).Title.Should().Be("Song");
        MusicVideoTitleMatcher.IsTitledAsTheSong("Song", videoTitle).Should().BeTrue();
    }

    [Fact]
    public void A_soundtrack_note_the_songs_own_title_carries_stays()
    {
        MusicVideoTitleMatcher.Strip("Laufey - From The Start").Title.Should().Be("Laufey - From The Start");
        MusicVideoTitleMatcher.Strip("Song (From \"Frozen\")", "Song (From \"Frozen\")").Title.Should().Be("Song (From \"Frozen\")");
        MusicVideoTitleMatcher.IsTitledAsTheSong("Song (From \"Frozen\")", "Song (From \"Frozen\") (Official Video)").Should().BeTrue();
        MusicVideoTitleMatcher.IsTitledAsTheSong("Song", "Song (from Live at Wembley)").Should().BeFalse();
    }

    [Fact]
    public void The_real_timberlake_title_is_the_song()
    {
        var evaluation = MusicVideoTitleMatcher.Evaluate("CAN'T STOP THE FEELING!", ["Justin Timberlake"], TimberlakeTitle);

        evaluation.IsTitledAsTheSong.Should().BeTrue();
        evaluation.IsOtherVersion.Should().BeFalse();
        MusicVideoTitleMatcher.IsAudioOnly("CAN'T STOP THE FEELING!", TimberlakeTitle).Should().BeFalse();
    }

    [Theory]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (852 hz)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake 432Hz")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (Pitched)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (pitch shifted)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (Slowed + Reverb)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (Sped Up)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake Nightcore")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (8D Audio)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (Bass Boosted)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake Chipmunk")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake (10 Hours)")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake 1 Hour")]
    [InlineData("Can't Stop the Feeling! - Justin Timberlake Loop")]
    public void An_altered_upload_is_another_version_and_not_titled_as_the_song(string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion("CAN'T STOP THE FEELING!", videoTitle, ["Justin Timberlake"]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("CAN'T STOP THE FEELING!", ["Justin Timberlake"], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void The_saddle_club_chipmunk_upload_is_another_version()
    {
        var evaluation = MusicVideoTitleMatcher.Evaluate("Hello World", ["The Saddle Club"], "The Saddle Club - Hello World - Chipmunk");

        evaluation.IsOtherVersion.Should().BeTrue();
        evaluation.IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void An_altered_word_the_songs_own_title_has_is_not_a_version()
    {
        MusicVideoTitleMatcher.IsOtherVersion("Loop", "Artist - Loop (Official Video)", ["Artist"]).Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Concert", "Artist - Concert (Official Video)", ["Artist"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Paramore - When It Rains - (iTunes Festivel 2013)- [HD]")]
    [InlineData("Paramore - When It Rains (iTunes Festival 2013)")]
    [InlineData("Paramore - When It Rains (Fest)")]
    [InlineData("Paramore - When It Rains (Concert)")]
    [InlineData("Paramore - When It Rains (In Concert)")]
    [InlineData("Paramore - When It Rains (Live at Reading)")]
    [InlineData("Paramore - When It Rains (Live from the O2)")]
    [InlineData("Paramore - When It Rains (Unplugged)")]
    [InlineData("Paramore - When It Rains (BBC Session)")]
    [InlineData("Paramore - When It Rains (Live Sessions)")]
    [InlineData("Paramore - When It Rains (MTV)")]
    public void A_live_festival_or_session_upload_is_another_version_and_not_titled_as_the_song(string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion("When It Rains", videoTitle, ["Paramore"]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("When It Rains", ["Paramore"], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void The_plain_paramore_title_is_still_the_song()
    {
        MusicVideoTitleMatcher.Evaluate("When It Rains", ["Paramore"], "Paramore - When it Rains").IsTitledAsTheSong.Should().BeTrue();
    }

    // M-70 item 4: other versions that still counted as the song. Every title is a real one from production.

    [Theory]
    [InlineData("Some Things Never Change", "Kristen Bell", "Some Things Never Change (From \"Frozen 2\"/Sing-Along)")]
    [InlineData("When Will My Life Begin?", "Mandy Moore", "Mandy Moore - When Will My Life Begin? (From \"Tangled\"/Sing-Along)")]
    [InlineData("When Will My Life Begin?", "Mandy Moore", "Mandy Moore - When Will My Life Begin? (From \"Tangled\") (Singalong)")]
    [InlineData("When Will My Life Begin?", "Mandy Moore", "Mandy Moore - When Will My Life Begin? (Sing Along)")]
    public void A_sing_along_is_audio_only_and_not_titled_as_the_song(string song, string artist, string videoTitle)
    {
        MusicVideoTitleMatcher.IsAudioOnly(song, videoTitle).Should().BeTrue();
        MusicVideoTitleMatcher.IsNotAMusicVideo(song, videoTitle).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate(song, [artist], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void A_soundtrack_bracket_with_slash_parts_is_judged_part_by_part()
    {
        MusicVideoTitleMatcher.Strip("Song (From \"Frozen 2\"/Sing-Along)").Title.Should().Be("Song (Sing-Along)");
        MusicVideoTitleMatcher.Strip("Song (From \"AC/DC\")").Title.Should().Be("Song");
        MusicVideoTitleMatcher.Strip("Song (From \"Frozen\"/HD)").Title.Should().Be("Song");
        MusicVideoTitleMatcher.Strip("Song (From \"Frozen\"/Live)").Title.Should().Be("Song (Live)");
    }

    [Fact]
    public void A_sing_along_word_the_songs_own_title_has_is_not_a_lyric_word()
    {
        MusicVideoTitleMatcher.IsAudioOnly("Sing-Along", "Artist - Sing-Along (Official Video)").Should().BeFalse();
        MusicVideoTitleMatcher.IsNotAMusicVideo("Sing-Along", "Artist - Sing-Along (Official Video)").Should().BeFalse();
    }

    [Fact]
    public void An_american_sign_language_version_is_another_version()
    {
        const string title = "Kristen Bell - Do You Want to Build a Snowman? (American Sign Language Version)";

        MusicVideoTitleMatcher.IsOtherVersion("Do You Want to Build a Snowman?", title, ["Kristen Bell"]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("Do You Want to Build a Snowman?", ["Kristen Bell"], title).IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Do You Want to Build a Snowman?", "Kristen Bell - Do You Want to Build a Snowman? (ASL)", ["Kristen Bell"]).Should().BeTrue();
    }

    [Fact]
    public void A_reprise_is_a_different_track_from_the_full_song()
    {
        // Song "When Will My Life Begin (reprise 1)" is 63 s; the full song's video is 152 s.
        const string song = "When Will My Life Begin (reprise 1)";
        const string full = "Mandy Moore - When Will My Life Begin? (From \"Tangled\")";

        MusicVideoTitleMatcher.Similarity(song, full).Should().Be(0);
        MusicVideoTitleMatcher.Evaluate(song, ["Mandy Moore"], full).IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.Evaluate(song, ["Mandy Moore"], "Mandy Moore - When Will My Life Begin (Reprise 1)").IsTitledAsTheSong.Should().BeTrue();
    }

    [Fact]
    public void A_reprise_video_is_not_the_full_song()
    {
        MusicVideoTitleMatcher.IsOtherVersion("How Far I'll Go", "Auli'i Cravalho - How Far I'll Go (Reprise)", ["Auli'i Cravalho"]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("How Far I'll Go", ["Auli'i Cravalho"], "Auli'i Cravalho - How Far I'll Go (Reprise)").IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Reprise", "Artist - Reprise (Official Video)", ["Artist"]).Should().BeFalse("the song's own title has the word");
    }

    [Fact]
    public void A_part_or_interlude_bracket_of_the_songs_title_is_kept_by_the_variants()
    {
        MusicVideoTitleMatcher.Evaluate("Rhapsody (Part 2)", ["Artist"], "Artist - Rhapsody (Part 2)").IsTitledAsTheSong.Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("Rhapsody (Part 2)", ["Artist"], "Artist - Rhapsody").IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.Evaluate("Rhapsody (Interlude)", ["Artist"], "Artist - Rhapsody").IsTitledAsTheSong.Should().BeFalse();
        MusicVideoTitleMatcher.Evaluate("Rhapsody", ["Artist"], "Artist - Rhapsody (Interlude)").IsTitledAsTheSong.Should().BeFalse();
    }

    [Theory]
    [InlineData("How Far I'll Go", "Auli'i Cravalho", "How far I'll go (Reprise) - Persian")]
    [InlineData("Wanna Have Good Times", "Justin Timberlake", "Trolls Wanna Have Good Times - Pop Trolls (Sub. Español)")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Frozen II - Into the unknown - Brazilian Portuguese - Panic! at the disco version")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go (Français)")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go (Subtitulado)")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go (Legendado)")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go (in Japanese)")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go - Deutsch")]
    [InlineData("Let It Go", "Idina Menzel", "Idina Menzel - Let It Go (Subtitles)")]
    public void A_bracket_or_dash_part_that_names_a_language_is_another_version(string song, string artist, string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion(song, videoTitle, [artist]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate(song, [artist], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    [Fact]
    public void A_language_word_in_the_songs_title_or_the_artists_name_is_not_a_version()
    {
        MusicVideoTitleMatcher.IsOtherVersion("English Rose", "The Jam - English Rose (Official Video)", ["The Jam"]).Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Latin Lover", "Artist - Latin Lover (Official Video)", ["Artist"]).Should().BeFalse();
        MusicVideoTitleMatcher.IsOtherVersion("Song", "Thai - Song (Official Video)", ["Thai"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Into the Unknown - Panic at the Disco (lower key)")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Panic! at the Disco - Into the Unknown (higher key)")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Panic! at the Disco - Into the Unknown (key change)")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Panic! at the Disco - Into the Unknown (transposed)")]
    [InlineData("Into the Unknown", "Idina Menzel", "Idina Menzel - Into the Unknown (male version)")]
    [InlineData("Into the Unknown", "Idina Menzel", "Idina Menzel - Into the Unknown (female version)")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge - So You Think You Can Dance")]
    [InlineData("London Bridge", "Fergie", "London Bridge - Fergie | Akiva Brooks Choreography")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge (Dance Cover)")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge (Dance Tutorial)")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge (Tutorial)")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge (Lesson)")]
    public void A_key_change_cover_or_dance_upload_is_another_version(string song, string artist, string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion(song, videoTitle, [artist]).Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate(song, [artist], videoTitle).IsTitledAsTheSong.Should().BeFalse();
    }

    // M-70 item 5.

    [Fact]
    public void A_title_with_one_harmless_extra_bracket_is_titled_with_an_alternate_bracket_but_not_as_the_song()
    {
        var evaluation = MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], "Fergie - London Bridge (Oh Snap) (Official Music Video)");

        evaluation.IsTitledAsTheSong.Should().BeFalse("the strict reading stays as it was, so it is never confident");
        evaluation.IsTitledWithAlternateBracket.Should().BeTrue();
        evaluation.IsOtherVersion.Should().BeFalse();
    }

    [Theory]
    [InlineData("Fergie - London Bridge (Oh Snap) (Live)")]
    [InlineData("Fergie - London Bridge (Oh Snap) (Remix)")]
    [InlineData("Fergie - London Bridge (Español)")]
    [InlineData("Fergie - London Bridge (Lyrics)")]
    [InlineData("Fergie - London Bridge (Reprise)")]
    [InlineData("Fergie - London Bridge (Dance Cover)")]
    [InlineData("Fergie - London Bridge (Oh Snap) (Extra)")]
    [InlineData("Fergie - Another Bridge (Oh Snap)")]
    public void An_alternate_bracket_with_a_version_word_or_a_second_bracket_is_not_accepted(string videoTitle)
    {
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], videoTitle).IsTitledWithAlternateBracket.Should().BeFalse();
    }

    [Fact]
    public void A_plain_title_is_not_flagged_as_an_alternate_bracket()
    {
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], "Fergie - London Bridge (Official Music Video)").IsTitledWithAlternateBracket.Should().BeFalse();
    }

    [Theory]
    [InlineData("Yusuf / Cat Stevens", "Cat Stevens", true)]
    [InlineData("Yusuf / Cat Stevens", "Yusuf", true)]
    [InlineData("Lisa & Fans", "Lisa", false)]
    [InlineData("Lisa, Fans", "Lisa", false)]
    [InlineData("Some Fan / Other Fan", "Cat Stevens", false)]
    public void A_channel_naming_two_artists_with_a_slash_passes_when_either_part_does(string channel, string artist, bool expected)
    {
        MusicVideoTitleMatcher.ChannelNamesArtist(channel, [artist]).Should().Be(expected);
    }

    // M-70 review fixes.

    [Theory]
    [InlineData("Fergie - London Bridge (Alternate Version)")]
    [InlineData("Fergie - London Bridge (Director's Cut)")]
    [InlineData("Fergie - London Bridge (Taylor's Version)")]
    [InlineData("Fergie - London Bridge (Piano Version)")]
    [InlineData("Fergie - London Bridge (Duet Version)")]
    [InlineData("Fergie - London Bridge (Medley)")]
    [InlineData("Fergie - London Bridge (Take 2)")]
    [InlineData("Fergie - London Bridge (Intro)")]
    [InlineData("Fergie - London Bridge (Club Mix)")]
    public void An_alternate_bracket_that_names_a_cut_of_the_song_is_not_accepted(string videoTitle)
    {
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], videoTitle).IsTitledWithAlternateBracket.Should().BeFalse();
    }

    [Fact]
    public void An_alternate_title_bracket_is_still_accepted_and_a_remaster_bracket_is_still_noise()
    {
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], "Fergie - London Bridge (Oh Snap)").IsTitledWithAlternateBracket.Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], "Fergie - London Bridge (Oh Snap) (Remastered 2020)").IsTitledWithAlternateBracket.Should().BeTrue();
        MusicVideoTitleMatcher.Evaluate("London Bridge", ["Fergie"], "Fergie - London Bridge (Remastered)").IsTitledAsTheSong.Should().BeTrue();
    }

    [Fact]
    public void A_channel_that_is_the_whole_credited_name_passes_before_it_is_split()
    {
        MusicVideoTitleMatcher.ChannelNamesArtist("Yusuf / Cat Stevens", ["Yusuf / Cat Stevens"]).Should().BeTrue();
        MusicVideoTitleMatcher.ChannelNamesArtist("Yusuf / Cat Stevens", ["Cat Stevens"]).Should().BeTrue();
        MusicVideoTitleMatcher.ChannelNamesArtist("Yusuf / Cat Stevens", ["Yusuf"]).Should().BeTrue();
        MusicVideoTitleMatcher.ChannelNamesArtist("Fan / Queen Covers", ["Queen"]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Artist - Song (feat. French Montana)")]
    [InlineData("Artist - Song [ft. Swedish House Mafia]")]
    [InlineData("Artist - Song - feat. French Montana")]
    [InlineData("Artist - Song ft. Swedish House Mafia")]
    public void A_language_word_in_a_featuring_clause_is_not_a_language_version(string videoTitle)
    {
        MusicVideoTitleMatcher.IsOtherVersion("Song", videoTitle, ["Artist"]).Should().BeFalse();
    }

    [Fact]
    public void A_bracket_whose_parts_are_all_notes_is_dropped_without_an_empty_pair()
    {
        MusicVideoTitleMatcher.Strip("Song (From \"Frozen\"/From the Movie Frozen)").Title.Should().Be("Song");
    }
}
