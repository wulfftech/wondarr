using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// Soulseek allows 30 searches per 4 minutes and two in flight; these tests pin the budget that
/// keeps Wondarr inside that, on a fake clock so the waits are exact rather than approximate.
/// </summary>
public class SoulseekSearchBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(240);

    [Fact]
    public async Task Submits_thirty_searches_at_five_second_spacing()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time);
        var submissions = new List<DateTimeOffset>();

        for (var i = 0; i < 30; i++)
        {
            await using var lease = await AcquireAsync(time, budget, i == 0 ? TimeSpan.Zero : Spacing);
            submissions.Add(time.GetUtcNow());
        }

        submissions.Should().HaveCount(30);
        submissions[^1].Should().Be(Start + TimeSpan.FromSeconds(145));
        submissions.Zip(submissions.Skip(1), (earlier, later) => later - earlier)
            .Should().OnlyContain(gap => gap == Spacing);
        budget.Snapshot().SubmittedInWindow.Should().Be(30);
        budget.Snapshot().Outstanding.Should().Be(0);
    }

    [Fact]
    public async Task The_thirty_first_waits_until_the_first_leaves_the_window()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time);

        for (var i = 0; i < 30; i++)
        {
            await using var lease = await AcquireAsync(time, budget, i == 0 ? TimeSpan.Zero : Spacing);
        }

        // The 31st is only 145 s after the 1st, so it waits for the first to fall out at t+240 s.
        var acquisition = budget.AcquireAsync(CancellationToken.None);
        acquisition.IsCompleted.Should().BeFalse();

        time.Advance(Window - TimeSpan.FromSeconds(146));
        acquisition.IsCompleted.Should().BeFalse();

        // A submission exactly one window old still counts, so reaching t+240 s is not enough.
        time.Advance(TimeSpan.FromSeconds(1));
        acquisition.IsCompleted.Should().BeFalse();

        // (FakeTimeProvider's timestamps are coarser than a tick, so step by a millisecond.)
        time.Advance(TimeSpan.FromMilliseconds(1));
        await using var last = await acquisition;

        time.GetUtcNow().Should().Be(Start + Window + TimeSpan.FromMilliseconds(1));
        budget.Snapshot().SubmittedInWindow.Should().Be(30);
    }

    [Fact]
    public async Task A_third_search_waits_for_a_lease_to_be_disposed()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time);

        var first = await AcquireAsync(time, budget, TimeSpan.Zero);
        var second = await AcquireAsync(time, budget, Spacing);

        var third = budget.AcquireAsync(CancellationToken.None);
        third.IsCompleted.Should().BeFalse();
        budget.Snapshot().Outstanding.Should().Be(2);

        // No amount of waiting frees an outstanding slot; only a lease does.
        time.Advance(TimeSpan.FromHours(1));
        third.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        await using var thirdLease = await third;

        budget.Snapshot().Outstanding.Should().Be(2);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Serves_waiters_in_the_order_they_arrived()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MaxOutstanding = 1, MinSpacingSeconds = 0 });

        var first = await AcquireAsync(time, budget, TimeSpan.Zero);

        var second = budget.AcquireAsync(CancellationToken.None);
        var third = budget.AcquireAsync(CancellationToken.None);
        second.IsCompleted.Should().BeFalse();
        third.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();

        await using var secondLease = await second;
        third.IsCompleted.Should().BeFalse("the third waiter must not overtake the second");

        await secondLease.DisposeAsync();
        await using var thirdLease = await third;
    }

    [Fact]
    public async Task Cancelling_a_wait_on_the_window_records_nothing()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MaxSearches = 1, MinSpacingSeconds = 0 });

        await using var lease = await AcquireAsync(time, budget, TimeSpan.Zero);

        using var cancellation = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(cancellation.Token);
        waiting.IsCompleted.Should().BeFalse();

        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        budget.Snapshot().SubmittedInWindow.Should().Be(1);
        budget.Snapshot().Outstanding.Should().Be(1);
    }

    [Fact]
    public async Task Cancelling_a_wait_for_a_slot_records_nothing()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MaxOutstanding = 1, MinSpacingSeconds = 0 });

        await using var lease = await AcquireAsync(time, budget, TimeSpan.Zero);

        using var cancellation = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(cancellation.Token);
        waiting.IsCompleted.Should().BeFalse();

        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        budget.Snapshot().SubmittedInWindow.Should().Be(1);
        budget.Snapshot().Outstanding.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_one_slot()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MinSpacingSeconds = 0 });

        var first = await AcquireAsync(time, budget, TimeSpan.Zero);
        var second = await AcquireAsync(time, budget, TimeSpan.Zero);

        await first.DisposeAsync();
        await first.DisposeAsync();

        budget.Snapshot().Outstanding.Should().Be(1);

        // One slot was freed, so a third search goes and a fourth has to wait for it.
        await using var third = await AcquireAsync(time, budget, TimeSpan.Zero);

        var fourth = budget.AcquireAsync(CancellationToken.None);
        fourth.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(5));
        fourth.IsCompleted.Should().BeFalse();

        await second.DisposeAsync();
        await using var fourthLease = await fourth;
    }

    /// <summary>
    /// The property that matters: however the caller's own timing varies, no 240 s window ever holds
    /// more than 30 submissions and no two submissions are closer than 5 s.
    /// </summary>
    [Fact]
    public async Task A_hundred_acquisitions_never_break_the_window_or_the_spacing()
    {
        var time = new FakeTimeProvider(Start);
        var search = new SoulseekSearchOptions();
        var budget = Budget(time, search);
        var random = new Random(20260929);

        var submissions = new List<DateTimeOffset>();
        var leases = new List<IAsyncDisposable>();

        for (var i = 0; i < 100; i++)
        {
            // Leases are held and disposed at random moments, but never more than the budget allows
            // at once: the property under test is the window and the spacing, not the slot count.
            while (leases.Count >= search.MaxOutstanding)
            {
                var index = random.Next(leases.Count);
                await leases[index].DisposeAsync();
                leases.RemoveAt(index);
            }

            var now = time.GetUtcNow();
            var earliest = EarliestAllowed(submissions, now, search);
            var acquisition = budget.AcquireAsync(CancellationToken.None);

            if (earliest > now)
            {
                acquisition.IsCompleted.Should().BeFalse();

                if (earliest - now > TimeSpan.FromMilliseconds(1))
                {
                    time.Advance(earliest - now - TimeSpan.FromMilliseconds(1));
                    acquisition.IsCompleted.Should().BeFalse();
                }

                time.Advance(earliest - time.GetUtcNow());
            }

            leases.Add(await acquisition);
            submissions.Add(time.GetUtcNow());
        }

        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }

        submissions.Should().HaveCount(100);
        submissions.Should().BeInAscendingOrder();

        for (var i = 1; i < submissions.Count; i++)
        {
            (submissions[i] - submissions[i - 1])
                .Should().BeGreaterThanOrEqualTo(Spacing, "submissions are never closer than five seconds");
        }

        for (var i = 0; i + search.MaxSearches < submissions.Count; i++)
        {
            (submissions[i + search.MaxSearches] - submissions[i])
                .Should().BeGreaterThan(Window, "no closed four-minute interval holds more than thirty submissions");
        }
    }

    [Fact]
    public async Task A_cancelled_slot_waiter_does_not_swallow_the_wakeup()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MaxOutstanding = 1, MinSpacingSeconds = 5 });

        var first = await AcquireAsync(time, budget, TimeSpan.Zero);
        time.Advance(Spacing);

        using var cancel = new CancellationTokenSource();
        var cancelled = budget.AcquireAsync(cancel.Token);
        var waiting = budget.AcquireAsync(CancellationToken.None);

        await cancel.CancelAsync();
        var cancelling = () => cancelled;
        await cancelling.Should().ThrowAsync<OperationCanceledException>();
        waiting.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        await using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        budget.Snapshot().Outstanding.Should().Be(1);
    }

    [Fact]
    public async Task Spacing_waiters_are_served_in_arrival_order_and_newcomers_queue_behind_them()
    {
        var time = new FakeTimeProvider(Start);
        var budget = Budget(time, new SoulseekSearchOptions { MaxOutstanding = 2 });
        var order = new List<string>();

        await using var first = await AcquireAsync(time, budget, TimeSpan.Zero);
        var second = Track(budget.AcquireAsync(CancellationToken.None), "second", order);
        var third = Track(budget.AcquireAsync(CancellationToken.None), "third", order);

        time.Advance(Spacing);
        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        third.IsCompleted.Should().BeFalse("the third needs another five seconds and a slot");

        // A newcomer arriving now must not overtake the third.
        var newcomer = Track(budget.AcquireAsync(CancellationToken.None), "newcomer", order);
        await first.DisposeAsync();
        time.Advance(Spacing);
        await using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5));
        newcomer.IsCompleted.Should().BeFalse();

        await secondLease.DisposeAsync();
        time.Advance(Spacing);
        await using var newcomerLease = await newcomer.WaitAsync(TimeSpan.FromSeconds(5));

        order.Should().Equal("second", "third", "newcomer");
    }

    private static async Task<IAsyncDisposable> Track(Task<IAsyncDisposable> acquisition, string name, List<string> order)
    {
        var lease = await acquisition.ConfigureAwait(false);
        lock (order)
        {
            order.Add(name);
        }

        return lease;
    }

    /// <summary>When the budget says the next submission may go, worked out from the same rules.</summary>
    private static DateTimeOffset EarliestAllowed(
        List<DateTimeOffset> submissions,
        DateTimeOffset now,
        SoulseekSearchOptions search)
    {
        var earliest = now;

        if (submissions.Count > 0)
        {
            var afterSpacing = submissions[^1] + TimeSpan.FromSeconds(search.MinSpacingSeconds);
            if (afterSpacing > earliest)
            {
                earliest = afterSpacing;
            }
        }

        var inWindow = submissions
            .Where(submission => submission >= now - TimeSpan.FromSeconds(search.WindowSeconds))
            .ToList();

        if (inWindow.Count >= search.MaxSearches)
        {
            var windowOpens = inWindow[inWindow.Count - search.MaxSearches]
                + TimeSpan.FromSeconds(search.WindowSeconds)
                + TimeSpan.FromMilliseconds(1);

            if (windowOpens > earliest)
            {
                earliest = windowOpens;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Acquires, checks the budget does not let the submission go early, advances the clock by the
    /// wait it asked for and hands the lease back.
    /// </summary>
    private static async Task<IAsyncDisposable> AcquireAsync(
        FakeTimeProvider time,
        SoulseekSearchBudget budget,
        TimeSpan expectedWait)
    {
        var acquisition = budget.AcquireAsync(CancellationToken.None);

        if (expectedWait > TimeSpan.Zero)
        {
            acquisition.IsCompleted.Should().BeFalse();
            time.Advance(expectedWait - TimeSpan.FromMilliseconds(1));
            acquisition.IsCompleted.Should().BeFalse("the budget must not submit before the wait it computed");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        return await acquisition;
    }

    private static SoulseekSearchBudget Budget(TimeProvider time, SoulseekSearchOptions? search = null) =>
        new(
            SlskdTestData.Monitor(new SoulseekOptions { Search = search ?? new SoulseekSearchOptions() }),
            time,
            NullLogger<SoulseekSearchBudget>.Instance);
}
