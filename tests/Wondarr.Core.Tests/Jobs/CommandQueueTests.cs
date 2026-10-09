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
    public async Task The_same_command_with_another_body_is_queued_next_to_the_running_one()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"referenceLibraryId\":1}",
            CommandTrigger.Manual,
            CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"referenceLibraryId\":2}",
            CommandTrigger.Manual,
            CancellationToken.None);

        second.Id.Should().NotBe(first.Id);
        (await host.Queue.ListAsync(50, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_same_body_twice_is_one_command_whatever_its_whitespace_and_key_order()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"referenceLibraryId\":1,\"ids\":[1,2]}",
            CommandTrigger.Manual,
            CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{ \"ids\": [1, 2], \"referenceLibraryId\": 1.0, \"name\": \"Heartbeat\" }",
            CommandTrigger.Manual,
            CancellationToken.None);
        var reordered = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"referenceLibraryId\":1,\"ids\":[2,1]}",
            CommandTrigger.Manual,
            CancellationToken.None);

        second.Id.Should().Be(first.Id);
        reordered.Id.Should().NotBe(first.Id, "the order of an array is part of its value");
    }

    [Fact]
    public async Task Empty_bodies_and_a_body_that_only_repeats_the_name_are_the_same_command()
    {
        await using var host = await JobTestHost.CreateAsync();

        var scheduled = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);
        var again = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);
        var blank = await host.Queue.EnqueueAsync("Heartbeat", " ", CommandTrigger.Manual, CancellationToken.None);
        var named = await host.Queue.EnqueueAsync("Heartbeat", "{\"name\":\"Heartbeat\"}", CommandTrigger.Manual, CancellationToken.None);

        again.Id.Should().Be(scheduled.Id);
        blank.Id.Should().Be(scheduled.Id);
        named.Id.Should().Be(scheduled.Id);
        (await host.Queue.ListAsync(50, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Property_names_are_compared_without_regard_to_case()
    {
        await using var host = await JobTestHost.CreateAsync();

        var first = await host.Queue.EnqueueAsync(
            "Heartbeat", "{\"name\":\"Heartbeat\",\"referenceLibraryId\":1}", CommandTrigger.Manual, CancellationToken.None);
        var second = await host.Queue.EnqueueAsync(
            "Heartbeat", "{\"Name\":\"Heartbeat\",\"ReferenceLibraryId\":1}", CommandTrigger.Manual, CancellationToken.None);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task A_null_property_is_the_same_as_an_absent_one()
    {
        await using var host = await JobTestHost.CreateAsync();

        var all = await host.Queue.EnqueueAsync("Heartbeat", null, CommandTrigger.Scheduled, CancellationToken.None);
        var nulled = await host.Queue.EnqueueAsync(
            "Heartbeat", "{\"name\":\"Heartbeat\",\"referenceLibraryId\":null}", CommandTrigger.Manual, CancellationToken.None);
        var other = await host.Queue.EnqueueAsync(
            "Heartbeat", "{\"name\":\"Heartbeat\",\"referenceLibraryId\":3}", CommandTrigger.Manual, CancellationToken.None);

        nulled.Id.Should().Be(all.Id);
        other.Id.Should().NotBe(all.Id);
    }

    [Fact]
    public async Task A_started_command_with_a_different_body_does_not_swallow_a_new_one()
    {
        await using var host = await JobTestHost.CreateAsync();

        var running = await host.InsertCommandAsync("Heartbeat", CommandStatus.Started, "{\"name\":\"Heartbeat\",\"referenceLibraryId\":1}");
        var other = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"name\":\"Heartbeat\",\"referenceLibraryId\":2}",
            CommandTrigger.Manual,
            CancellationToken.None);
        var same = await host.Queue.EnqueueAsync(
            "Heartbeat",
            "{\"referenceLibraryId\":1,\"name\":\"Heartbeat\"}",
            CommandTrigger.Manual,
            CancellationToken.None);

        other.Id.Should().NotBe(running);
        same.Id.Should().Be(running);
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
