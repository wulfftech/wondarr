using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Organizer;
using Wondarr.Core.Tests.Support;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// An import into a library whose root is missing or carries no marker (an unmounted share looks like
/// that) waits in the queue: nothing is counted, blocklisted or recorded, and nothing is created.
/// </summary>
public sealed class ImportLibraryRootTests
{
    private static string Marker(ImportTestHost host) => Path.Combine(host.LibraryRoot, LibraryRoots.MarkerFileName);

    [Fact]
    public async Task An_unmarked_root_defers_the_item_for_five_minutes_and_touches_nothing()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();
        File.Delete(Marker(host));

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        await using var context = host.Database.CreateContext(host.Time);
        var item = await context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);

        item.State.Should().Be(QueueItemState.Completed);
        item.NextCheckAt.Should().Be(host.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));
        item.Message.Should().Contain("has no .wondarr-library file").And.Contain(host.LibraryRoot);
        item.DownloadPath.Should().Be(seed.DownloadPath);

        (await context.Blocklist.CountAsync()).Should().Be(0, "the file is not at fault");
        (await context.History.CountAsync()).Should().Be(0);
        (await context.SongFiles.CountAsync()).Should().Be(0);
        File.Exists(seed.DownloadPath).Should().BeTrue("the download stays where it is");
        host.Placer.Requests.Should().BeEmpty();
        host.TagWriter.Writes.Should().BeEmpty();
        host.Search.Grabs.Should().BeEmpty("no next candidate is searched for");
        Directory.GetFileSystemEntries(host.LibraryRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_root_defers_the_item_and_the_root_is_not_created()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();
        Directory.Delete(host.LibraryRoot, recursive: true);

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        await using var context = host.Database.CreateContext(host.Time);
        var item = await context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);

        item.State.Should().Be(QueueItemState.Completed);
        item.Message.Should().Contain("does not exist");
        Directory.Exists(host.LibraryRoot).Should().BeFalse();
        File.Exists(seed.DownloadPath).Should().BeTrue();
    }

    [Fact]
    public async Task The_item_imports_once_the_marker_is_back()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();
        File.Delete(Marker(host));

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Deferred);

        TestLibraryRoots.Real.Mark(host.LibraryRoot);
        host.Time.Advance(TimeSpan.FromMinutes(5));

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);
    }

    [Fact]
    public async Task A_share_that_drops_during_the_import_defers_it_instead_of_failing_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync();

        // The root is ready when the import starts and gone by the time the file is filed.
        host.Verifier.OnVerifyAsync = _ =>
        {
            File.Delete(Marker(host));

            return Task.CompletedTask;
        };

        var outcome = await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None);

        outcome.Should().Be(ImportOutcome.Deferred);

        await using var context = host.Database.CreateContext(host.Time);
        var item = await context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);

        item.State.Should().Be(QueueItemState.Completed);
        item.NextCheckAt.Should().Be(host.Time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));
        item.Message.Should().Contain("has no .wondarr-library file");
        item.DownloadPath.Should().Be(seed.DownloadPath);

        (await context.Blocklist.CountAsync()).Should().Be(0);
        (await context.History.CountAsync()).Should().Be(0);
        (await context.SongFiles.CountAsync()).Should().Be(0);
        host.Placer.Requests.Should().BeEmpty();
        File.Exists(seed.DownloadPath).Should().BeTrue();
    }
}
