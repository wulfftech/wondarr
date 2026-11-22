using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>
/// M-70 items 1 (the pending replacement), 6a (the queue position of a queued download) and 6b (the title backfill of
/// the scan), against the harness: a real SQLite database, a fake queue and a fake title source.
/// </summary>
public sealed class MusicVideoReviewThreeTests : IDisposable
{
    private const string FatBottomedGirls = "VMnjF1O4eH0";

    private readonly MusicVideoHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<Song> SeedPresentFatBottomedGirlsAsync()
    {
        await _harness.InitAsync();
        var song = await _harness.AddSongAsync("Fat Bottomed Girls", artist: "Queen");
        await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, FatBottomedGirls, _harness.Temp.Full("videos/x.mkv"));

        return song;
    }

    private async Task AddCommandAsync(long songId, string videoId, CommandStatus status, string? title)
    {
        await using var context = _harness.NewContext();
        context.Commands.Add(new CommandRecord
        {
            Name = MusicVideoDownloadCommandHandler.CommandName,
            Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, videoId, MusicVideoFoundBy.Manual, title)),
            Status = status,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_queued_download_for_a_present_row_is_a_pending_replacement_that_waits()
    {
        var song = await SeedPresentFatBottomedGirlsAsync();
        await AddCommandAsync(song.Id, "AAAAAAAAAAA", CommandStatus.Queued, "Queen - Fat Bottomed Girls (Official Video)");

        await using var context = _harness.NewContext();
        var pending = await _harness.Videos(context).GetPendingReplacementAsync(song.Id, CancellationToken.None);

        pending.Should().Be(new MusicVideoPendingReplacement("AAAAAAAAAAA", "Queen - Fat Bottomed Girls (Official Video)", MusicVideoPendingStatus.Queued));
    }

    [Fact]
    public async Task A_running_download_is_downloading_and_comes_before_a_queued_one()
    {
        var song = await SeedPresentFatBottomedGirlsAsync();
        await AddCommandAsync(song.Id, "BBBBBBBBBBB", CommandStatus.Queued, null);
        await AddCommandAsync(song.Id, FatBottomedGirls, CommandStatus.Started, null);

        await using var context = _harness.NewContext();
        var pending = await _harness.Videos(context).GetPendingReplacementAsync(song.Id, CancellationToken.None);

        pending.Should().NotBeNull();
        pending!.Status.Should().Be(MusicVideoPendingStatus.Downloading);
        pending.YouTubeVideoId.Should().Be(FatBottomedGirls);
        pending.VideoTitle.Should().BeNull("the command was queued without a title");
    }

    [Fact]
    public async Task No_command_another_songs_command_a_finished_command_or_a_row_that_is_not_present_is_no_pending_replacement()
    {
        var song = await SeedPresentFatBottomedGirlsAsync();
        var other = await _harness.AddSongAsync("Angel");
        var queuedRow = await _harness.AddSongAsync("Teardrop");
        await _harness.AddRowAsync(queuedRow.Id, MusicVideoStatus.Queued, "CCCCCCCCCCC");
        await AddCommandAsync(other.Id, "DDDDDDDDDDD", CommandStatus.Started, null);
        await AddCommandAsync(song.Id, "EEEEEEEEEEE", CommandStatus.Completed, null);
        await AddCommandAsync(queuedRow.Id, "CCCCCCCCCCC", CommandStatus.Started, null);

        await using var context = _harness.NewContext();
        var videos = _harness.Videos(context);

        (await videos.GetPendingReplacementAsync(song.Id, CancellationToken.None)).Should().BeNull();
        (await videos.GetPendingReplacementAsync(queuedRow.Id, CancellationToken.None)).Should().BeNull("a row that is not present is shown by its own status");
        (await videos.GetPendingReplacementAsync(987_654, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_queued_download_says_how_many_other_downloads_are_ahead_of_it()
    {
        _harness.CommandCount = 1_000;
        await _harness.InitAsync();
        var first = await _harness.AddSongAsync("Teardrop");
        var second = await _harness.AddSongAsync("Angel");
        var third = await _harness.AddSongAsync("Inertia Creeps");

        await using (var context = _harness.NewContext())
        {
            var alone = await _harness.Search(context).QueueDownloadAsync(first.Id, "AAAAAAAAAAA", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

            alone.Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
            alone.AheadInQueue.Should().Be(0, "nothing else is queued, so it starts at once");
        }

        await _harness.AddActiveDownloadAsync(first.Id, "AAAAAAAAAAA", CommandStatus.Started);
        await _harness.AddActiveDownloadAsync(second.Id, "BBBBBBBBBBB", CommandStatus.Queued);
        await _harness.AddActiveDownloadAsync(second.Id, "FFFFFFFFFFF", CommandStatus.Completed);

        await using var fresh = _harness.NewContext();
        var queued = await _harness.Search(fresh).QueueDownloadAsync(third.Id, "CCCCCCCCCCC", MusicVideoFoundBy.Manual, CommandTrigger.Manual, CancellationToken.None);

        queued.Outcome.Should().Be(MusicVideoQueueOutcome.Queued);
        queued.AheadInQueue.Should().Be(2, "one running and one queued, and the finished one does not count");
    }

    // 6b: the title backfill.

    private async Task<List<long>> SeedUntitledRowsAsync(int count)
    {
        await _harness.InitAsync();
        var ids = new List<long>();

        for (var index = 0; index < count; index++)
        {
            var song = await _harness.AddSongAsync($"Song {index}");
            var row = await _harness.AddRowAsync(song.Id, MusicVideoStatus.Present, $"video{index:D6}", _harness.Temp.Full($"videos/{index}.mkv"));
            ids.Add(row.Id);
        }

        return ids;
    }

    private static MusicVideoTitleBackfill Backfill(WondarrDbContext context, IMusicVideoTitleSource source) =>
        new(context, source, NullLogger<MusicVideoTitleBackfill>.Instance);

    [Fact]
    public async Task The_backfill_fills_the_missing_title_of_a_video_downloaded_before_titles_were_kept()
    {
        var song = await SeedPresentFatBottomedGirlsAsync();
        var source = new FakeTitles(videoId => videoId == FatBottomedGirls ? "Queen - Fat Bottomed Girls (Official Video)" : null);

        await using (var context = _harness.NewContext())
        {
            (await Backfill(context, source).FillMissingAsync(CancellationToken.None)).Should().Be(1);
        }

        await using var fresh = _harness.NewContext();
        var row = await fresh.MusicVideos.SingleAsync(video => video.SongId == song.Id);
        row.VideoTitle.Should().Be("Queen - Fat Bottomed Girls (Official Video)");
        row.UpdatedAt.Should().Be(row.CreatedAt, "a title fill is not a status change");
    }

    [Fact]
    public async Task The_backfill_looks_up_at_most_twenty_videos_two_at_a_time()
    {
        await SeedUntitledRowsAsync(25);
        var source = new FakeTitles(videoId => "Title of " + videoId) { Delay = TimeSpan.FromMilliseconds(30) };

        await using (var context = _harness.NewContext())
        {
            (await Backfill(context, source).FillMissingAsync(CancellationToken.None)).Should().Be(MusicVideoTitleBackfill.MaxPerRun);
        }

        source.Lookups.Should().HaveCount(20).And.OnlyHaveUniqueItems();
        source.MaxConcurrent.Should().BeLessThanOrEqualTo(2).And.BeGreaterThan(0);

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync(video => video.VideoTitle != null)).Should().Be(20);
        (await fresh.MusicVideos.CountAsync(video => video.VideoTitle == null)).Should().Be(5, "the next scan takes the rest");
    }

    [Fact]
    public async Task The_backfill_leaves_titled_rows_found_files_and_rows_that_are_not_present_alone()
    {
        await _harness.InitAsync();
        var titled = await _harness.AddSongAsync("Titled");
        var found = await _harness.AddSongAsync("Found");
        var downloading = await _harness.AddSongAsync("Downloading");
        var titledRow = await _harness.AddRowAsync(titled.Id, MusicVideoStatus.Present, "AAAAAAAAAAA", _harness.Temp.Full("videos/a.mkv"));
        await _harness.AddRowAsync(found.Id, MusicVideoStatus.Present, string.Empty, _harness.Temp.Full("videos/b.mkv"), MusicVideoFoundBy.Existing);
        await _harness.AddRowAsync(downloading.Id, MusicVideoStatus.Downloading, "CCCCCCCCCCC");

        await using (var context = _harness.NewContext())
        {
            await context.MusicVideos.Where(video => video.Id == titledRow.Id).ExecuteUpdateAsync(set => set.SetProperty(video => video.VideoTitle, "Kept"));
        }

        var source = new FakeTitles(videoId => "Looked up " + videoId);

        await using (var context = _harness.NewContext())
        {
            (await Backfill(context, source).FillMissingAsync(CancellationToken.None)).Should().Be(0);
        }

        source.Lookups.Should().BeEmpty();

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.SingleAsync(video => video.Id == titledRow.Id)).VideoTitle.Should().Be("Kept");
    }

    [Fact]
    public async Task A_lookup_that_fails_or_answers_nothing_leaves_the_title_empty_and_the_others_are_filled()
    {
        await SeedUntitledRowsAsync(3);
        var source = new FakeTitles(videoId => videoId.EndsWith('0') ? null : videoId.EndsWith('1') ? throw new HttpRequestException("offline") : "Fine");

        await using (var context = _harness.NewContext())
        {
            (await Backfill(context, source).FillMissingAsync(CancellationToken.None)).Should().Be(1);
        }

        await using var fresh = _harness.NewContext();
        (await fresh.MusicVideos.CountAsync(video => video.VideoTitle == "Fine")).Should().Be(1);
    }

    [Fact]
    public async Task The_scan_runs_the_backfill_and_says_so_and_a_failing_backfill_does_not_fail_the_scan()
    {
        var scanner = Substitute.For<IMusicVideoFileScanner>();
        scanner.ScanAsync(Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new MusicVideoScanResult(0, 0, 0, 0, 0, 0));
        var identifier = Substitute.For<IMusicVideoIdentifier>();
        identifier.IdentifyPendingAsync(Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new MusicVideoIdentifyResult(0, 0, 0));
        var backfill = Substitute.For<IMusicVideoTitleBackfill>();
        backfill.FillMissingAsync(Arg.Any<CancellationToken>()).Returns(3);
        var options = new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = true });
        var context = new CommandContext(1, null, CommandTrigger.Manual, _ => Task.CompletedTask);

        var handler = new MusicVideoScanCommandHandler(scanner, identifier, options, NullLogger<MusicVideoScanCommandHandler>.Instance, backfill);

        (await handler.ExecuteAsync(context, CancellationToken.None)).Should().Contain("3 video title(s) filled");

        backfill.FillMissingAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ => throw new InvalidOperationException("db busy"));

        (await handler.ExecuteAsync(context, CancellationToken.None)).Should().Contain("0 unmatched").And.NotContain("filled");
    }

    private sealed class FakeTitles(Func<string, string?> answer) : IMusicVideoTitleSource
    {
        private int _running;
        private int _max;

        public List<string> Lookups { get; } = [];

        public TimeSpan Delay { get; init; }

        public int MaxConcurrent => Volatile.Read(ref _max);

        public async Task<string?> GetTitleAsync(string videoId, CancellationToken cancellationToken)
        {
            lock (Lookups)
            {
                Lookups.Add(videoId);
            }

            var now = Interlocked.Increment(ref _running);

            int seen;

            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                return answer(videoId);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
