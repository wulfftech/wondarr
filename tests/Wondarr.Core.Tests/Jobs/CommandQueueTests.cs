using Wondarr.Core.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

public class CommandQueueTests
{
    [Fact]
    public async Task Enqueueing_a_command_with_no_handler_throws()
    {
        await using var host = await JobTestHost.CreateAsync();

        var act = async () => await host.Queue.EnqueueAsync(
            "NoSuchCommand",
            null,
            CommandTrigger.Manual,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnknownCommandException>();
    }

    [Fact]
    public async Task A_second_enqueue_of_a_queued_command_returns_the_same_row()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync("Heartbeat", "{}", CommandTrigger.Manual, CancellationToken.None);
        var second = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);

        second.Id.Should().Be(first.Id);
        (await host.Queue.ListAsync(50, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Listing_returns_the_newest_command_first()
    {
        await using var host = await JobTestHost.CreateAsync();

        await host.InsertCommandAsync("Heartbeat", CommandStatus.Completed);
        var newest = await host.InsertCommandAsync("CheckHealth", CommandStatus.Completed);

        var commands = await host.Queue.ListAsync(50, CancellationToken.None);

        commands.Should().HaveCount(2);
        commands[0].Id.Should().Be(newest);
    }

    [Fact]
    public async Task A_queued_command_can_be_cancelled()
    {
        await using var host = await JobTestHost.CreateAsync();
        var queued = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Manual, CancellationToken.None);

        var cancelled = await host.Queue.CancelAsync(queued.Id, CancellationToken.None);

        cancelled.Should().BeTrue();

        var record = await host.Queue.GetAsync(queued.Id, CancellationToken.None);
        record!.Status.Should().Be(CommandStatus.Cancelled);
    }

    [Fact]
    public async Task A_command_that_is_not_queued_cannot_be_cancelled()
    {
        await using var host = await JobTestHost.CreateAsync();
        var completed = await host.InsertCommandAsync("Heartbeat", CommandStatus.Completed);

        (await host.Queue.CancelAsync(completed, CancellationToken.None)).Should().BeFalse();

        (await host.Queue.GetAsync(completed, CancellationToken.None))!.Status.Should().Be(CommandStatus.Completed);
        (await host.Queue.CancelAsync(4242, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_whole_body_is_stored_as_the_command_body()
    {
        await using var host = await JobTestHost.CreateAsync();

        var record = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"extra\":true}",
            CommandTrigger.Manual,
            CancellationToken.None);

        (await host.Queue.GetAsync(record.Id, CancellationToken.None))!.Body
            .Should().Be("{\"name\":\"Heartbeat\",\"extra\":true}");
    }
}
