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

    private static InnertubeSearchResult Search(params InnertubeResult[] results) =>
        new("Massive Attack Teardrop", InnertubeSearchFilter.Videos, null, results);

    private static YouTubeMusicVideoFinder Finder(
        IMusicBrainzClient musicBrainz,
        StubInnertubeClient innertube) =>
        new(musicBrainz, innertube, NullLogger<YouTubeMusicVideoFinder>.Instance);

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

        var candidates = await Finder(musicBrainz, innertube).FindAsync(Teardrop(mbid: null), CancellationToken.None);

        candidates.Should().OnlyContain(candidate => !candidate.Confident);
        candidates.Select(candidate => candidate.VideoId).Take(2).Should().Equal("BBBBBBBBBBB", "AAAAAAAAAAA");
        candidates[0].Kind.Should().Be(MusicVideoCandidateKind.OfficialVideo);
        candidates[0].Score.Should().BeGreaterThan(candidates[1].Score, "the closer length ranks first");
        candidates.Select(candidate => candidate.Score).Should().BeInDescendingOrder();
        candidates[^1].VideoId.Should().Be("EEEEEEEEEEE", "not the artist's, not official: the last");
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
