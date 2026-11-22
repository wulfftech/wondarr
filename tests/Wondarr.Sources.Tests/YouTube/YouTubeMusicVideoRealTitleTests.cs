using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.MusicVideos;
using Wondarr.Sources.Tests.Metadata;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// M-68: a search result is checked against its real YouTube title and channel (oEmbed) before it may be confident,
/// and the ranking no longer lets another song of the artist outrank the right one. The examples are the second
/// production test's, through a fake oEmbed: no network.
/// </summary>
public sealed class YouTubeMusicVideoRealTitleTests
{
    private static readonly IReadOnlySet<string> NoBlocklist = new HashSet<string>();

    private static InnertubeResult Official(string videoId, string catalogueTitle, string artist, int durationMs = 240_000) =>
        new(videoId, catalogueTitle, [artist], null, durationMs, InnertubeVideoTypes.OfficialVideo, false);

    private static InnertubeResult Upload(string videoId, string title, string channel, int durationMs = 240_000) =>
        new(videoId, title, [channel], null, durationMs, InnertubeVideoTypes.UserUpload, false);

    private static StubInnertubeClient Searching(params InnertubeResult[] results) =>
        new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            new InnertubeSearchResult("search", InnertubeSearchFilter.Videos, null, results));

    private static StubOEmbedClient Real(params (string VideoId, string Title, string Channel)[] answers) =>
        new(videoId => answers.Where(answer => answer.VideoId == videoId)
            .Select(answer => new YouTubeOEmbed(answer.Title, answer.Channel, null))
            .FirstOrDefault());

    private static Task<MusicVideoFindResult> FindAsync(
        MusicVideoQuery query,
        StubInnertubeClient innertube,
        StubOEmbedClient oEmbed) =>
        new YouTubeMusicVideoFinder(Substitute.For<IMusicBrainzClient>(), innertube, oEmbed, NullLogger<YouTubeMusicVideoFinder>.Instance)
            .FindAsync(query, CancellationToken.None);

    private static MusicVideoQuery Song(string title, string artist, int durationMs = 240_000) =>
        new(title, [artist], null, durationMs, NoBlocklist);

    [Theory]
    [InlineData("Fences", "ybluEqQTpKk", "Paramore - Fences (Official Audio)")]
    [InlineData("Born for This", "OFxpEFb7TtE", "Paramore - Born For This (Official Audio)")]
    [InlineData("We Are Broken", "tcckFDk9RQo", "Paramore - We Are Broken (Official Audio)")]
    [InlineData("When It Rains", "u3IAybZOt-E", "Paramore -  When It Rains (Official Audio)")]
    public async Task An_official_audio_upload_that_youtube_music_calls_an_official_video_is_audio_only_and_never_confident(string song, string videoId, string realTitle)
    {
        // YouTube Music shows the catalogue entry: the song's own title and artist.
        var innertube = Searching(Official(videoId, song, "Paramore"));

        var candidates = await FindAsync(Song(song, "Paramore"), innertube, Real((videoId, realTitle, "Paramore")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse("it is the audio, not a video");
        candidate.Kind.Should().Be(MusicVideoCandidateKind.AudioOnly);
        candidate.Title.Should().Be(realTitle, "the list shows what the upload is called");
        candidate.TitledAsSong.Should().BeFalse();
    }

    [Fact]
    public async Task A_lyrics_upload_by_a_fan_channel_is_audio_only_and_not_the_artists_channel()
    {
        var innertube = Searching(Official("ChZYHH6PyPg", "3 Am", "Matchbox Twenty"));

        var candidates = await FindAsync(
            Song("3 Am", "Matchbox Twenty"),
            innertube,
            Real(("ChZYHH6PyPg", "Matchbox Twenty - 3AM // Lyrics", "Millennial Pause")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.Kind.Should().Be(MusicVideoCandidateKind.AudioOnly);
    }

    [Theory]
    [InlineData("The Power of Love", "Huey Lewis & The News", "wBl2QGAIx1s", "Huey Lewis & The News - The Power Of Love (Official Music Video)", "HueyLewisTheNewsVEVO")]
    [InlineData("Hip to Be Square", "Huey Lewis & The News", "LB5YkmjalDg", "Huey Lewis & The News - Hip To Be Square", "HueyLewisTheNewsVEVO")]
    [InlineData("Don't Stop Me Now", "Queen", "HgzGwKwLmgM", "Queen - Don't Stop Me Now (Official Video Remastered 4K)", "Queen Official")]
    [InlineData("Fat Bottomed Girls", "Queen", "VMnjF1O4eH0", "Queen - Fat Bottomed Girls (Official Video)", "Queen Official")]
    [InlineData("Lemon Pound Cake", "Afroman", "9xxK5yyecRo", "Afroman - Lemon Pound Cake (OFFICIAL MUSIC VIDEO)", "ogafroman")]
    [InlineData("CAN'T STOP THE FEELING!", "Justin Timberlake", "ru0K8uYEZWw", "Justin Timberlake - CAN'T STOP THE FEELING! [Official Video]", "JustinTimberlakeVEVO")]
    [InlineData("Higher Ground", "Red Hot Chili Peppers", "HIGHERGRND1", "Red Hot Chili Peppers - Higher Ground (Official Music Video)", "RedHotChiliPeppersVEVO")]
    [InlineData("(I Can't Help) Falling In Love With You", "UB40", "FALLINGLOVE", "UB40 - (I Can't Help) Falling In Love With You (Remastered 2002)", "UB40Official")]
    public async Task The_real_official_videos_stay_confident(string song, string artist, string videoId, string realTitle, string channel)
    {
        var innertube = Searching(Official(videoId, song, artist));

        var candidates = await FindAsync(Song(song, artist), innertube, Real((videoId, realTitle, channel)));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeTrue();
        candidate.Kind.Should().Be(MusicVideoCandidateKind.OfficialVideo);
        candidate.TitledAsSong.Should().BeTrue();
    }

    [Fact]
    public async Task A_confident_looking_video_on_the_wrong_channel_stays_an_official_video_for_ranking_but_is_not_confident()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Teardrop", "Massive Attack"));

        var candidates = await FindAsync(
            Song("Teardrop", "Massive Attack"),
            innertube,
            Real(("AAAAAAAAAAA", "Massive Attack - Teardrop (Official Video)", "Some Random Channel")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.Kind.Should().Be(MusicVideoCandidateKind.OfficialVideo);
    }

    [Fact]
    public async Task A_lookup_that_returns_nothing_leaves_the_candidate_listed_but_not_confident()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Teardrop", "Massive Attack"));

        var candidates = await FindAsync(Song("Teardrop", "Massive Attack"), innertube, Real());

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse("a lookup that fails closes the door");
        candidate.Kind.Should().Be(MusicVideoCandidateKind.OfficialVideo);
    }

    [Fact]
    public async Task A_lookup_that_throws_leaves_the_candidate_listed_but_not_confident()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Teardrop", "Massive Attack"));
        var oEmbed = Real();
        oEmbed.Failure = new HttpRequestException("offline");

        var candidates = await FindAsync(Song("Teardrop", "Massive Attack"), innertube, oEmbed);

        candidates.Should().ContainSingle().Which.Confident.Should().BeFalse();
    }

    [Fact]
    public async Task At_most_six_lookups_are_made_and_two_run_at_once()
    {
        var results = Enumerable.Range(0, 10)
            .Select(index => Official($"video{index:D6}", "Teardrop", "Massive Attack"))
            .ToArray();
        var oEmbed = new StubOEmbedClient(videoId => new YouTubeOEmbed("Massive Attack - Teardrop (Official Video)", "Massive Attack", null))
        {
            Delay = TimeSpan.FromMilliseconds(40),
        };

        var candidates = await FindAsync(Song("Teardrop", "Massive Attack"), Searching(results), oEmbed);

        oEmbed.Lookups.Should().HaveCount(6).And.OnlyHaveUniqueItems();
        oEmbed.MaxConcurrent.Should().Be(2);
        candidates.Count(candidate => candidate.Confident).Should().Be(6, "a result that was not checked is not confident");
    }

    [Fact]
    public async Task A_user_upload_is_never_looked_up()
    {
        var innertube = Searching(Upload("AAAAAAAAAAA", "Teardrop", "Some Fan"));
        var oEmbed = Real();

        await FindAsync(Song("Teardrop", "Massive Attack"), innertube, oEmbed);

        oEmbed.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task Music_brainz_links_are_not_checked_against_oembed()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var oEmbed = Real();
        var innertube = Searching();

        var candidates = await new YouTubeMusicVideoFinder(musicBrainz, innertube, oEmbed, NullLogger<YouTubeMusicVideoFinder>.Instance)
            .FindAsync(new MusicVideoQuery("Teardrop", ["Massive Attack"], MusicBrainzRelationsTests.TeardropAudioId, 330_026, NoBlocklist), CancellationToken.None);

        candidates.Should().ContainSingle().Which.Confident.Should().BeTrue();
        candidates[0].TitledAsSong.Should().BeTrue();
        oEmbed.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task Friends_to_Be_ranks_the_right_song_above_another_song_by_the_same_artist()
    {
        // "Be my friend" shares words with "Friends to Be" and clears the title floor on its own; the song's own
        // title still outranks it.
        var innertube = Searching(
            Upload("AAAAAAAAAAA", "The Saddle club - Be my friend", "Some Fan"),
            Upload("BBBBBBBBBBB", "The Saddle Club - Friends to Be", "Unnamed Uploader"));

        var candidates = await FindAsync(Song("Friends to Be", "The Saddle Club"), innertube, Real());

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates[0].TitledAsSong.Should().BeTrue();
    }

    [Fact]
    public async Task Kingston_Town_ranks_the_right_title_above_an_official_video_of_another_song()
    {
        var innertube = Searching(
            Official("AAAAAAAAAAA", "Many Rivers to Cross", "UB40"),
            Upload("BBBBBBBBBBB", "Kingston Town", "A Reggae Fan"));

        var candidates = await FindAsync(Song("Kingston Town", "UB40"), innertube, Real());

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates.Single(candidate => candidate.VideoId == "AAAAAAAAAAA").Confident.Should().BeFalse();
    }

    [Fact]
    public async Task Ball_of_Kerrymuir_ranks_the_right_title_above_another_song_by_the_same_artist()
    {
        var innertube = Searching(
            Upload("AAAAAAAAAAA", "Jimmy Buffett - Roller Derby Queen", "Some Fan"),
            Upload("BBBBBBBBBBB", "Ball of Kerrymuir", "A Folk Channel"));

        var candidates = await FindAsync(Song("Ball of Kerrymuir", "Jimmy Buffett"), innertube, Real());

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates[1].TitledAsSong.Should().BeFalse();
    }

    [Fact]
    public async Task All_Your_Reasons_ranks_the_right_title_above_another_song_by_the_same_artist()
    {
        var innertube = Searching(
            Official("AAAAAAAAAAA", "Real World", "Matchbox Twenty"),
            Upload("BBBBBBBBBBB", "All Your Reasons", "A Fan"));

        var candidates = await FindAsync(Song("All Your Reasons", "Matchbox Twenty"), innertube, Real());

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates.Single(candidate => candidate.VideoId == "AAAAAAAAAAA").TitledAsSong.Should().BeFalse();
    }

    [Fact]
    public async Task Joyride_single_edit_finds_the_official_video_confident_and_first_and_searches_without_the_marker()
    {
        var innertube = Searching(
            Upload("AAAAAAAAAAA", "Roxette - Joyride (Single Version)", "Some Fan"),
            Upload("BBBBBBBBBBB", "Roxette - Joyride (7\" Version) (1991)", "Another Fan"),
            Official("CCCCCCCCCCC", "Joyride", "Roxette"));
        var query = Song("Joyride (single edit)", "Roxette");

        var candidates = await FindAsync(query, innertube, Real(("CCCCCCCCCCC", "Roxette - Joyride (Official Video)", "Roxette")));

        candidates[0].VideoId.Should().Be("CCCCCCCCCCC");
        candidates[0].Confident.Should().BeTrue();
        candidates.Where(candidate => candidate.Kind == MusicVideoCandidateKind.UserUpload).Should().OnlyContain(candidate => !candidate.Confident);
        candidates.Should().OnlyContain(candidate => candidate.TitledAsSong, "an edit or single version is not another version for a video");
        innertube.Requests.Should().ContainSingle().Which.Query.Should().Be("Roxette Joyride");
    }

    [Fact]
    public async Task A_cover_that_youtube_music_calls_an_official_video_is_penalised_and_not_confident()
    {
        var innertube = Searching(
            Official("AAAAAAAAAAA", "Kingston Town - Cover By Peppa's Kisses", "Peppa's Kisses"),
            Official("BBBBBBBBBBB", "Kingston Town", "UB40"));

        var candidates = await FindAsync(
            Song("Kingston Town", "UB40"),
            innertube,
            Real(
                ("AAAAAAAAAAA", "UB40 - Kingston Town - Cover By Peppa's Kisses", "Peppa's Kisses"),
                ("BBBBBBBBBBB", "UB40 - Kingston Town (Official Video)", "UB40Official")));

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates[0].Confident.Should().BeTrue();
        var cover = candidates.Single(candidate => candidate.VideoId == "AAAAAAAAAAA");
        cover.Confident.Should().BeFalse();
        cover.TitledAsSong.Should().BeFalse("a cover is another version");
    }

    [Fact]
    public async Task The_other_version_penalty_applies_to_an_official_video_not_credited_to_the_artist()
    {
        var innertube = Searching(
            Official("AAAAAAAAAAA", "Kingston Town (Live)", "Some Other Act"),
            Official("BBBBBBBBBBB", "Kingston Town (Live)", "UB40"));

        var candidates = await FindAsync(Song("Kingston Town", "UB40"), innertube, Real());

        var other = candidates.Single(candidate => candidate.VideoId == "AAAAAAAAAAA");
        var credited = candidates.Single(candidate => candidate.VideoId == "BBBBBBBBBBB");
        credited.Score.Should().BeGreaterThan(other.Score);
        (credited.Score - other.Score).Should().BeGreaterThan(150, "the credited one has the artist weight and the other the version penalty");
    }

    [Fact]
    public async Task An_official_video_of_another_song_by_the_same_artist_is_not_titled_or_confident_even_when_the_words_are_close()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Halo", "Beyoncé"));

        var candidates = await FindAsync(Song("Hello", "Beyoncé"), innertube, Real(("AAAAAAAAAAA", "Beyoncé - Halo", "BeyoncéVEVO")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.TitledAsSong.Should().BeFalse("\"Halo\" is 0.6 like \"Hello\" but is another song");
    }

    [Fact]
    public async Task An_official_Be_My_Friend_on_the_artists_channel_is_not_Friends_to_Be()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Be My Friend", "The Saddle Club"));

        var candidates = await FindAsync(
            Song("Friends to Be", "The Saddle Club"),
            innertube,
            Real(("AAAAAAAAAAA", "The Saddle Club - Be My Friend", "The Saddle Club")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.TitledAsSong.Should().BeFalse();
    }

    [Fact]
    public async Task The_production_case_ranks_the_correct_unnamed_upload_first_and_it_is_titled_as_the_song()
    {
        var innertube = Searching(
            Upload("AAAAAAAAAAA", "The Saddle club - Be my friend", "saddleclub14"),
            Upload("BBBBBBBBBBB", "Friends to be", "An Unnamed Uploader"));

        var candidates = await FindAsync(Song("Friends to Be", "The Saddle Club"), innertube, Real());

        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates[0].TitledAsSong.Should().BeTrue();
        candidates[1].TitledAsSong.Should().BeFalse("it is another song of the artist");
        candidates.Should().OnlyContain(candidate => !candidate.Confident);
    }

    [Theory]
    [InlineData("Queen", "QueenFanTV")]
    [InlineData("Lisa", "lisa_covers")]
    [InlineData("UB40", "UB40 Fan Club")]
    [InlineData("Stevie Wonder", "Stevie")]
    [InlineData("Paramore", "Paramore - Topic")]
    public async Task A_fan_or_topic_channel_is_never_the_artists_own(string artist, string channel)
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Teardrop", artist));

        var candidates = await FindAsync(Song("Teardrop", artist), innertube, Real(("AAAAAAAAAAA", $"{artist} - Teardrop (Official Video)", channel)));

        candidates.Should().ContainSingle().Which.Confident.Should().BeFalse();
    }

    // M-70 items 4 and 5: the real titles from the third production review, through fake search and oEmbed answers.

    [Fact]
    public async Task London_Bridge_ranks_the_official_video_with_an_alternate_bracket_first_and_not_confident()
    {
        var innertube = Searching(
            Upload("AAAAAAAAAAA", "Fergie - London Bridge - So You Think You Can Dance", "SYTYCD Fans"),
            Upload("BBBBBBBBBBB", "London Bridge - Fergie | Akiva Brooks Choreography", "Akiva Brooks"),
            Upload("CCCCCCCCCCC", "Fergie - London Bridge (Live)", "Some Fan"),
            Official("WD33ii01kXI", "London Bridge", "Fergie"));

        var candidates = await FindAsync(
            Song("London Bridge", "Fergie"),
            innertube,
            Real(("WD33ii01kXI", "Fergie - London Bridge (Oh Snap) (Official Music Video)", "FergieVEVO")));

        var best = candidates[0];
        best.VideoId.Should().Be("WD33ii01kXI");
        best.Confident.Should().BeFalse("an extra bracket is never confident");
        best.TitledAsSong.Should().BeTrue("it counts as titled, so it is the Best match");
        best.Title.Should().Be("Fergie - London Bridge (Oh Snap) (Official Music Video)");
        candidates.Where(candidate => candidate.VideoId is "AAAAAAAAAAA" or "BBBBBBBBBBB")
            .Should().OnlyContain(candidate => !candidate.TitledAsSong && !candidate.Confident);
    }

    [Fact]
    public async Task An_alternate_bracket_on_a_channel_that_is_not_the_artists_is_not_titled()
    {
        var innertube = Searching(Official("WD33ii01kXI", "London Bridge", "Fergie"));

        var candidates = await FindAsync(
            Song("London Bridge", "Fergie"),
            innertube,
            Real(("WD33ii01kXI", "Fergie - London Bridge (Oh Snap) (Official Music Video)", "Some Random Channel")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.TitledAsSong.Should().BeFalse("the channel did not pass the artist check");
    }

    [Fact]
    public async Task An_alternate_bracket_naming_a_version_is_not_titled()
    {
        var innertube = Searching(Official("WD33ii01kXI", "London Bridge", "Fergie"));

        var candidates = await FindAsync(
            Song("London Bridge", "Fergie"),
            innertube,
            Real(("WD33ii01kXI", "Fergie - London Bridge (Live at the O2) (Official Music Video)", "FergieVEVO")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.TitledAsSong.Should().BeFalse();
    }

    [Fact]
    public async Task Yusuf_slash_Cat_Stevens_is_the_artists_channel_and_Father_and_Son_stays_confident()
    {
        var innertube = Searching(Official("P6zaCV4niKk", "Father and Son", "Cat Stevens"));

        var candidates = await FindAsync(
            Song("Father and Son", "Cat Stevens"),
            innertube,
            Real(("P6zaCV4niKk", "Yusuf / Cat Stevens - Father & Son", "Yusuf / Cat Stevens")));

        candidates.Should().ContainSingle().Which.Confident.Should().BeTrue();
    }

    [Fact]
    public async Task Lisa_and_Fans_is_still_not_Lisas_channel()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "Teardrop", "Lisa"));

        var candidates = await FindAsync(
            Song("Teardrop", "Lisa"),
            innertube,
            Real(("AAAAAAAAAAA", "Lisa - Teardrop (Official Video)", "Lisa & Fans")));

        candidates.Should().ContainSingle().Which.Confident.Should().BeFalse();
    }

    [Fact]
    public async Task A_sing_along_is_audio_only_and_ranked_below_the_plain_video()
    {
        var innertube = Searching(
            Official("AAAAAAAAAAA", "Some Things Never Change", "Kristen Bell"),
            Official("BBBBBBBBBBB", "Some Things Never Change", "Kristen Bell"));

        var candidates = await FindAsync(
            Song("Some Things Never Change", "Kristen Bell"),
            innertube,
            Real(
                ("AAAAAAAAAAA", "Some Things Never Change (From \"Frozen 2\"/Sing-Along)", "Kristen Bell"),
                ("BBBBBBBBBBB", "Kristen Bell - Some Things Never Change (Official Video)", "Kristen Bell")));

        var singAlong = candidates.Single(candidate => candidate.VideoId == "AAAAAAAAAAA");
        singAlong.Confident.Should().BeFalse();
        singAlong.Kind.Should().Be(MusicVideoCandidateKind.AudioOnly);
        singAlong.TitledAsSong.Should().BeFalse();
        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
    }

    [Theory]
    [InlineData("Some Things Never Change", "Kristen Bell", "Some Things Never Change (From \"Frozen 2\"/Sing-Along)")]
    [InlineData("When Will My Life Begin?", "Mandy Moore", "Mandy Moore - When Will My Life Begin? (From \"Tangled\"/Sing-Along)")]
    [InlineData("How Far I'll Go", "Auli'i Cravalho", "How far I'll go (Reprise) - Persian")]
    [InlineData("Wanna Have Good Times", "Justin Timberlake", "Trolls Wanna Have Good Times - Pop Trolls (Sub. Español)")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Frozen II - Into the unknown - Brazilian Portuguese - Panic! at the disco version")]
    [InlineData("Into the Unknown", "Panic! at the Disco", "Into the Unknown - Panic at the Disco (lower key)")]
    [InlineData("London Bridge", "Fergie", "Fergie - London Bridge - So You Think You Can Dance")]
    [InlineData("London Bridge", "Fergie", "London Bridge - Fergie | Akiva Brooks Choreography")]
    public async Task The_real_other_versions_from_the_third_review_are_never_titled_or_confident(string song, string artist, string realTitle)
    {
        var innertube = Searching(Official("AAAAAAAAAAA", song, artist), Upload("BBBBBBBBBBB", realTitle, "Some Fan"));

        var candidates = await FindAsync(Song(song, artist), innertube, Real(("AAAAAAAAAAA", realTitle, artist)));

        candidates.Should().OnlyContain(candidate => !candidate.Confident && !candidate.TitledAsSong);
    }

    [Fact]
    public async Task The_reprise_song_is_not_titled_by_the_full_songs_video()
    {
        var innertube = Searching(Official("AAAAAAAAAAA", "When Will My Life Begin", "Mandy Moore", 152_000));

        var candidates = await FindAsync(
            Song("When Will My Life Begin (reprise 1)", "Mandy Moore", 63_000),
            innertube,
            Real(("AAAAAAAAAAA", "Mandy Moore - When Will My Life Begin? (From \"Tangled\")", "Mandy Moore")));

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.TitledAsSong.Should().BeFalse();
    }
}
