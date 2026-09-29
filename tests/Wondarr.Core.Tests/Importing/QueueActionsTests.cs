using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// Removing a queue item (ARCHITECTURE §5.4): an item still in flight is stopped at the source, and
/// optionally blocklisted and retried with the next candidate; an item that is already finished is
/// only dropped from the queue, because the history keeps the record.
/// </summary>
public sealed class QueueActionsTests
{
    [Fact]
    public async Task Removes_an_active_grab_and_stops_it_at_the_source()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options => options.State = QueueItemState.Downloading);

        var removed = await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: false, retry: false, CancellationToken.None);

        removed.Should().BeTrue();
        host.Source.Cancels.Should().ContainSingle();

        var item = await host.ItemAsync(seed.QueueItemId);
        item.State.Should().Be(QueueItemState.Cancelled);
        item.Message.Should().Be("Removed by the user");
        item.FinishedAt.Should().NotBeNull();

        (await host.BlocklistAsync()).Should().BeEmpty();
        host.SearchService.Grabs.Should().BeEmpty();

        var history = await host.HistoryAsync();
        history.Should().ContainSingle();
        history[0].EventType.Should().Be(HistoryEventType.Failed);
        history[0].Data.Should().Contain("removedByUser");

        var changed = host.Events.Of<QueueItemChangedEvent>().Should().ContainSingle().Subject;
        changed.State.Should().Be(QueueItemState.Cancelled);
    }

    [Fact]
    public async Task Blocklists_the_candidate_and_deletes_a_finished_download()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });

        File.Exists(seed.DownloadPath).Should().BeTrue();

        var removed = await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

        removed.Should().BeTrue();

        var blocked = (await host.BlocklistAsync()).Should().ContainSingle().Subject;
        blocked.SongId.Should().Be(seed.SongId);
        blocked.SourceType.Should().Be("soulseek");
        blocked.Reason.Should().Be("Removed by the user");

        File.Exists(seed.DownloadPath).Should().BeFalse();
        Directory.Exists(seed.DownloadDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Grabs_the_next_candidate_when_the_user_asks_for_a_retry()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Downloading;
            options.Attempt = 10;
        });

        var removed = await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: true, CancellationToken.None);

        removed.Should().BeTrue();

        // A manual retry ignores the attempt budget: the user asked for the next candidate.
        host.SearchService.Grabs.Should().Equal((seed.SearchRunId, 11));

        var history = await host.HistoryAsync();
        history.Should().ContainSingle();
        history[0].Data.Should().Contain("retried");
    }

    [Fact]
    public async Task Deletes_a_finished_item_from_the_queue()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options => options.State = QueueItemState.Failed);

        var removed = await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: true, CancellationToken.None);

        removed.Should().BeTrue();
        (await host.FindItemAsync(seed.QueueItemId)).Should().BeNull();

        // The history is the record, so nothing new is written and nothing is cancelled.
        (await host.HistoryAsync()).Should().BeEmpty();
        host.Source.Cancels.Should().BeEmpty();
        host.SearchService.Grabs.Should().BeEmpty();
    }

    [Fact]
    public async Task Says_no_to_an_item_that_is_not_there()
    {
        await using var host = await QueueTestHost.CreateAsync();

        var removed = await host.Actions.RemoveAsync(404, blocklist: false, retry: false, CancellationToken.None);

        removed.Should().BeFalse();
    }
}
