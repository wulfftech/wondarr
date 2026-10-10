using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>A search that found something to grab waits for a download slot instead of giving up (M-09).</summary>
public sealed class SlotWaitTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_search_with_every_slot_busy_waits_and_grabs_without_a_second_search()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var progress = new List<string>();
        host.SlotWait.ReportProgressAsync = message =>
        {
            lock (progress)
            {
                progress.Add(message);
            }

            return Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() =>
        {
            lock (progress)
            {
                return progress.Count > 0;
            }
        });

        search.IsCompleted.Should().BeFalse("every download slot is taken");
        progress.Should().Equal("Waiting for a download slot");
        host.Provider.Grabs.Should().BeEmpty();

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        result.QueueItemId.Should().NotBeNull("the grab happened once a slot freed");
        host.Provider.Grabs.Should().ContainSingle();
        host.Provider.Requests.Should().ContainSingle("the candidates the run accepted are what is grabbed; there is no second search");
    }

    [Fact]
    public async Task A_search_that_never_gets_a_slot_ends_cancelled_after_the_wait_and_the_song_stays_wanted()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.SlotWaitMinutes = 2);
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        await FillSlotsAsync(host);

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Cancelled);
        result.Message.Should().Be("still no free download slot after 2 min");
        result.QueueItemId.Should().BeNull();
        host.Provider.Grabs.Should().BeEmpty();

        await using var context = host.Database.CreateContext(host.Time);
        (await context.QueueItems.AsNoTracking().AnyAsync(item => item.SongId == songId, Token))
            .Should().BeFalse("nothing was queued for the song, so Missing Search still finds it");
    }

    [Fact]
    public async Task Cancelling_a_search_that_is_waiting_ends_the_run_cancelled()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        await FillSlotsAsync(host);
        var progress = 0;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            Interlocked.Increment(ref progress);

            return Task.CompletedTask;
        };

        using var cancel = new CancellationTokenSource();
        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, cancel.Token));

        await WaitUntilAsync(() => Volatile.Read(ref progress) > 0);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);

        await using var context = host.Database.CreateContext(host.Time);
        var run = await context.SearchRuns.AsNoTracking().SingleAsync(row => row.SongId == songId, Token);

        run.Outcome.Should().Be(SearchOutcome.Cancelled);
        run.FinishedAt.Should().NotBeNull("the run must not be left open");
    }

    [Fact]
    public async Task A_candidate_blocklisted_during_the_wait_is_not_grabbed()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        var candidate = SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac");
        host.Provider.Candidates.Add(candidate);
        var blockers = await FillSlotsAsync(host);
        var progress = 0;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            Interlocked.Increment(ref progress);

            return Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() => Volatile.Read(ref progress) > 0);

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Wondarr.Core.Blocklisting.IBlocklistService>().AddAsync(
                new BlocklistItem
                {
                    SongId = songId,
                    SourceType = candidate.SourceType,
                    BlocklistKey = candidate.BlocklistKey,
                    Reason = "bad",
                },
                Token);
        }

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        result.QueueItemId.Should().BeNull();
        host.Provider.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_search_with_a_free_slot_grabs_at_once_and_reports_nothing()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var reported = false;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            reported = true;

            return Task.CompletedTask;
        };

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        reported.Should().BeFalse();
    }

    [Fact]
    public void The_wait_is_validated()
    {
        var validator = new SearchOptionsValidator();

        validator.Validate(null, new SearchOptions { SlotWaitMinutes = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new SearchOptions { SlotWaitMinutes = 1441 }).Failed.Should().BeTrue();
        validator.Validate(null, new SearchOptions()).Succeeded.Should().BeTrue();
        new SearchOptions().SlotWaitMinutes.Should().Be(30);
    }

    /// <summary>Takes every download slot with a song of its own and returns the queue item ids.</summary>
    private static async Task<List<long>> FillSlotsAsync(SearchTestHost host)
    {
        var items = new List<long>();

        for (var index = 0; index < host.Options.MaxActiveDownloads; index++)
        {
            var blocker = await host.SeedSongAsync($"Blocker {index}");
            items.Add(await SeedActiveQueueItemAsync(host, blocker));
        }

        return items;
    }

    private static async Task FinishItemAsync(SearchTestHost host, long itemId)
    {
        await using var context = host.Database.CreateContext(host.Time);
        var item = await context.QueueItems.SingleAsync(row => row.Id == itemId, Token);

        item.State = QueueItemState.Imported;
        await context.SaveChangesAsync(Token);
    }

    /// <summary>Moves the fake clock on in poll-sized steps until the search ends.</summary>
    private static async Task<SongSearchResult> AdvanceUntilAsync(SearchTestHost host, Task<SongSearchResult> search)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (!search.IsCompleted && DateTime.UtcNow < deadline)
        {
            host.Time.Advance(TimeSpan.FromSeconds(host.Options.SlotWaitSeconds));
            await Task.Delay(10);
        }

        search.IsCompleted.Should().BeTrue("the search should have ended");

        return await search;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the search should have started waiting");
    }

    private static async Task<long> SeedActiveQueueItemAsync(SearchTestHost host, long songId)
    {
        await using var context = host.Database.CreateContext(host.Time);

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = host.Time.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = $"peer\u001fMusic\\{songId}.flac",
            DisplayName = "one.flac",
            RemotePath = $"Music\\{songId}.flac",
            Provider = "peer",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        var item = new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = SourceTypes.Soulseek,
            State = QueueItemState.Queued,
            Destination = $"wondarr/{run.Id}",
            Attempt = 1,
        };

        context.QueueItems.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }
}
