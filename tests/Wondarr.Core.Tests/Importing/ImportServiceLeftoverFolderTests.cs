using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Importing;
using Wondarr.Core.Sources;
using Wondarr.Core.Verification;
using Wondarr.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// <c>import.clean_leftover_folders</c> (M-44): after an import, Wondarr's own per-grab folder — the
/// Soulseek folder, the container staging folder, the YouTube folder, all named <c>wondarr/&lt;guid&gt;</c>
/// — goes with whatever the download left in it. With the option off it goes only while it is empty.
/// A torrent's data folder, which is not Wondarr's, is never touched.
/// </summary>
public sealed class ImportServiceLeftoverFolderTests
{
    private const string Partial = "08 - Get Lucky.flac.incomplete";
    private const string Sidecar = "cover.jpg";

    [Fact]
    public async Task The_option_is_on_by_default()
    {
        await using var host = await ImportTestHost.CreateAsync();

        host.ImportOptions.CleanLeftoverFolders.Should().BeTrue();
        new Wondarr.Core.Organizer.ImportOptions().CleanLeftoverFolders.Should().BeTrue();
    }

    [Theory]
    [InlineData(SourceTypes.Soulseek)]
    [InlineData(SourceTypes.Torznab)]
    [InlineData(SourceTypes.Newznab)]
    [InlineData(SourceTypes.YouTube)]
    public async Task A_grab_folder_with_a_partial_and_a_sidecar_left_in_it_is_removed_after_the_import(string sourceType)
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await SeedAsync(host, sourceType);
        var leftovers = Leave(seed, Partial, Sidecar);
        var neighbour = Write(Path.Combine(Path.GetDirectoryName(seed.DownloadDirectory)!, "another-grab", "keep.flac"));
        var torrentData = Write(Path.Combine(DownloadsRoot(seed), "torrents", "Random Access Memories", "08 - Get Lucky.flac"));

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        Directory.Exists(seed.DownloadDirectory).Should().BeFalse("the folder goes with the partial and the sidecar");
        leftovers.Should().OnlyContain(path => !File.Exists(path));
        File.Exists(neighbour).Should().BeTrue("another grab's folder is not this item's");
        File.Exists(torrentData).Should().BeTrue("a torrent's data is never touched; it stays for as long as the client has the torrent");
        Directory.Exists(Path.GetDirectoryName(seed.DownloadDirectory)!).Should().BeTrue("never a parent");
        host.LogMessages.Should().Contain(message => message.Contains(seed.DownloadDirectory, StringComparison.Ordinal) && message.Contains("Removed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SourceTypes.Soulseek)]
    [InlineData(SourceTypes.Torznab)]
    public async Task With_the_option_off_a_folder_that_is_not_empty_stays(string sourceType)
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.ImportOptions.CleanLeftoverFolders = false;
        var seed = await SeedAsync(host, sourceType);
        var leftovers = Leave(seed, Partial, Sidecar);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        leftovers.Should().OnlyContain(path => File.Exists(path));
        Directory.Exists(seed.DownloadDirectory).Should().BeTrue("with the option off only an empty folder is removed");
    }

    [Fact]
    public async Task With_the_option_off_an_empty_folder_is_still_removed()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.ImportOptions.CleanLeftoverFolders = false;
        var seed = await SeedAsync(host, SourceTypes.Soulseek);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        Directory.Exists(seed.DownloadDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task A_rejected_download_loses_its_folder_with_what_was_left_in_it()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = new VerificationResult(
            VerificationOutcome.Failed,
            "Fingerprint matches a different recording",
            FakeDownloadVerifier.Flac(),
            36,
            null,
            null,
            "someone-else",
            null,
            false);
        var seed = await host.SeedAsync();
        var leftovers = Leave(seed, Partial);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        File.Exists(seed.DownloadPath).Should().BeFalse();
        leftovers.Should().OnlyContain(path => !File.Exists(path));
        Directory.Exists(seed.DownloadDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData("downloads")]
    [InlineData("data/downloads")]
    [InlineData("wondarr/not-a-guid")]
    [InlineData("wondarr/0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("elsewhere/0123456789abcdef0123456789abcdef")]
    public async Task A_destination_that_is_not_wondarr_slash_a_guid_keeps_what_is_left(string destination)
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync(options => options.CreateDownload = false);

        // A destination that names a real-looking folder, with the download in exactly that folder: the old
        // empty-only rule is all that applies, whatever the folder is called.
        var folder = Path.Combine(DownloadsRoot(seed), destination.Replace('/', Path.DirectorySeparatorChar));
        var download = Write(Path.Combine(folder, "08 - Get Lucky.flac"));
        var leftover = Write(Path.Combine(folder, Partial));
        var item = await host.Context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
        item.Destination = destination;
        item.DownloadPath = download;
        await host.Context.SaveChangesAsync();
        host.Placer.OnPlaceAsync = request => MoveAway(request.SourcePath);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.Exists(leftover).Should().BeTrue("only wondarr/<32 lower-case hex> is removed with what is in it");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("lookalike")]
    public async Task A_download_that_is_not_in_the_items_own_folder_is_never_cleaned(string kind)
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await host.SeedAsync(options => options.CreateDownload = false);
        var wondarr = Path.GetDirectoryName(seed.DownloadDirectory)!;
        var folder = kind == "outside"
            ? Path.Combine(DownloadsRoot(seed), "elsewhere")
            : Path.Combine(wondarr, Guid.NewGuid().ToString("N"));
        var download = Write(Path.Combine(folder, "08 - Get Lucky.flac"));
        var leftover = Write(Path.Combine(folder, Partial));
        await host.SetDownloadPathAsync(seed.QueueItemId, download);
        host.Placer.OnPlaceAsync = request => MoveAway(request.SourcePath);

        (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

        File.Exists(leftover).Should().BeTrue("only the item's own wondarr/<guid> folder is Wondarr's to clean");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task A_link_in_the_folder_keeps_it_and_the_refusal_is_logged_with_the_path()
    {
        await using var host = await ImportTestHost.CreateAsync();
        var seed = await SeedAsync(host, SourceTypes.Soulseek);
        var leftovers = Leave(seed, Partial);
        var real = Write(Path.Combine(DownloadsRoot(seed), "real", "keep.flac"));
        var link = Path.Combine(seed.DownloadDirectory, "link");
        DirectoryLinks.Create(link, Path.GetDirectoryName(real)!);

        try
        {
            (await host.Import.ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Imported);

            File.Exists(real).Should().BeTrue("deleting through a link would take a folder somewhere else with it");
            leftovers.Should().OnlyContain(path => File.Exists(path));
            host.LogMessages.Should().Contain(message => message.Contains(seed.DownloadDirectory, StringComparison.Ordinal) && message.Contains("Not removing", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryLinks.Remove(link);
        }
    }

    /// <summary>A seeded grab whose placement moves the downloaded file away, as the real placer does.</summary>
    private static async Task<ImportSeed> SeedAsync(ImportTestHost host, string sourceType)
    {
        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = sourceType;

            if (sourceType == SourceTypes.YouTube)
            {
                options.DownloadName = "dQw4w9WgXcQ.opus";
                options.RemotePath = "dQw4w9WgXcQ";
            }
        });

        host.Placer.OnPlaceAsync = request => MoveAway(request.SourcePath);

        return seed;
    }

    /// <summary>What the real placer does to the download: it is moved into the library, so it is no longer in its folder.</summary>
    private static Task MoveAway(string source)
    {
        File.Delete(source);

        return Task.CompletedTask;
    }

    /// <summary>The folder that holds <c>wondarr/&lt;guid&gt;</c> and, beside it, whatever else the test puts there.</summary>
    private static string DownloadsRoot(ImportSeed seed) => Path.GetDirectoryName(Path.GetDirectoryName(seed.DownloadDirectory)!)!;

    private static List<string> Leave(ImportSeed seed, params string[] names) =>
        [.. names.Select(name => Write(Path.Combine(seed.DownloadDirectory, name)))];

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        return path;
    }
}
