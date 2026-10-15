using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Testing;
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
    public async Task Removes_the_folder_of_a_finished_download_with_what_was_left_in_it()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });
        var partial = Leftover(seed.DownloadDirectory, "08 - Get Lucky.flac.incomplete");
        var cover = Leftover(seed.DownloadDirectory, "cover.jpg");
        var neighbour = Leftover(Path.Combine(Path.GetDirectoryName(seed.DownloadDirectory)!, "another-grab"), "keep.flac");

        await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

        Directory.Exists(seed.DownloadDirectory).Should().BeFalse();
        File.Exists(partial).Should().BeFalse();
        File.Exists(cover).Should().BeFalse();
        File.Exists(neighbour).Should().BeTrue("another grab's folder is not this item's");
        Directory.Exists(Path.GetDirectoryName(seed.DownloadDirectory)!).Should().BeTrue("never a parent");
    }

    [Fact]
    public async Task Keeps_a_folder_that_is_not_empty_when_the_option_is_off()
    {
        await using var host = await QueueTestHost.CreateAsync();
        host.ImportOptions.CleanLeftoverFolders = false;
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });
        var partial = Leftover(seed.DownloadDirectory, "08 - Get Lucky.flac.incomplete");

        await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

        File.Exists(seed.DownloadPath).Should().BeFalse("the download itself is still deleted");
        File.Exists(partial).Should().BeTrue();
        Directory.Exists(seed.DownloadDirectory).Should().BeTrue();
    }

    [Theory]
    [InlineData("downloads")]
    [InlineData("data/downloads")]
    [InlineData("wondarr/not-a-guid")]
    [InlineData("wondarr/0123456789ABCDEF0123456789ABCDEF")]
    public async Task Keeps_what_is_left_when_the_destination_is_not_wondarr_slash_a_guid(string destination)
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = false;
        });

        // The download sits in exactly the folder the destination names, so it is "the item's own", and
        // whatever the folder is called, only an empty one may go.
        var folder = Path.Combine(DownloadsRoot(seed), destination.Replace('/', Path.DirectorySeparatorChar));
        var download = Leftover(folder, "08 - Get Lucky.flac");
        var partial = Leftover(folder, "08 - Get Lucky.flac.incomplete");
        await host.SetDestinationAsync(seed.QueueItemId, destination, download);

        await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

        File.Exists(download).Should().BeFalse("the download itself is still deleted");
        File.Exists(partial).Should().BeTrue("only wondarr/<32 lower-case hex> is removed with what is in it");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task Keeps_the_folder_and_what_a_link_inside_it_leads_to()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });
        var partial = Leftover(seed.DownloadDirectory, "08 - Get Lucky.flac.incomplete");
        var elsewhere = Leftover(Path.Combine(DownloadsRoot(seed), "elsewhere"), "keep.flac");
        var link = Path.Combine(seed.DownloadDirectory, "link");
        DirectoryLinks.Create(link, Path.GetDirectoryName(elsewhere)!);

        try
        {
            await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

            File.Exists(seed.DownloadPath).Should().BeFalse("the download itself is still deleted");
            File.Exists(elsewhere).Should().BeTrue("deleting through a link would reach a folder that is not the item's");
            File.Exists(partial).Should().BeTrue("a folder that holds a link is not removed with its contents");
            Directory.Exists(seed.DownloadDirectory).Should().BeTrue();
        }
        finally
        {
            DirectoryLinks.Remove(link);
        }
    }

    [Fact]
    public async Task Keeps_what_is_left_when_the_wondarr_folder_above_it_is_a_link()
    {
        await using var host = await QueueTestHost.CreateAsync();
        var seed = await host.SeedAsync(options =>
        {
            options.State = QueueItemState.Completed;
            options.Progress = 1;
            options.CreateDownload = true;
        });
        var partial = Leftover(seed.DownloadDirectory, "08 - Get Lucky.flac.incomplete");

        // <downloads>/wondarr is a link to a folder somewhere else; the item's path still reads wondarr/<guid>.
        var wondarr = Path.GetDirectoryName(seed.DownloadDirectory)!;
        var real = wondarr + "-real";
        Directory.Move(wondarr, real);
        DirectoryLinks.Create(wondarr, real);

        try
        {
            await host.Actions.RemoveAsync(seed.QueueItemId, blocklist: true, retry: false, CancellationToken.None);

            File.Exists(Path.Combine(real, Path.GetFileName(seed.DownloadDirectory), "08 - Get Lucky.flac.incomplete")).Should().BeTrue(
                "nothing is removed with its contents through a link on the way");
            Directory.Exists(Path.Combine(real, Path.GetFileName(seed.DownloadDirectory))).Should().BeTrue();
            File.Exists(partial).Should().BeTrue();
        }
        finally
        {
            DirectoryLinks.Remove(wondarr);
        }
    }

    private static string DownloadsRoot(QueueSeed seed) =>
        Path.GetDirectoryName(Path.GetDirectoryName(seed.DownloadDirectory)!)!;

    private static string Leftover(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");

        return path;
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
