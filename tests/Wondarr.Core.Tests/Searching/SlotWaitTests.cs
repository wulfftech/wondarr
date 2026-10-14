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
        host.SlotWait.WaitForSlot = true;
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
        progress.Should().Equal("Waiting for a free download slot");
        host.Provider.Grabs.Should().BeEmpty();

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        result.QueueItemId.Should().NotBeNull("the grab happened once a slot freed");
        host.Provider.Grabs.Should().ContainSingle();
        host.Provider.Requests.Should().ContainSingle("the candidates the run accepted are what is grabbed; there is no second search");
    }

    [Fact]
    public async Task A_song_a_reference_scan_linked_during_the_wait_is_not_grabbed_when_a_slot_frees()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var progress = 0;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            Interlocked.Increment(ref progress);

            return Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() => Volatile.Read(ref progress) > 0);
        search.IsCompleted.Should().BeFalse("every download slot is taken");

        // While the search waits, a reference scan finds the user's own file for the song.
        await host.SeedReferenceFileAsync(songId, ReferenceFileState.Identified, withSongFile: true);

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.QueueItemId.Should().BeNull();
        host.Provider.Grabs.Should().BeEmpty("the song is the user's now, so nothing is grabbed for it");

        await using var context = host.Database.CreateContext(host.Time);
        (await context.QueueItems.AsNoTracking().AnyAsync(item => item.SongId == songId, Token)).Should().BeFalse();
    }

    [Fact]
    public async Task A_search_that_never_gets_a_slot_ends_after_the_wait_and_the_song_stays_wanted()
    {
        await using var host = await SearchTestHost.CreateAsync(options => options.SlotWaitMinutes = 2);
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        await FillSlotsAsync(host);

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.NoFreeSlot, "the run is not a failed search, and the backoff ignores it");
        result.SlotWaitExpired.Should().BeTrue();
        result.Message.Should().Be("No free download slot after 2 min; the song stays wanted for the next Missing Search");
        result.QueueItemId.Should().BeNull();
        host.Provider.Grabs.Should().BeEmpty();

        await using var context = host.Database.CreateContext(host.Time);
        (await context.QueueItems.AsNoTracking().AnyAsync(item => item.SongId == songId, Token))
            .Should().BeFalse("nothing was queued for the song, so Missing Search still finds it");
    }

    [Fact]
    public async Task The_command_waits_two_hours_by_default_then_completes_instead_of_cancelling()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        await FillSlotsAsync(host);
        var progress = new List<string>();
        var command = new SongSearchCommandHandler(
            host.Search,
            host.SlotWait,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SongSearchCommandHandler>.Instance);
        var context = new Wondarr.Core.Jobs.CommandContext(
            1,
            $"{{\"name\":\"SongSearch\",\"songId\":{songId}}}",
            Wondarr.Core.Jobs.CommandTrigger.Manual,
            message =>
            {
                lock (progress)
                {
                    progress.Add(message);
                }

                return Task.CompletedTask;
            });

        var start = host.Time.GetUtcNow();
        var run = Task.Run(() => command.ExecuteAsync(context, Token));

        await WaitUntilAsync(() =>
        {
            lock (progress)
            {
                return progress.Count > 0;
            }
        });

        // An hour and fifty-nine minutes in, the command is still waiting.
        await AdvanceByAsync(host, run, TimeSpan.FromMinutes(119));
        run.IsCompleted.Should().BeFalse("the wait is two hours");

        await AdvanceByAsync(host, run, TimeSpan.FromMinutes(5));
        run.IsCompleted.Should().BeTrue("two hours have passed");

        var text = await run;

        (host.Time.GetUtcNow() - start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(2));
        text.Should().Be($"Song {songId}: Completed — No free download slot after 2 h; the song stays wanted for the next Missing Search");
        text.Should().NotContain("Cancelled");

        lock (progress)
        {
            progress.Should().Equal(
                "Waiting for a free download slot",
                "No free download slot after 2 h; the song stays wanted for the next Missing Search");
        }

        await using var database = host.Database.CreateContext(host.Time);
        var searchRun = await database.SearchRuns.AsNoTracking().SingleAsync(row => row.SongId == songId, Token);

        searchRun.Outcome.Should().Be(SearchOutcome.NoFreeSlot, "the run is not a failed search, so the backoff ignores it");
        searchRun.Message.Should().Be("No free download slot after 2 h; the song stays wanted for the next Missing Search");
    }

    [Fact]
    public async Task A_song_that_meets_its_cutoff_during_the_wait_is_not_grabbed_when_a_slot_frees()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var progress = 0;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            Interlocked.Increment(ref progress);

            return Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() => Volatile.Read(ref progress) > 0);
        search.IsCompleted.Should().BeFalse("every download slot is taken");

        // While it waits, another download imports an MP3-320, the default profile's cutoff.
        await host.SeedFileAsync(songId, qualityId: 29);

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Cancelled);
        result.SlotWaitExpired.Should().BeFalse();
        result.Message.Should().StartWith("Already at the cutoff");
        result.QueueItemId.Should().BeNull();
        host.Provider.Grabs.Should().BeEmpty("the song does not need a file any more");
    }

    [Fact]
    public async Task A_song_below_its_cutoff_that_got_a_file_during_the_wait_is_still_upgraded()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var progress = 0;
        host.SlotWait.ReportProgressAsync = _ =>
        {
            Interlocked.Increment(ref progress);

            return Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() => Volatile.Read(ref progress) > 0);

        // MP3-256 is below the cutoff, so the wanted upgrade is still wanted.
        await host.SeedFileAsync(songId, qualityId: 23);

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task The_executor_place_is_handed_back_while_the_search_waits_and_only_taken_back_for_the_grab()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var taken = 0;
        var returned = 0;
        host.SlotWait.YieldWorker = () =>
        {
            Interlocked.Increment(ref taken);

            return new CountingLease(() => Interlocked.Increment(ref returned));
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        await WaitUntilAsync(() => Volatile.Read(ref taken) > 0);
        Volatile.Read(ref returned).Should().Be(0, "the place stays handed back for as long as the search waits");

        await AdvanceByAsync(host, search, TimeSpan.FromMinutes(10));
        Volatile.Read(ref taken).Should().Be(1, "one hand-back covers the whole wait");
        Volatile.Read(ref returned).Should().Be(0);

        await FinishItemAsync(host, blockers[0]);
        (await AdvanceUntilAsync(host, search)).Outcome.Should().Be(SearchOutcome.Grabbed);
        Volatile.Read(ref returned).Should().Be(1, "the place is taken back before the grab");
    }

    [Fact]
    public async Task Cancelling_a_search_that_is_waiting_ends_the_run_cancelled()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
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
        host.SlotWait.WaitForSlot = true;
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
        host.SlotWait.WaitForSlot = true;
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
    public async Task A_search_outside_the_command_fails_fast_when_every_slot_is_busy()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        await FillSlotsAsync(host);

        var result = await host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

        result.Outcome.Should().Be(SearchOutcome.NoFreeSlot);
        result.Message.Should().Be("No free download slot");
        host.Provider.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_place_is_taken_back_before_the_grab()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseTaken = 0;
        var leaseDisposing = 0;
        host.SlotWait.YieldWorker = () =>
        {
            Interlocked.Increment(ref leaseTaken);

            return new BlockingLease(release.Task, () => Interlocked.Increment(ref leaseDisposing));
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));

        // Free a slot only once the search has handed its place back; earlier, it would grab without waiting.
        await WaitUntilAsync(() => Volatile.Read(ref leaseTaken) > 0);
        await FinishItemAsync(host, blockers[0]);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (Volatile.Read(ref leaseDisposing) == 0 && DateTime.UtcNow < deadline)
        {
            host.Time.Advance(TimeSpan.FromSeconds(host.Options.SlotWaitSeconds));
            await Task.Delay(10);
        }

        Volatile.Read(ref leaseDisposing).Should().BePositive("the search should take its place back once a slot frees");

        // No sleep: the lease counts the disposal and then blocks on `release`, so once the count is
        // up the search is parked in that dispose. The grab comes after the dispose (and before it, in
        // a regression that grabs first), so a grab that did not wait for the place would already
        // be visible here.
        search.IsCompleted.Should().BeFalse("the search is still waiting for a place");
        host.Provider.Grabs.Should().BeEmpty("nothing is grabbed until the place is back");

        release.SetResult();
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_song_whose_search_is_waiting_is_not_searched_a_second_time()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
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
            var second = await scope.ServiceProvider.GetRequiredService<ISongSearchService>()
                .SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token);

            second.Message.Should().Be("Already waiting for a download slot");
        }

        host.Provider.Requests.Should().ContainSingle();

        await FinishItemAsync(host, blockers[0]);
        (await AdvanceUntilAsync(host, search)).Outcome.Should().Be(SearchOutcome.Grabbed);

        using var after = host.CreateScope();
        after.ServiceProvider.GetRequiredService<SlotWaiters>().IsWaiting(songId).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_progress_report_does_not_lose_the_candidates_and_the_end_is_reported()
    {
        await using var host = await SearchTestHost.CreateAsync();
        host.SlotWait.WaitForSlot = true;
        var songId = await host.SeedSongAsync("Alpha");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var messages = new List<string>();
        host.SlotWait.ReportProgressAsync = message =>
        {
            lock (messages)
            {
                messages.Add(message);
            }

            return messages.Count == 1 ? throw new InvalidOperationException("database is locked") : Task.CompletedTask;
        };

        var search = Task.Run(() => host.Search.SearchAsync(songId, SearchTrigger.Automatic, grab: true, Token));
        await WaitUntilAsync(() =>
        {
            lock (messages)
            {
                return messages.Count > 0;
            }
        });

        await FinishItemAsync(host, blockers[0]);
        var result = await AdvanceUntilAsync(host, search);

        result.Outcome.Should().Be(SearchOutcome.Grabbed);
        messages.Last().Should().StartWith("Grabbed after waiting");
    }

    [Fact]
    public void The_wait_is_validated()
    {
        var validator = new SearchOptionsValidator();

        validator.Validate(null, new SearchOptions { SlotWaitMinutes = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new SearchOptions { SlotWaitMinutes = 1441 }).Failed.Should().BeTrue();
        validator.Validate(null, new SearchOptions()).Succeeded.Should().BeTrue();
        new SearchOptions().SlotWaitMinutes.Should().Be(120, "a search waits two hours for a download slot");
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

    /// <summary>
    /// Moves the fake clock on by <paramref name="span"/> a minute at a time (or until the task ends), giving
    /// the waiting search a moment to look at the clock after each step.
    /// </summary>
    private static async Task AdvanceByAsync(SearchTestHost host, Task task, TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span && !task.IsCompleted; elapsed += TimeSpan.FromMinutes(1))
        {
            host.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20);
        }
    }

    private sealed class CountingLease(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();

            return ValueTask.CompletedTask;
        }
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

    private sealed class BlockingLease(Task gate, Action onDispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            onDispose();
            await gate;
        }
    }
}
