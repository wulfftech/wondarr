using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

public class CommandExecutorTests
{
    [Fact]
    public async Task A_handler_that_throws_ends_the_command_as_failed_with_the_exception_message()
    {
        await using var host = await JobTestHost.CreateAsync(
            services => services.AddSingleton<ICommandHandler>(new ThrowingCommandHandler("Explode")));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Explode", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Failed))
            .Should().BeTrue("the handler threw");

        var failed = await host.Queue.GetAsync(queued.Id, CancellationToken.None);
        failed!.Result.Should().Be(CommandResult.Unsuccessful);
        failed.Exception.Should().Be(ThrowingCommandHandler.FailureMessage);
        failed.EndedAt.Should().NotBeNull();

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_running_command_that_did_not_opt_in_cannot_be_cancelled()
    {
        using var handler = new BlockingCommandHandler("Plain");
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Plain", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Started)).Should().BeTrue();

        (await host.Queue.CancelAsync(queued.Id, CancellationToken.None))
            .Should().BeFalse("only a handler that opts in can be cancelled while it runs");
        handler.SawCancellation.Should().BeFalse();
        (await StatusAsync(host, queued.Id)).Should().Be(CommandStatus.Started);

        handler.Release();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Completed)).Should().BeTrue();

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_running_command_that_opted_in_is_cancelled_through_the_queue()
    {
        using var handler = new BlockingCommandHandler("Waiter") { CancelWhileRunning = true };
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Waiter", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Started)).Should().BeTrue();
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();

        (await host.Queue.CancelAsync(queued.Id, CancellationToken.None)).Should().BeTrue();

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Cancelled))
            .Should().BeTrue("a cancelled run is not an abort or a failure");
        handler.SawCancellation.Should().BeTrue();

        var row = await host.Queue.GetAsync(queued.Id, CancellationToken.None);
        row!.Result.Should().Be(CommandResult.Unsuccessful);
        row.Message.Should().Be("Cancelled");
        row.Exception.Should().BeNull();
        row.EndedAt.Should().NotBeNull();

        (await TestWait.UntilAsync(() => Task.FromResult(!host.Active.Contains(queued.Id))))
            .Should().BeTrue("the executor lets go of the command");

        // A second cancel finds nothing running.
        (await host.Queue.CancelAsync(queued.Id, CancellationToken.None)).Should().BeFalse();

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_cancel_fires_its_callbacks_after_the_queue_has_let_go_of_its_gate()
    {
        using var handler = new BlockingCommandHandler("Waiter") { CancelWhileRunning = true };
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        // A callback registered on the token runs inline on the thread that fires it. This one uses the
        // queue, which needs its gate: fired under the gate it would wait for itself forever.
        handler.OnUserCancel = () => host.Queue
            .EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var queued = await host.Queue.EnqueueAsync("Waiter", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Started)).Should().BeTrue();

        var cancel = host.Queue.CancelAsync(queued.Id, CancellationToken.None);

        (await Task.WhenAny(cancel, Task.Delay(TestWait.Timeout)))
            .Should().BeSameAs(cancel, "the token is fired after the gate is released");
        (await cancel).Should().BeTrue();

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Cancelled)).Should().BeTrue();

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_the_host_still_aborts_a_command_that_opted_in()
    {
        using var handler = new BlockingCommandHandler("Waiter") { CancelWhileRunning = true };
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Waiter", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);

        (await StatusAsync(host, queued.Id)).Should().Be(CommandStatus.Aborted);
    }

    [Fact]
    public async Task A_command_whose_handler_cannot_be_built_fails_once_and_the_next_command_still_runs()
    {
        await using var host = await JobTestHost.CreateAsync(
            services => services.AddScoped<ICommandHandler, UnbuildableCommandHandler>());
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var broken = await host.Queue.EnqueueAsync("Unbuildable", null, CommandTrigger.Manual, CancellationToken.None);
        var healthy = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, broken.Id) == CommandStatus.Failed))
            .Should().BeTrue("a handler DI cannot build fails its command instead of leaving it queued");
        (await TestWait.UntilAsync(async () => await StatusAsync(host, healthy.Id) == CommandStatus.Completed))
            .Should().BeTrue("one broken registration must not take the healthy commands down");

        var failed = await host.Queue.GetAsync(broken.Id, CancellationToken.None);
        failed!.Result.Should().Be(CommandResult.Unsuccessful);
        failed.Exception.Should().Contain("Unbuildable").And.Contain(nameof(IUnregisteredService));
        failed.Message.Should().Be(failed.Exception);
        failed.EndedAt.Should().NotBeNull();

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_handler_built_by_the_fallback_is_disposed_after_it_ran()
    {
        DisposableCommandHandler.Disposed = 0;
        await using var host = await JobTestHost.CreateAsync(services =>
        {
            services.AddScoped<ICommandHandler, UnbuildableCommandHandler>();
            services.AddScoped<ICommandHandler, DisposableCommandHandler>();
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Disposable", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Completed)).Should().BeTrue();
        (await TestWait.UntilAsync(() => Task.FromResult(DisposableCommandHandler.Disposed >= 1))).Should().BeTrue();

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_failed_status_write_does_not_stop_the_worker_or_the_host()
    {
        await using var host = await JobTestHost.CreateAsync(
            services => services.AddSingleton<ICommandHandler>(new ThrowingCommandHandler("Explode")));
        var aggregator = new ThrowOnceAggregator(host.EventAggregator);
        var executor = host.CreateExecutor(aggregator);
        await executor.StartAsync(CancellationToken.None);

        // The first publish from the executor throws, as a busy database would on a status write.
        await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(aggregator.Threw))).Should().BeTrue();

        var next = await host.Queue.EnqueueAsync("Explode", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(async () =>
        {
            host.TimeProvider.Advance(CommandExecutor.WorkerFaultPause);

            return await StatusAsync(host, next.Id) == CommandStatus.Failed;
        })).Should().BeTrue("the other commands still run");

        executor.ExecuteTask!.IsFaulted.Should().BeFalse("a bookkeeping fault must not end the executor");

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    [Fact]
    public async Task A_running_command_is_never_started_a_second_time_and_a_repeat_enqueue_returns_it()
    {
        var handler = new BlockingCommandHandler("Long");
        await using var host = await JobTestHost.CreateAsync(services => services.AddSingleton<ICommandHandler>(handler));
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);
        (await TestWait.UntilAsync(() => Task.FromResult(handler.StartedCount == 1))).Should().BeTrue();

        var duplicate = await host.Queue.EnqueueAsync("Long", null, CommandTrigger.Manual, CancellationToken.None);

        duplicate.Id.Should().Be(queued.Id, "the running command is returned instead of queueing a copy");
        handler.StartedCount.Should().Be(1);
        handler.PeakConcurrency.Should().Be(1);

        handler.Release();
        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Completed))
            .Should().BeTrue();
        handler.StartedCount.Should().Be(1);

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_more_than_three_commands_run_at_the_same_time()
    {
        var handlers = Enumerable.Range(1, 4)
            .Select(index => new BlockingCommandHandler($"Hold{index}"))
            .ToArray();

        await using var host = await JobTestHost.CreateAsync(services =>
        {
            foreach (var blocking in handlers)
            {
                services.AddSingleton<ICommandHandler>(blocking);
            }
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var ids = new List<long>();
        foreach (var blocking in handlers)
        {
            var queued = await host.Queue.EnqueueAsync(blocking.Name, null, CommandTrigger.Manual, CancellationToken.None);
            ids.Add(queued.Id);
        }

        (await TestWait.UntilAsync(() => Task.FromResult(handlers.Sum(blocking => blocking.StartedCount) == 3)))
            .Should().BeTrue("three workers should have picked up three commands");

        // All three workers are blocked, so the fourth command has to stay queued.
        handlers.Count(blocking => blocking.StartedCount > 0).Should().Be(3);
        handlers.Where(blocking => blocking.StartedCount > 0).Should().OnlyContain(blocking => blocking.PeakConcurrency == 1);

        var statuses = new List<CommandStatus>();
        foreach (var id in ids)
        {
            statuses.Add((await host.Queue.GetAsync(id, CancellationToken.None))!.Status);
        }

        statuses.Count(status => status == CommandStatus.Started).Should().Be(3);
        statuses.Count(status => status == CommandStatus.Queued).Should().Be(1);

        foreach (var blocking in handlers)
        {
            blocking.Release();
        }

        (await TestWait.UntilAsync(async () =>
        {
            foreach (var id in ids)
            {
                if (await StatusAsync(host, id) != CommandStatus.Completed)
                {
                    return false;
                }
            }

            return true;
        })).Should().BeTrue("the fourth command runs once a worker is free");

        handlers.Should().OnlyContain(blocking => blocking.StartedCount == 1);

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Commands_left_behind_by_a_previous_process_are_orphaned_on_start()
    {
        await using var host = await JobTestHost.CreateAsync();
        var queued = await host.InsertCommandAsync("Heartbeat", CommandStatus.Queued);
        var started = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started);

        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued) == CommandStatus.Orphaned
            && await StatusAsync(host, started) == CommandStatus.Orphaned)).Should().BeTrue();

        var orphans = await host.Queue.ListAsync(50, CancellationToken.None);
        orphans.Should().OnlyContain(command => command.Status == CommandStatus.Orphaned);
        orphans.Should().OnlyContain(command => command.EndedAt != null);

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Every_state_change_is_published_and_a_throwing_handler_does_not_stop_the_command()
    {
        var recorder = new RecordingEventHandler();

        await using var host = await JobTestHost.CreateAsync(services =>
        {
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(recorder);
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(new ThrowingEventHandler());
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var queued = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, queued.Id) == CommandStatus.Completed))
            .Should().BeTrue("a throwing subscriber must not fail the command");

        // The row is saved before the event is published, so wait for the event itself.
        (await TestWait.UntilAsync(() => Task.FromResult(recorder.Updates.Any(
            update => update.Id == queued.Id && update.Status == CommandStatus.Completed))))
            .Should().BeTrue("the completed state is published too");

        recorder.Updates
            .Where(update => update.Id == queued.Id)
            .Select(update => update.Status)
            .Should().ContainInOrder(CommandStatus.Queued, CommandStatus.Started, CommandStatus.Completed);

        var completed = await host.Queue.GetAsync(queued.Id, CancellationToken.None);
        completed!.Result.Should().Be(CommandResult.Successful);
        completed.Message.Should().Be("Heartbeat OK");

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Commands_that_hand_their_worker_back_do_not_starve_the_others_and_never_exceed_the_limit()
    {
        YieldingCommandHandler.Reset();
        var waiting = Enumerable.Range(0, CommandExecutor.MaxConcurrency + 2)
            .Select(index => new YieldingCommandHandler($"Waiting{index}"))
            .ToList();
        await using var host = await JobTestHost.CreateAsync(services =>
        {
            foreach (var handler in waiting)
            {
                services.AddSingleton<ICommandHandler>(handler);
            }
        });
        var executor = host.CreateExecutor();
        await executor.StartAsync(CancellationToken.None);

        var ids = new List<long>();

        foreach (var handler in waiting)
        {
            ids.Add((await host.Queue.EnqueueAsync(handler.Name, null, CommandTrigger.Manual, CancellationToken.None)).Id);
        }

        (await TestWait.UntilAsync(() => Task.FromResult(waiting.All(handler => handler.IsWaiting))))
            .Should().BeTrue("more waiting commands than workers all got to wait");

        var other = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        (await TestWait.UntilAsync(async () => await StatusAsync(host, other.Id) == CommandStatus.Completed))
            .Should().BeTrue("waiting commands gave their workers back");

        foreach (var handler in waiting)
        {
            handler.Release();
        }

        foreach (var id in ids)
        {
            (await TestWait.UntilAsync(async () => await StatusAsync(host, id) == CommandStatus.Completed))
                .Should().BeTrue();
        }

        YieldingCommandHandler.PeakWorking.Should().BeLessThanOrEqualTo(CommandExecutor.MaxConcurrency);

        using var stopBudget = new CancellationTokenSource(TestWait.Timeout);
        await executor.StopAsync(stopBudget.Token);
    }

    private static async Task<CommandStatus?> StatusAsync(JobTestHost host, long id) =>
        (await host.Queue.GetAsync(id, CancellationToken.None))?.Status;

    private sealed class ThrowOnceAggregator(IEventAggregator inner) : IEventAggregator
    {
        private int _thrown;

        public bool Threw => Volatile.Read(ref _thrown) > 0;

        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : IEvent
        {
            if (Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("database is locked");
            }

            return inner.PublishAsync(message, cancellationToken);
        }
    }
}
