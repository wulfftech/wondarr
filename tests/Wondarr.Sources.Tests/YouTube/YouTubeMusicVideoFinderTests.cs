using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.MusicVideos;
using Wondarr.Sources.Tests.Metadata;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The music video finder (ADR-0014): the MusicBrainz path over the recorded Teardrop responses, and the YouTube Music
/// search over a scripted InnerTube client. No network.
/// </summary>
public sealed class YouTubeMusicVideoFinderTests
{
    private const string TeardropVideo = "u7K72X4eo_s";

    private static readonly IReadOnlySet<string> NoBlocklist = new HashSet<string>();

    private static MusicVideoQuery Teardrop(
        string? mbid = MusicBrainzRelationsTests.TeardropAudioId,
        IReadOnlySet<string>? blocklisted = null,
        int? durationMs = 330_026) =>
        new("Teardrop", ["Massive Attack"], mbid, durationMs, blocklisted ?? NoBlocklist);

    private static InnertubeResult Result(
        string videoId,
        string title,
        string[] artists,
        string? type = InnertubeVideoTypes.OfficialVideo,
        int? durationMs = 331_000) =>
        new(videoId, title, artists, null, durationMs, type, false);

    /// <summary>Every search result a test scripted, by video id: what the echoing oEmbed stub answers from.</summary>
    private readonly Dictionary<string, InnertubeResult> _scripted = new(StringComparer.Ordinal);

    private InnertubeSearchResult Search(params InnertubeResult[] results)
    {
        foreach (var result in results)
        {
            _scripted[result.VideoId] = result;
        }

        return new("Massive Attack Teardrop", InnertubeSearchFilter.Videos, null, results);
    }

    /// <summary>
    /// The finder over a scripted search. Unless a test scripts the real titles itself, oEmbed echoes what the search
    /// showed: the same title, uploaded by the first credited artist.
    /// </summary>
    private YouTubeMusicVideoFinder Finder(
        IMusicBrainzClient musicBrainz,
        StubInnertubeClient innertube,
        IYouTubeOEmbedClient? oEmbed = null) =>
        new(musicBrainz, innertube, oEmbed ?? new StubOEmbedClient(videoId =>
            _scripted.TryGetValue(videoId, out var result)
                ? new YouTubeOEmbed(result.Title, result.Artists.Count > 0 ? result.Artists[0] : "Some Uploader", null)
                : null), NullLogger<YouTubeMusicVideoFinder>.Instance);

    [Fact]
    public async Task The_music_brainz_video_link_is_a_confident_candidate()
    {
        var (musicBrainz, handler) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.VideoId.Should().Be(TeardropVideo);
        candidate.Confident.Should().BeTrue();
        candidate.FoundBy.Should().Be(MusicVideoFoundBy.MusicBrainz);
        candidate.DurationMs.Should().Be(284_000);
        candidate.ThumbnailUrl.Should().Be("https://i.ytimg.com/vi/u7K72X4eo_s/hqdefault.jpg");
        candidate.Title.Should().Be("Teardrop");
        candidate.Artists.Should().Equal("Massive Attack");

        // The song's recording, then the video recording it links to: two lookups.
        handler.Requests.Select(uri => uri.AbsolutePath.Split('/').Last())
            .Should().Equal(MusicBrainzRelationsTests.TeardropAudioId, MusicBrainzRelationsTests.TeardropVideoId);
    }

    [Fact]
    public async Task The_audio_recordings_own_youtube_links_are_not_candidates()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        // The audio recording links music.youtube.com/watch?v=3h-JYx76QNM, the art track.
        candidates.Select(candidate => candidate.VideoId).Should().NotContain("3h-JYx76QNM");
    }

    [Fact]
    public async Task Only_a_forward_link_to_a_video_recording_is_followed()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        musicBrainz.GetRecordingRelationsAsync(MusicBrainzRelationsTests.TeardropAudioId, Arg.Any<CancellationToken>())
            .Returns(new MbRecordingRelations(
                MusicBrainzRelationsTests.TeardropAudioId,
                Video: false,
                330_026,
                [
                    new MbMusicVideoRelation("11111111-1111-1111-1111-111111111111", TargetIsVideo: true, "backward"),
                    new MbMusicVideoRelation("22222222-2222-2222-2222-222222222222", TargetIsVideo: false, "forward"),
                ],
                [new MbUrlRelation("free streaming", "https://www.youtube.com/watch?v=AAAAAAAAAAA")]));
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        candidates.Should().BeEmpty("a backward link points at audio, a forward link to an audio recording is not a video, and the audio's own links are not candidates");
        await musicBrainz.Received(1).GetRecordingRelationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_target_that_turns_out_not_to_be_a_video_is_skipped()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        musicBrainz.GetRecordingRelationsAsync(MusicBrainzRelationsTests.TeardropAudioId, Arg.Any<CancellationToken>())
            .Returns(new MbRecordingRelations(
                MusicBrainzRelationsTests.TeardropAudioId,
                false,
                null,
                [new MbMusicVideoRelation(MusicBrainzRelationsTests.TeardropVideoId, TargetIsVideo: true, "forward")],
                []));
        musicBrainz.GetRecordingRelationsAsync(MusicBrainzRelationsTests.TeardropVideoId, Arg.Any<CancellationToken>())
            .Returns(new MbRecordingRelations(
                MusicBrainzRelationsTests.TeardropVideoId,
                Video: false,
                284_000,
                [],
                [new MbUrlRelation("free streaming", "https://www.youtube.com/watch?v=u7K72X4eo_s")]));
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        (await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_url_that_is_not_a_youtube_watch_page_is_ignored()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        musicBrainz.GetRecordingRelationsAsync(MusicBrainzRelationsTests.TeardropAudioId, Arg.Any<CancellationToken>())
            .Returns(new MbRecordingRelations(
                MusicBrainzRelationsTests.TeardropAudioId,
                false,
                null,
                [new MbMusicVideoRelation(MusicBrainzRelationsTests.TeardropVideoId, TargetIsVideo: true, "forward")],
                []));
        musicBrainz.GetRecordingRelationsAsync(MusicBrainzRelationsTests.TeardropVideoId, Arg.Any<CancellationToken>())
            .Returns(new MbRecordingRelations(
                MusicBrainzRelationsTests.TeardropVideoId,
                Video: true,
                284_000,
                [],
                [
                    new MbUrlRelation("free streaming", "https://vimeo.com/12345"),
                    new MbUrlRelation("free streaming", "https://evil.example/watch?v=u7K72X4eo_s"),
                    new MbUrlRelation("free streaming", "https://www.youtube.com/channel/UCabc"),
                ]));
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        (await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_confident_official_video_by_the_songs_artist_is_confident_and_a_lyric_video_a_different_artist_and_a_user_upload_are_not()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Teardrop (Lyric Video)", ["Massive Attack"]),
                Result("BBBBBBBBBBB", "Teardrop (Official Video)", ["Some Cover Band"]),
                Result("CCCCCCCCCCC", "Teardrop (Official Video)", ["Massive Attack"], InnertubeVideoTypes.UserUpload),
                Result("DDDDDDDDDDD", "Teardrop (Official Video)", ["Massive Attack"]),
                Result("EEEEEEEEEEE", "Teardrop (Visualizer)", ["Massive Attack"]),
                Result("FFFFFFFFFFF", "Angel (Official Video)", ["Massive Attack"])));

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        candidates.Should().HaveCount(6);
        candidates.Single(candidate => candidate.Confident).VideoId.Should().Be("DDDDDDDDDDD");
        candidates[0].VideoId.Should().Be("DDDDDDDDDDD", "the only confident result leads");
        candidates.Should().OnlyContain(candidate => candidate.FoundBy == MusicVideoFoundBy.YouTubeSearch);

        innertube.Requests.Should().ContainSingle().Which.Should().Be(new InnertubeSearchRequest("Massive Attack Teardrop", InnertubeSearchFilter.Videos));
    }

    [Fact]
    public async Task The_artist_must_be_named_as_a_whole_name()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Teardrop (Official Video)", ["Massive"]),
                Result("BBBBBBBBBBB", "Teardrop (Official Video)", ["Massive Attack & Friends"]),
                Result("CCCCCCCCCCC", "Teardrop (Official Video)", ["Massive Attack feat. Elizabeth Fraser"])));

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        candidates.Where(candidate => candidate.Confident).Select(candidate => candidate.VideoId)
            .Should().BeEquivalentTo(["BBBBBBBBBBB", "CCCCCCCCCCC"], "a credit is split into its names, and 'Massive' is not 'Massive Attack'");
    }

    [Fact]
    public async Task The_other_results_are_ranked_official_first_then_artist_then_title_then_length()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("EEEEEEEEEEE", "Teardrop", ["Some Uploader"], InnertubeVideoTypes.UserUpload, durationMs: 330_000),
                Result("DDDDDDDDDDD", "Teardrop live", ["Massive Attack"], InnertubeVideoTypes.UserUpload, durationMs: 330_000),
                Result("CCCCCCCCCCC", "Teardrop (Lyric Video)", ["Massive Attack"], InnertubeVideoTypes.UserUpload, durationMs: 330_000),
                Result("BBBBBBBBBBB", "Teardrop (Lyric Video)", ["Massive Attack"], InnertubeVideoTypes.OfficialVideo, durationMs: 330_000),
                Result("AAAAAAAAAAA", "Teardrop (Lyric Video)", ["Massive Attack"], InnertubeVideoTypes.OfficialVideo, durationMs: 400_000)));

        // No real title is known: a lyric video would otherwise be read as audio only, which is its own kind.
        var candidates = await Finder(musicBrainz, innertube, new StubOEmbedClient(_ => null)).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        candidates.Should().OnlyContain(candidate => !candidate.Confident);
        candidates.Select(candidate => candidate.VideoId).Take(2).Should().Equal("BBBBBBBBBBB", "AAAAAAAAAAA");
        candidates[0].Kind.Should().Be(MusicVideoCandidateKind.OfficialVideo);
        candidates[0].Score.Should().BeGreaterThan(candidates[1].Score, "the closer length ranks first");
        candidates.Select(candidate => candidate.Score).Should().BeInDescendingOrder();
        candidates[^1].VideoId.Should().Be("EEEEEEEEEEE", "not the artist's, not official: the last");
    }

    [Fact]
    public async Task A_fan_upload_that_names_the_artist_in_its_title_outranks_an_unrelated_upload_of_the_right_length()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Hello World", ["Unrelated Uploader"], InnertubeVideoTypes.UserUpload, durationMs: 186_000),
                Result("BBBBBBBBBBB", "hello world - the saddle club", ["Some Fan"], InnertubeVideoTypes.UserUpload, durationMs: 187_000),
                Result("CCCCCCCCCCC", "The Saddle Club - Hello World (Music Video) (1080p Remaster by aTunes)", ["aTunes"], InnertubeVideoTypes.UserUpload, durationMs: 190_000),
                Result("DDDDDDDDDDD", "The Saddle Club \"Hello World\"", ["Another Fan"], InnertubeVideoTypes.UserUpload, durationMs: 200_000),
                Result("EEEEEEEEEEE", "The Saddle Club & Friends – Hello World", ["Third Fan"], InnertubeVideoTypes.UserUpload, durationMs: 230_000)));
        var query = new MusicVideoQuery("Hello World", ["The Saddle Club"], null, 187_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        candidates.Select(candidate => candidate.VideoId).Should().EndWith("AAAAAAAAAAA");
        candidates.Select(candidate => candidate.VideoId).Take(2).Should().BeEquivalentTo(["BBBBBBBBBBB", "CCCCCCCCCCC"]);
        candidates.Should().OnlyContain(candidate => !candidate.Confident, "a user upload is never confident, whatever its title says");
        candidates.Where(candidate => candidate.ArtistNamed).Select(candidate => candidate.VideoId)
            .Should().BeEquivalentTo(["BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD", "EEEEEEEEEEE"], "the artist is read from the title, in every form");
    }

    [Fact]
    public async Task A_remix_titled_official_video_stays_first_and_not_confident_among_live_vocals_only_and_mix_uploads()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("BBBBBBBBBBB", "Queen - Under Pressure 1981 Live Video Full HD", ["Fan One"], InnertubeVideoTypes.UserUpload, durationMs: 248_000),
                Result("CCCCCCCCCCC", "Queen - Under Pressure (Vocals Only)", ["Fan Two"], InnertubeVideoTypes.UserUpload, durationMs: 248_000),
                Result("DDDDDDDDDDD", "Queen & David Bowie - Under Pressure (Juno Ray Club Mix) [Audio]", ["Fan Three"], InnertubeVideoTypes.UserUpload, durationMs: 248_000),
                Result("AAAAAAAAAAA", "Under Pressure (Rah Mix) (feat. David Bowie)", ["Queen"], InnertubeVideoTypes.OfficialVideo, durationMs: 254_000)));
        var query = new MusicVideoQuery("Under Pressure", ["Queen"], null, 248_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        candidates[0].VideoId.Should().Be("AAAAAAAAAAA");
        candidates.Should().OnlyContain(candidate => !candidate.Confident, "a mix in the title is a different version");
    }

    [Fact]
    public async Task An_official_video_of_a_live_remastered_version_is_not_confident()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Queen - Under Pressure (Live at Wembley 1986 - Remastered)", ["Queen"], InnertubeVideoTypes.OfficialVideo, durationMs: 248_000),
                Result("BBBBBBBBBBB", "Queen - Under Pressure (HD Live)", ["Queen"], InnertubeVideoTypes.OfficialVideo, durationMs: 248_000)));
        var query = new MusicVideoQuery("Under Pressure", ["Queen"], null, 248_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        candidates.Should().OnlyContain(candidate => !candidate.Confident, "the version word survives the decorations");
    }

    [Fact]
    public async Task The_artist_in_the_title_earns_ranking_points_but_never_confidence()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Unrelated Song", ["Someone"], InnertubeVideoTypes.UserUpload, durationMs: 248_000),
                Result("BBBBBBBBBBB", "Queen - Under Pressure (Official Video)", ["VEVO Channel"], InnertubeVideoTypes.OfficialVideo, durationMs: 248_000),
                Result("CCCCCCCCCCC", "Under Pressure (feat. Queen)", ["Other Act"], InnertubeVideoTypes.OfficialVideo, durationMs: 248_000)));
        var query = new MusicVideoQuery("Under Pressure", ["Queen"], null, 248_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        candidates.Should().OnlyContain(candidate => !candidate.Confident);
        candidates[0].VideoId.Should().Be("BBBBBBBBBBB");
        candidates.Select(candidate => candidate.VideoId).Should().EndWith("AAAAAAAAAAA");
    }

    [Fact]
    public async Task A_featured_artist_in_the_title_is_not_the_videos_artist()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(Result("AAAAAAAAAAA", "Band B - Song X (feat. Band A)", ["Band B"], InnertubeVideoTypes.OfficialVideo, durationMs: 200_000)));
        var query = new MusicVideoQuery("Song X", ["Band A"], null, 200_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Confident.Should().BeFalse();
        candidate.ArtistNamed.Should().BeFalse();
    }

    [Fact]
    public async Task An_artist_whose_name_holds_a_version_word_is_not_marked_another_version()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var littleMixStub = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Little Mix - Shout Out to My Ex (Official Video)", ["Little Mix"], InnertubeVideoTypes.OfficialVideo, durationMs: 250_000),
                Result("CCCCCCCCCCC", "Little Mix - Shout Out to My Ex", ["Fan"], InnertubeVideoTypes.UserUpload, durationMs: 250_000)));
        var liveStub = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(Result("BBBBBBBBBBB", "Live - Lightning Crashes", ["Fan"], InnertubeVideoTypes.UserUpload, durationMs: 250_000)));

        var little = await Finder(musicBrainz, littleMixStub).FindAsync(
            new MusicVideoQuery("Shout Out to My Ex", ["Little Mix"], null, 250_000, NoBlocklist),
            CancellationToken.None);
        little.Single(candidate => candidate.VideoId == "AAAAAAAAAAA").Confident.Should().BeTrue();
        little.Single(candidate => candidate.VideoId == "CCCCCCCCCCC").Score.Should().BeGreaterThan(500 + 250, "no version penalty for a band called Mix");

        var live = await Finder(musicBrainz, liveStub).FindAsync(
            new MusicVideoQuery("Lightning Crashes", ["Live"], null, 250_000, NoBlocklist),
            CancellationToken.None);
        var upload = live.Single();
        upload.ArtistNamed.Should().BeTrue();
        upload.Score.Should().BeGreaterThan(500 + 250, "no version penalty for the band called Live");
    }

    [Fact]
    public async Task A_live_or_cover_upload_ranks_below_a_studio_upload_that_names_the_artist()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Queen - Under Pressure (Cover)", ["Fan One"], InnertubeVideoTypes.UserUpload, durationMs: 248_000),
                Result("BBBBBBBBBBB", "Queen - Under Pressure", ["Fan Two"], InnertubeVideoTypes.UserUpload, durationMs: 240_000)));
        var query = new MusicVideoQuery("Under Pressure", ["Queen"], null, 248_000, NoBlocklist);

        var candidates = await Finder(musicBrainz, innertube).FindAsync(query, CancellationToken.None);

        candidates.Select(candidate => candidate.VideoId).Should().Equal("BBBBBBBBBBB", "AAAAAAAAAAA");
    }

    [Fact]
    public async Task Music_brainz_outranks_a_confident_search_result_which_outranks_the_rest()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result("AAAAAAAAAAA", "Teardrop live", ["Massive Attack"], InnertubeVideoTypes.UserUpload),
                Result("BBBBBBBBBBB", "Teardrop (Official Video)", ["Massive Attack"]),
                Result(TeardropVideo, "Teardrop (Official Video)", ["Massive Attack"])));

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        candidates.Select(candidate => candidate.VideoId).Should().Equal(TeardropVideo, "BBBBBBBBBBB", "AAAAAAAAAAA");
        candidates[0].FoundBy.Should().Be(MusicVideoFoundBy.MusicBrainz, "the same video found twice keeps the MusicBrainz find");
        candidates[1].Confident.Should().BeTrue();
        candidates[2].Confident.Should().BeFalse();
    }

    [Fact]
    public async Task A_video_found_by_both_is_listed_once()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(Result(TeardropVideo, "Teardrop (Official Video)", ["Massive Attack"])));

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task A_blocklisted_video_id_is_dropped_from_both_sources()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Respond(
            InnertubeSearchFilter.Videos,
            Search(
                Result(TeardropVideo, "Teardrop (Official Video)", ["Massive Attack"]),
                Result("BBBBBBBBBBB", "Teardrop (Official Video)", ["Massive Attack"])));
        var blocklist = new HashSet<string> { TeardropVideo, "BBBBBBBBBBB" };

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(blocklisted: blocklist), CancellationToken.None);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task A_song_without_a_recording_id_never_asks_music_brainz()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        var innertube = new StubInnertubeClient().Respond(InnertubeSearchFilter.Videos, Search());

        await Finder(musicBrainz, innertube).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        await musicBrainz.DidNotReceiveWithAnyArgs().GetRecordingRelationsAsync(default!, default);
    }

    [Fact]
    public async Task A_failing_source_does_not_hide_what_the_other_found()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Fail(InnertubeSearchFilter.Videos, new InvalidOperationException("429"));

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        candidates.Should().ContainSingle().Which.FoundBy.Should().Be(MusicVideoFoundBy.MusicBrainz);
    }

    [Fact]
    public async Task A_lookup_that_failed_everywhere_is_an_error_not_an_empty_answer()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        musicBrainz.GetRecordingRelationsAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("MusicBrainz is down"));
        var innertube = new StubInnertubeClient().Fail(InnertubeSearchFilter.Videos, new InvalidOperationException("429"));

        var act = () => Finder(musicBrainz, innertube).FindAsync(Teardrop(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("a failed lookup must not read as 'no video'");
    }

    [Fact]
    public async Task A_failed_search_with_nothing_from_music_brainz_is_an_error()
    {
        var (musicBrainz, _) = MusicBrainzRelationsTests.CreateClient();
        var innertube = new StubInnertubeClient().Fail(InnertubeSearchFilter.Videos, new InvalidOperationException("429"));

        var act = () => Finder(musicBrainz, innertube).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
