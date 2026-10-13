using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// The scheduled loops: the backoff that reaches its weekly step (R-032), pushes that do not count
/// towards it (owner question 21), a run that names its songs (R-034), and loops that hand their
/// executor place back while they wait for a download slot (R-067, owner question 22).
/// </summary>
public sealed class SearchLoopTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task A_hopeless_song_settles_at_the_weekly_step_with_runs_spaced_as_the_schedule_spaces_them()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        // The defaults are [1, 6, 24, 72, 168]: fruitless runs land at 0, 6, 12, 36 and 108 hours. The
        // fifth failure sets the weekly wait, so the next search is due at 108 + 168 = 276 hours.
        var start = host.Time.GetUtcNow().UtcDateTime;

        foreach (var hours in new[] { 0, 6, 12, 36, 108 })
        {
            await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, start.AddHours(hours));
        }

        // 180 hours in: a window of 168 hours would have seen only three failures and searched again.
        host.Time.Advance(TimeSpan.FromHours(180));
        await Handler(host).ExecuteAsync(Context(), Token);
        host.Provider.Requests.Should().BeEmpty("the song has failed five times in a row and waits a week");

        host.Time.Advance(TimeSpan.FromHours(95));
        await Handler(host).ExecuteAsync(Context(), Token);
        host.Provider.Requests.Should().BeEmpty("275 hours is still short of 108 + 168");

        host.Time.Advance(TimeSpan.FromHours(2));
        await Handler(host).ExecuteAsync(Context(), Token);
        host.Provider.Requests.Should().ContainSingle("the weekly step has passed");
    }

    [Fact]
    public async Task A_hopeless_song_stays_weekly_when_housekeeping_has_pruned_the_old_runs()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var now = host.Time.GetUtcNow().UtcDateTime;

        // Runs older than 30 days are gone: what is left is one run a week for the last 26 days. Counted
        // as they stand that is four failures (the 72-hour step), which would search the song every three
        // days; but nothing in what is kept is a success and the oldest kept failure is over three weeks
        // old, so the song is at the weekly step.
        foreach (var days in new[] { 26, 19, 12, 5 })
        {
            await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddDays(-days));
        }

        await Handler(host).ExecuteAsync(Context(), Token);
        host.Provider.Requests.Should().BeEmpty("five days is short of the weekly step");

        host.Time.Advance(TimeSpan.FromDays(2.5));
        await Handler(host).ExecuteAsync(Context(), Token);
        host.Provider.Requests.Should().ContainSingle("seven and a half days have passed");
    }

    [Fact]
    public async Task A_young_hopeless_song_still_follows_the_steps_it_has_earned()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var now = host.Time.GetUtcNow().UtcDateTime;

        // Two failures in the last two days: the six-hour step, long past.
        await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddDays(-2));
        await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddDays(-1));

        await Handler(host).ExecuteAsync(Context(), Token);

        host.Provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Failures_older_than_the_longest_step_still_count_until_a_grab_resets_them()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var now = host.Time.GetUtcNow().UtcDateTime;

        // An old success, then five failures that began a month ago: all five count, none of the older ones.
        await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddDays(-60));
        await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.Grabbed, now.AddDays(-50));

        foreach (var days in new[] { 30, 29, 28, 26, 22 })
        {
            await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddDays(-days));
        }

        host.Time.Advance(TimeSpan.FromDays(1));
        await Handler(host).ExecuteAsync(Context(), Token);

        // Last failure 23 days ago: past the weekly step either way, so the song is due.
        host.Provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejected_release_pushes_do_not_delay_a_songs_own_search()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var now = host.Time.GetUtcNow().UtcDateTime;

        // One fruitless search of its own ten hours ago, then four rejected pushes: counted, they would
        // make five failures in a row and park the song for a week.
        await SeedRunAsync(host, songId, SearchTrigger.Automatic, SearchOutcome.NoResults, now.AddHours(-10));

        foreach (var hours in new[] { 4, 3, 2, 1 })
        {
            await SeedRunAsync(host, songId, SearchTrigger.Push, SearchOutcome.NoAcceptableCandidate, now.AddHours(-hours));
        }

        await Handler(host).ExecuteAsync(Context(), Token);

        host.Provider.Requests.Should().ContainSingle("the pushes say nothing about when the song's own search should run");
    }

    [Fact]
    public async Task A_run_that_names_its_songs_searches_those_and_no_others()
    {
        await using var host = await SearchTestHost.CreateAsync();
        await host.SeedSongAsync("Alpha");
        var beta = await host.SeedSongAsync("Beta");
        await host.SeedSongAsync("Gamma");
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));

        var context = new CommandContext(
            1,
            $"{{\"name\":\"MissingSearch\",\"songIds\":[{beta}]}}",
            CommandTrigger.Unspecified,
            _ => Task.CompletedTask);

        await Handler(host).ExecuteAsync(context, Token);

        host.Provider.Requests.Select(request => request.Title).Should().Equal("Beta");
    }

    [Fact]
    public async Task A_run_that_names_its_songs_does_not_search_one_that_has_no_need()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var downloading = await host.SeedSongAsync("Alpha");
        var wanted = await host.SeedSongAsync("Beta");
        await SeedActiveQueueItemsAsync(host, downloading, 1);

        var context = new CommandContext(
            1,
            $"{{\"songIds\":[{downloading},{wanted}]}}",
            CommandTrigger.Unspecified,
            _ => Task.CompletedTask);

        await Handler(host).ExecuteAsync(context, Token);

        host.Provider.Requests.Select(request => request.Title).Should().Equal("Beta");
    }

    [Fact]
    public async Task Missing_search_hands_its_place_back_while_it_waits_for_a_slot_and_takes_it_before_it_searches()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var lease = new TestLease();

        var running = Handler(host).ExecuteAsync(ContextWith(lease), Token);

        await lease.Yielded.WaitAsync(TimeSpan.FromSeconds(10));
        host.Provider.Requests.Should().BeEmpty("every slot is taken");

        await FinishItemAsync(host, blockers[0]);
        await AdvanceUntilAsync(host.Time, TimeSpan.FromSeconds(host.Options.SlotWaitSeconds), lease.Disposing);

        // The place is being waited for: the song must not be searched until it is back.
        host.Provider.Requests.Should().BeEmpty();
        running.IsCompleted.Should().BeFalse();

        lease.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        host.Provider.Requests.Should().ContainSingle();
        _ = songId;
    }

    [Fact]
    public async Task Upgrade_search_hands_its_place_back_while_it_waits_for_a_slot_and_takes_it_before_it_searches()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var songId = await host.SeedSongAsync();
        await host.SeedFileAsync(songId, qualityId: 17);
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var lease = new TestLease();

        var handler = new UpgradeSearchCommandHandler(
            host.Scopes,
            host.Monitor,
            host.Time,
            NullLogger<UpgradeSearchCommandHandler>.Instance);
        var running = handler.ExecuteAsync(ContextWith(lease), Token);

        await lease.Yielded.WaitAsync(TimeSpan.FromSeconds(10));
        host.Provider.Requests.Should().BeEmpty("every slot is taken");

        await FinishItemAsync(host, blockers[0]);
        await AdvanceUntilAsync(host.Time, TimeSpan.FromSeconds(host.Options.SlotWaitSeconds), lease.Disposing);

        host.Provider.Requests.Should().BeEmpty();
        running.IsCompleted.Should().BeFalse();

        lease.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        host.Provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_loop_whose_slot_is_taken_while_it_waits_for_its_place_yields_again()
    {
        await using var host = await SearchTestHost.CreateAsync();
        await host.SeedSongAsync();
        host.Provider.Candidates.Add(SearchTestHost.Candidate("Music\\Aphex Twin\\Alpha.flac"));
        var blockers = await FillSlotsAsync(host);
        var lease = new TestLease();
        var yields = 0;
        var yieldedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var context = new CommandContext(
            1,
            null,
            CommandTrigger.Scheduled,
            _ => Task.CompletedTask,
            () =>
            {
                if (Interlocked.Increment(ref yields) == 2)
                {
                    yieldedAgain.TrySetResult();
                }

                return lease.Take();
            });

        var running = Handler(host).ExecuteAsync(context, Token);
        await lease.Yielded.WaitAsync(TimeSpan.FromSeconds(10));

        // A slot frees, and while the loop waits for its place another grab takes it.
        await FinishItemAsync(host, blockers[0]);
        await AdvanceUntilAsync(host.Time, TimeSpan.FromSeconds(host.Options.SlotWaitSeconds), lease.Disposing);
        var other = await host.SeedSongAsync("Other");
        await SeedActiveQueueItemsAsync(host, other, 1);
        lease.Release();

        // The slots are full again, so it is back to waiting with a place on loan; nothing is searched.
        await AdvanceUntilAsync(host.Time, TimeSpan.FromSeconds(host.Options.SlotWaitSeconds), yieldedAgain.Task);

        host.Provider.Requests.Should().BeEmpty();
        running.IsCompleted.Should().BeFalse();

        lease.Release();
        await FinishItemAsync(host, blockers[1]);
        await AdvanceUntilAsync(host.Time, TimeSpan.FromSeconds(host.Options.SlotWaitSeconds), running);
        host.Provider.Requests.Should().ContainSingle();
    }

    private static async Task SeedRunAsync(
        SearchTestHost host,
        long songId,
        SearchTrigger trigger,
        SearchOutcome outcome,
        DateTime startedAt)
    {
        await using var context = host.Database.CreateContext(host.Time);

        context.SearchRuns.Add(new SearchRun
        {
            SongId = songId,
            Trigger = trigger,
            StartedAt = startedAt,
            FinishedAt = startedAt,
            Outcome = outcome,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Moves the fake clock on in poll-sized steps until <paramref name="signal"/> completes.</summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Task signal)
    {
        using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!signal.IsCompleted)
        {
            cap.Token.ThrowIfCancellationRequested();
            time.Advance(step);
            await Task.Yield();
        }

        await signal;
    }

    private static async Task<List<long>> FillSlotsAsync(SearchTestHost host)
    {
        var items = new List<long>();

        for (var index = 0; index < host.Options.MaxActiveDownloads; index++)
        {
            var blocker = await host.SeedSongAsync($"Blocker {index}");
            items.AddRange(await SeedActiveQueueItemsAsync(host, blocker, 1));
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

    private static async Task<List<long>> SeedActiveQueueItemsAsync(SearchTestHost host, long songId, int count)
    {
        await using var context = host.Database.CreateContext(host.Time);
        var now = host.Time.GetUtcNow().UtcDateTime;

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = now,
            Outcome = SearchOutcome.Grabbed,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var ids = new List<long>();

        for (var index = 0; index < count; index++)
        {
            var candidate = new CandidateRecord
            {
                SearchRunId = run.Id,
                SongId = songId,
                SourceType = SourceTypes.Soulseek,
                BlocklistKey = $"peer\u001fMusic\\other{songId}-{index}.flac",
                DisplayName = $"other{index}.flac",
                RemotePath = $"Music\\other{songId}-{index}.flac",
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
                State = QueueItemState.Downloading,
                Destination = $"wondarr/{candidate.Id}",
                Attempt = 1,
            };

            context.QueueItems.Add(item);
            await context.SaveChangesAsync();

            ids.Add(item.Id);
        }

        return ids;
    }

    private static MissingSearchCommandHandler Handler(SearchTestHost host) =>
        new(host.Scopes, host.Monitor, host.Time, NullLogger<MissingSearchCommandHandler>.Instance);

    private static CommandContext Context() =>
        new(1, null, CommandTrigger.Scheduled, _ => Task.CompletedTask);

    private static CommandContext ContextWith(TestLease lease) =>
        new(1, null, CommandTrigger.Scheduled, _ => Task.CompletedTask, lease.Take);

    /// <summary>
    /// An executor place on loan: says when it was handed back and when it is being taken again, and
    /// gives it back only when the test lets it.
    /// </summary>
    private sealed class TestLease
    {
        private readonly TaskCompletionSource _yielded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Yielded => _yielded.Task;

        public Task Disposing => _disposing.Task;

        public IAsyncDisposable Take()
        {
            _yielded.TrySetResult();

            return new Handle(this);
        }

        public void Release() => _release.TrySetResult();

        private sealed class Handle(TestLease owner) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                owner._disposing.TrySetResult();
                await owner._release.Task;
                owner._release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
