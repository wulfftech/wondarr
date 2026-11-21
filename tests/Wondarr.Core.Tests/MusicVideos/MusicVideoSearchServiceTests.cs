using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>Searching for a song's video, queueing its download, and choosing the songs the scheduled task visits.</summary>
public sealed class MusicVideoSearchServiceTests : IDisposable
{
    private const string VideoId = "u7K72X4eo_s";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task A_search_asks_the_finder_about_the_song_and_hands_it_the_blocklist()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await using (var seed = _harness.NewContext())
        {
            var row = await seed.Songs.FirstAsync(candidate => candidate.Id == song.Id);
            row.MbRecordingId = "f3bba4cd-8018-468b-902e-bc8f029593e5";
            row.DurationMs = 330_026;
            seed.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = song.Id, YouTubeVideoId = "BBBBBBBBBBB", Reason = "wrong" });
            await seed.SaveChangesAsync();
        }

        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz)];

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context).SearchAsync(song.Id, CancellationToken.None);

        result!.Confident!.VideoId.Should().Be(VideoId);
        var query = _harness.Finder.Queries.Should().ContainSingle().Subject;
        query.Title.Should().Be("Teardrop");
        query.Artists.Should().Equal("Massive Attack");
        query.MbRecordingId.Should().Be("f3bba4cd-8018-468b-902e-bc8f029593e5");
        query.DurationMs.Should().Be(330_026);
        query.Blocklisted.Should().BeEquivalentTo(["BBBBBBBBBBB"]);
    }

    [Fact]
    public async Task A_blocklisted_candidate_is_dropped_even_if_the_finder_returns_it()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await using (var seed = _harness.NewContext())
        {
            seed.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = song.Id, YouTubeVideoId = VideoId, Reason = "wrong" });
            await seed.SaveChangesAsync();
        }

        _harness.Finder.Candidates =
        [
            FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz),
            FakeVideoFinder.Candidate("CCCCCCCCCCC", confident: false),
        ];

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context).SearchAsync(song.Id, CancellationToken.None);

        result!.Candidates.Select(candidate => candidate.VideoId).Should().Equal("CCCCCCCCCCC");
        result.Confident.Should().BeNull("the only confident candidate is blocklisted");
    }

    [Fact]
    public async Task A_search_for_a_song_that_is_not_there_is_null()
    {
        await _harness.InitAsync();
        await using var context = _harness.NewContext();

        (await _harness.Search(context).SearchAsync(9999, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Queueing_a_download_keeps_the_chosen_videos_title_on_the_row_and_in_the_command()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");

        await using (var context = _harness.NewContext())
        {
            await _harness.Search(context).QueueDownloadAsync(
                song.Id,
                VideoId,
                MusicVideoFoundBy.Manual,
                CommandTrigger.Manual,
                CancellationToken.None,
                videoTitle: "  Massive Attack - Teardrop  ");
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync()).VideoTitle.Should().Be("Massive Attack - Teardrop");
        await _harness.Commands.Received().EnqueueAsync(
            MusicVideoDownloadCommandHandler.CommandName,
            Arg.Is<string?>(body => body != null && body.Contains("Massive Attack - Teardrop")),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Queueing_a_download_makes_a_queued_row_and_queues_the_command()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");

        await using (var context = _harness.NewContext())
        {
            var result = await _harness.Search(context)
                .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz, CommandTrigger.Manual, CancellationToken.None);

            result.Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
            result.CommandId.Should().Be(1);
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Queued);
        row.YouTubeVideoId.Should().Be(VideoId);
        row.FoundBy.Should().Be(MusicVideoFoundBy.MusicBrainz);

        await _harness.Commands.Received(1).EnqueueAsync(
            MusicVideoDownloadCommandHandler.CommandName,
            Arg.Is<string?>(body => body!.Contains("\"songId\":" + song.Id) && body.Contains(VideoId) && body.Contains("musicBrainz")),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blocklisted_video_is_not_queued()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await using (var seed = _harness.NewContext())
        {
            seed.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = song.Id, YouTubeVideoId = VideoId, Reason = "wrong" });
            await seed.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.Blocklisted);
        (await context.MusicVideos.CountAsync()).Should().Be(0);
        await _harness.Commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_song_that_is_not_there_is_not_queued()
    {
        await _harness.InitAsync();
        await using var context = _harness.NewContext();

        var result = await _harness.Search(context)
            .QueueDownloadAsync(9999, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.SongNotFound);
    }

    [Fact]
    public async Task A_download_already_queued_for_the_song_is_in_progress()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Queued);
        await using (var seed = _harness.NewContext())
        {
            seed.Commands.Add(new CommandRecord
            {
                Name = MusicVideoDownloadCommandHandler.CommandName,
                Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(song.Id, VideoId, MusicVideoFoundBy.MusicBrainz)),
                Status = CommandStatus.Started,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, "CCCCCCCCCCC", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.InProgress);
    }

    [Fact]
    public async Task A_queued_row_with_no_command_behind_it_is_reclaimed()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Downloading, "BBBBBBBBBBB");

        await using (var context = _harness.NewContext())
        {
            var result = await _harness.Search(context)
                .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

            result.Outcome.Should().Be(MusicVideoQueueOutcome.Queued, "a crash left the row behind");
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Queued);
        row.YouTubeVideoId.Should().Be(VideoId);
    }

    [Fact]
    public async Task A_failed_row_is_reset_by_a_new_pick()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await using (var seed = _harness.NewContext())
        {
            seed.MusicVideos.Add(new MusicVideo
            {
                SongId = song.Id,
                Status = MusicVideoStatus.Failed,
                YouTubeVideoId = "BBBBBBBBBBB",
                FoundBy = MusicVideoFoundBy.YouTubeSearch,
                FailureReason = "no video stream",
            });
            await seed.SaveChangesAsync();
        }

        await using (var context = _harness.NewContext())
        {
            (await _harness.Search(context)
                .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None))
                .Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Queued);
        row.FailureReason.Should().BeNull();
        row.FoundBy.Should().Be(MusicVideoFoundBy.Manual);
    }

    [Fact]
    public async Task The_video_a_song_already_holds_is_not_queued_again_but_another_one_replaces_it_later()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, VideoId, "/videos/x.mkv");

        await using (var context = _harness.NewContext())
        {
            (await _harness.Search(context)
                .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None))
                .Outcome.Should().Be(MusicVideoQueueOutcome.AlreadyFiled);

            (await _harness.Search(context)
                .QueueDownloadAsync(song.Id, "CCCCCCCCCCC", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None, replace: true))
                .Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Present, "the filed video stays until its replacement is checked");
        row.YouTubeVideoId.Should().Be(VideoId);
    }

    [Fact]
    public async Task An_automatic_queue_never_replaces_a_filed_video()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, VideoId, "/videos/x.mkv");

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, "CCCCCCCCCCC", MusicVideoFoundBy.YouTubeSearch, CommandTrigger.Scheduled, CancellationToken.None);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.AlreadyFiled);
        await _harness.Commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_second_pick_while_a_replacement_is_on_its_way_is_refused()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, VideoId, "/videos/x.mkv");
        await _harness.AddActiveDownloadAsync(song.Id, "CCCCCCCCCCC");

        await using var context = _harness.NewContext();
        var result = await _harness.Search(context)
            .QueueDownloadAsync(song.Id, "DDDDDDDDDDD", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None, replace: true);

        result.Outcome.Should().Be(MusicVideoQueueOutcome.InProgress);
    }

    [Fact]
    public async Task A_partial_result_only_auto_downloads_a_youtube_match_that_is_confident_on_its_own()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Finder.Partial = true;

        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.YouTubeSearch)];
        await using var context = _harness.NewContext();
        var withSearch = await _harness.Search(context).SearchAsync(song.Id, CancellationToken.None);
        withSearch!.Partial.Should().BeTrue();
        withSearch.AutoDownload.Should().NotBeNull("a YouTube result that is confident on its own stands");

        _harness.Finder.Candidates = [FakeVideoFinder.Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz)];
        var withLink = await _harness.Search(context).SearchAsync(song.Id, CancellationToken.None);
        withLink!.Confident.Should().NotBeNull();
        withLink.AutoDownload.Should().BeNull("a partial result does not auto-download anything else");
    }

    [Fact]
    public async Task A_row_made_for_a_command_that_could_not_be_queued_is_taken_back()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop");
        _harness.Commands
            .EnqueueAsync(default!, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("queue down"));

        await using (var context = _harness.NewContext())
        {
            var act = () => _harness.Search(context)
                .QueueDownloadAsync(song.Id, VideoId, MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_searches_are_queued_with_auto_download_for_the_songs_that_exist()
    {
        await _harness.InitAsync();
        var first = await _harness.AddSongAsync("Teardrop");
        var second = await _harness.AddSongAsync("Angel");

        await using var context = _harness.NewContext();
        var commandIds = await _harness.Search(context)
            .QueueSearchesAsync([second.Id, first.Id, 9999], CommandTrigger.Manual, CancellationToken.None);

        commandIds.Should().HaveCount(2);
        await _harness.Commands.Received(1).EnqueueAsync(
            MusicVideoSearchCommandHandler.CommandName,
            Arg.Is<string?>(body => body!.Contains("\"songId\":" + first.Id) && body.Contains("\"autoDownload\":true")),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_missing_songs_are_the_monitored_ones_with_a_file_and_no_video_oldest_first()
    {
        await _harness.InitAsync();
        var start = _harness.Time.GetUtcNow();

        // Added newest first, so the ids run the other way from the ages.
        var newest = await _harness.AddSongAsync("Newest", "/music/a.flac");
        var middle = await _harness.AddSongAsync("Middle", "/music/b.flac");
        var oldest = await _harness.AddSongAsync("Oldest", "/music/c.flac");
        var withVideo = await _harness.AddSongAsync("Has a video", "/music/d.flac");
        var noFile = await _harness.AddSongAsync("No file");
        var unmonitored = await _harness.AddSongAsync("Unmonitored", "/music/e.flac");
        await _harness.SetAddedAsync(newest.Id, start.AddDays(3));
        await _harness.SetAddedAsync(middle.Id, start.AddDays(2));
        await _harness.SetAddedAsync(oldest.Id, start.AddDays(1));
        await _harness.AddRowAsync(withVideo.Id, MusicVideoStatus.Failed);
        await using (var seed = _harness.NewContext())
        {
            (await seed.Songs.FirstAsync(song => song.Id == unmonitored.Id)).Monitored = false;
            await seed.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        var ids = await _harness.Search(context).NextMissingAsync(10, CancellationToken.None);

        ids.Should().Equal(oldest.Id, middle.Id, newest.Id);
        ids.Should().NotContain([withVideo.Id, noFile.Id, unmonitored.Id]);
        (await _harness.Search(context).NextMissingAsync(2, CancellationToken.None)).Should().Equal(oldest.Id, middle.Id);
    }

    [Fact]
    public async Task A_song_remembered_as_fruitless_is_left_alone_for_30_days()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Teardrop", "/music/a.flac");

        await using (var context = _harness.NewContext())
        {
            await _harness.Search(context).RememberFruitlessAsync(song.Id, CancellationToken.None);
            await _harness.Search(context).RememberFruitlessAsync(song.Id, CancellationToken.None);
        }

        await using (var context = _harness.NewContext())
        {
            (await context.MusicVideoLastSearches.CountAsync()).Should().Be(1, "one row per song");
            (await _harness.Search(context).NextMissingAsync(10, CancellationToken.None)).Should().BeEmpty();
        }

        _harness.Time.Advance(TimeSpan.FromDays(29));

        await using (var context = _harness.NewContext())
        {
            (await _harness.Search(context).NextMissingAsync(10, CancellationToken.None)).Should().BeEmpty();
        }

        _harness.Time.Advance(TimeSpan.FromDays(2));

        await using (var context = _harness.NewContext())
        {
            (await _harness.Search(context).NextMissingAsync(10, CancellationToken.None)).Should().Equal(song.Id);
        }
    }
}
