using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Core;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The runner's contract is as much about what it leaves behind as about what it returns: whatever
/// happens, the search is deleted from slskd and the budget's slot is handed back.
/// </summary>
public class SlskdSearchRunnerTests
{
    private const string SearchText = "daft punk get lucky";

    private static readonly DateTimeOffset Start = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Reads_the_responses_once_and_deletes_the_search()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Complete());
        api.GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([Response()]);
        RecordsDelete(api, budget);

        var result = await RunAsync(time, Runner(provider, time, budget), budget);

        result.SearchText.Should().Be(SearchText);
        result.FinalState.Should().Be("Completed, ResponseLimitReached");
        result.StoppedByWallClock.Should().BeFalse();
        result.Responses.Should().ContainSingle();
        result.Elapsed.Should().Be(Poll);

        await api.Received(1).GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await api.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await api.DidNotReceive().StopAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // The search is gone before the slot is handed back.
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task Stops_a_search_the_wall_clock_ran_out_on_and_still_deletes_it()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        var stopped = false;

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => stopped ? Complete() : InProgress());
        api.StopAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stopped = true;
                return Task.CompletedTask;
            });
        api.GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([Response()]);
        RecordsDelete(api, budget);

        var result = await RunAsync(time, Runner(provider, time, budget), budget);

        result.StoppedByWallClock.Should().BeTrue();
        result.FinalState.Should().Be("Completed, ResponseLimitReached");
        result.Responses.Should().ContainSingle();

        // The wall clock, not the network, ended this one.
        result.Elapsed.Should().Be(TimeSpan.FromSeconds(30));

        await api.Received(1).StopAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await api.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task Deletes_the_search_when_the_caller_cancels_mid_poll()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);
        using var cancellation = new CancellationTokenSource();

        var callerWasCancelled = false;
        var deleteTokenWasCancelled = true;

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                deleteTokenWasCancelled = call.Arg<CancellationToken>().IsCancellationRequested;
                callerWasCancelled = cancellation.IsCancellationRequested;
                budget.Deleted();
                return Task.CompletedTask;
            });

        var run = Runner(provider, time, budget).RunAsync(SearchText, cancellation.Token);

        time.Advance(Poll);
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();

        callerWasCancelled.Should().BeTrue();
        deleteTokenWasCancelled.Should().BeFalse("the delete must not inherit the caller's cancelled token");
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task Swallows_a_failed_delete()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Complete());
        api.GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([Response()]);
        api.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                budget.Deleted();
                return Task.FromException(new HttpRequestException("slskd is gone"));
            });

        var result = await RunAsync(time, Runner(provider, time, budget), budget);

        result.FinalState.Should().Be("Completed, ResponseLimitReached");
        await api.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task Deletes_nothing_and_releases_the_slot_when_slskd_rejects_the_search()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Rejected());

        var run = Runner(provider, time, budget).RunAsync(SearchText, CancellationToken.None);

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<SlskdSearchRejectedException>();

        await api.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "release");
    }

    [Fact]
    public async Task Reports_a_search_that_vanished_mid_poll_as_missing()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((SlskdSearch?)null);
        RecordsDelete(api, budget);

        var result = await RunAsync(time, Runner(provider, time, budget), budget);

        result.FinalState.Should().Be("Missing");
        result.Responses.Should().BeEmpty();
        result.StoppedByWallClock.Should().BeFalse();
        await api.DidNotReceive().GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await api.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    /// <summary>The exact delegate type, so the setup is not ambiguous between Task and its result.</summary>
    private static Func<CallInfo, Task<SlskdSearch>> Rejected() =>
        _ => Task.FromException<SlskdSearch>(new SlskdSearchRejectedException());

    /// <summary>Runs to completion by stepping the fake clock; the runner waits on nothing else.</summary>
    private static async Task<SlskdSearchResult> RunAsync(
        FakeTimeProvider time,
        SlskdSearchRunner runner,
        RecordingBudget budget)
    {
        var run = runner.RunAsync(SearchText, CancellationToken.None);

        for (var step = 0; step < 200 && !run.IsCompleted; step++)
        {
            time.Advance(Poll);

            // The runner's continuation runs on the thread pool; give it its turn before deciding
            // how far the clock goes next.
            for (var spin = 0; spin < 20 && !run.IsCompleted; spin++)
            {
                await Task.Yield();
            }

            if (!run.IsCompleted)
            {
                await Task.Delay(1);
            }
        }

        run.IsCompleted.Should().BeTrue("the run must finish without waiting on real time");
        return await run;
    }

    [Fact]
    public async Task Deletes_the_search_when_the_start_fails_after_the_request_may_have_reached_slskd()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        Guid started = default;
        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<SlskdSearch>>(call =>
            {
                started = call.Arg<SlskdSearchRequest>().Id;
                throw new HttpRequestException("500 after the POST was received");
            });
        RecordsDelete(api, budget);

        var run = () => Runner(provider, time, budget).RunAsync(SearchText, CancellationToken.None);

        await run.Should().ThrowAsync<HttpRequestException>();
        await api.Received(1).DeleteAsync(started, Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task Deletes_the_search_and_then_releases_the_slot_when_a_poll_fails()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<SlskdSearch?>>(_ => throw new HttpRequestException("connection reset"));
        RecordsDelete(api, budget);

        var run = () => RunAsync(time, Runner(provider, time, budget), budget);

        await run.Should().ThrowAsync<HttpRequestException>();
        await api.Received(1).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    [Fact]
    public async Task The_wall_clock_also_bounds_a_poll_that_never_answers()
    {
        var time = new FakeTimeProvider(Start);
        var api = Substitute.For<ISlskdSearchApi>();
        var budget = new RecordingBudget();
        using var provider = Provider(api);

        api.StartAsync(Arg.Any<SlskdSearchRequest>(), Arg.Any<CancellationToken>()).Returns(InProgress());

        // The first poll hangs until its token is cancelled; after the stop, slskd answers again.
        var stopped = false;
        api.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => stopped
                ? Task.FromResult<SlskdSearch?>(Complete())
                : Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith<SlskdSearch?>(
                    task => throw new TaskCanceledException(task),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default));
        api.StopAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stopped = true;
                return Task.CompletedTask;
            });
        api.GetResponsesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([Response()]);
        RecordsDelete(api, budget);

        var result = await RunAsync(time, Runner(provider, time, budget), budget);

        result.StoppedByWallClock.Should().BeTrue();
        result.Responses.Should().ContainSingle();
        budget.Events.Should().Equal("acquire", "delete", "release");
    }

    /// <summary>Records the delete in the budget's event log, so the order of the two is visible.</summary>
    private static void RecordsDelete(ISlskdSearchApi api, RecordingBudget budget) =>
        api.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                budget.Deleted();
                return Task.CompletedTask;
            });

    private static ServiceProvider Provider(ISlskdSearchApi api)
    {
        var services = new ServiceCollection();
        services.AddSingleton(api);
        return services.BuildServiceProvider();
    }

    private static SlskdSearchRunner Runner(
        IServiceProvider provider,
        TimeProvider time,
        ISoulseekSearchBudget budget) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            budget,
            SlskdTestData.Monitor(new SoulseekOptions()),
            time,
            NullLogger<SlskdSearchRunner>.Instance);

    private static SlskdSearch InProgress() => new()
    {
        Id = Guid.NewGuid(),
        SearchText = SearchText,
        State = "InProgress",
        IsComplete = false,
    };

    private static SlskdSearch Complete() => new()
    {
        Id = Guid.NewGuid(),
        SearchText = SearchText,
        State = "Completed, ResponseLimitReached",
        IsComplete = true,
        ResponseCount = 1,
        FileCount = 1,
        EndedAt = Start.UtcDateTime.AddSeconds(3),
    };

    private static SlskdSearchResponse Response() => new()
    {
        Username = "peer005",
        HasFreeUploadSlot = true,
        UploadSpeed = 1146398,
        QueueLength = 17,
        FileCount = 1,
        Files = [new SlskdSearchFile { Filename = "@@qkrmw\\Music\\Get Lucky.flac", Size = 30038322 }],
    };

    /// <summary>A budget that records what the runner did with the slot it handed out.</summary>
    private sealed class RecordingBudget : ISoulseekSearchBudget
    {
        public List<string> Events { get; } = [];

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            Events.Add("acquire");
            return Task.FromResult<IAsyncDisposable>(new Lease(this));
        }

        public SoulseekSearchBudgetSnapshot Snapshot() => new(0, 0, null);

        public void Deleted() => Events.Add("delete");

        private sealed class Lease : IAsyncDisposable
        {
            private readonly RecordingBudget _budget;

            public Lease(RecordingBudget budget) => _budget = budget;

            public ValueTask DisposeAsync()
            {
                _budget.Events.Add("release");
                return ValueTask.CompletedTask;
            }
        }
    }
}