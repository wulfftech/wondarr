using Compilarr.Core.Jobs;
using Compilarr.Core.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Core.Tests.Jobs;

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

    private static async Task<CommandStatus?> StatusAsync(JobTestHost host, long id) =>
        (await host.Queue.GetAsync(id, CancellationToken.None))?.Status;
}
