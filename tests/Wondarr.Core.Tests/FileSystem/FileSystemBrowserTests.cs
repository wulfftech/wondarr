using FluentAssertions;
using Wondarr.Core.Configuration;
using Wondarr.Core.FileSystem;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Tests.MusicVideos;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Testing;
using Xunit;

namespace Wondarr.Core.Tests.FileSystem;

/// <summary>The folder picker's backend: only mounts in a container, never outside a root, every disk call bounded.</summary>
public sealed class FileSystemBrowserTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [LinuxFact]
    public async Task In_a_container_the_roots_are_exactly_the_real_data_mounts()
    {
        var mounts = new ContainerMounts(
            path => path == "/.dockerenv",
            () => ContainerMountsTests.Sample,
            directoryExists: path => path is not ("/etc/hosts" or "/etc/resolv.conf"));

        var result = await Browser(mounts).BrowseAsync(null, CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Ok);
        result.Value!.Path.Should().BeNull();
        result.Value.Folders.Should().BeEmpty();
        result.Value.Roots.Should().Equal("/data/downloads", "/data/music");
    }

    [LinuxFact]
    public async Task Outside_a_container_the_root_is_the_file_system_root()
    {
        var result = await Browser(new ContainerMounts(_ => false, () => null)).BrowseAsync(null, CancellationToken.None);

        result.Value!.Roots.Should().Equal("/");
    }

    [LinuxFact]
    public async Task A_folder_lists_only_visible_sub_folders_and_never_files_or_the_recycle_bin()
    {
        Directory.CreateDirectory(_temp.Full("Beta"));
        Directory.CreateDirectory(_temp.Full("alpha"));
        Directory.CreateDirectory(_temp.Full(".hidden"));
        Directory.CreateDirectory(_temp.Full("recycle"));
        File.WriteAllText(_temp.Full("song.mp3"), "x");

        var result = await Browser(Mounted(), recycleBin: _temp.Full("recycle")).BrowseAsync(_temp.Root, CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Ok);
        result.Value!.Folders.Select(folder => folder.Name).Should().Equal("alpha", "Beta");
        result.Value.Folders[0].Path.Should().Be(_temp.Full("alpha"));
        result.Value.Parent.Should().BeNull("the mount itself is the top");
        result.Value.Roots.Should().Equal(_temp.Root);
    }

    [LinuxFact]
    public async Task A_sub_folder_has_its_parent()
    {
        Directory.CreateDirectory(_temp.Full("a/b"));

        var result = await Browser(Mounted()).BrowseAsync(_temp.Full("a/b/"), CancellationToken.None);

        result.Value!.Path.Should().Be(_temp.Full("a/b"));
        result.Value.Parent.Should().Be(_temp.Full("a"));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("a/../b")]
    [InlineData("/etc")]
    public async Task A_path_that_is_relative_climbs_or_leaves_every_root_is_refused(string path)
    {
        var requested = path.StartsWith("/etc", StringComparison.Ordinal) ? path : path.StartsWith("a/", StringComparison.Ordinal) ? _temp.Root + "/" + path : path;

        var result = await Browser(Mounted()).BrowseAsync(requested, CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Invalid);
    }

    [LinuxFact]
    public async Task Dot_dot_back_out_of_the_mount_is_refused_even_when_it_would_land_inside_another()
    {
        Directory.CreateDirectory(_temp.Full("x"));

        var result = await Browser(Mounted()).BrowseAsync(_temp.Full("x") + "/../../", CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Invalid);
    }

    [LinuxFact]
    public async Task A_sibling_that_shares_the_mount_prefix_is_outside_it()
    {
        var result = await Browser(Mounted()).BrowseAsync(_temp.Root + "-other", CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Invalid);
    }

    [LinuxFact]
    public async Task A_folder_that_does_not_exist_is_not_found()
    {
        var result = await Browser(Mounted()).BrowseAsync(_temp.Full("nope"), CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.NotFound);
    }

    [LinuxFact]
    public async Task A_hung_share_times_out_instead_of_holding_the_request()
    {
        using var release = new ManualResetEventSlim();
        var mounts = new ContainerMounts(
            _ => true,
            () => ["1 0 0:50 / / rw - overlay overlay rw", $"2 1 0:60 / {_temp.Root} rw - cifs //nas/x rw"],
            directoryExists: _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));

                return true;
            });

        var result = await Browser(mounts).BrowseAsync(_temp.Root, CancellationToken.None);
        release.Set();

        result.Outcome.Should().Be(FileSystemOutcome.TimedOut);
    }

    [LinuxFact]
    public async Task A_new_folder_is_made_inside_a_mount()
    {
        var result = await Browser(Mounted()).CreateFolderAsync(_temp.Root, "Videos", CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Ok);
        result.Value!.Path.Should().Be(_temp.Full("Videos"));
        Directory.Exists(_temp.Full("Videos")).Should().BeTrue();

        (await Browser(Mounted()).CreateFolderAsync(_temp.Root, "Videos", CancellationToken.None)).Outcome
            .Should().Be(FileSystemOutcome.Conflict);
    }

    [LinuxFact]
    public async Task A_new_folder_outside_a_mount_or_in_a_missing_parent_is_refused()
    {
        (await Browser(Mounted()).CreateFolderAsync("/tmp", "Videos", CancellationToken.None)).Outcome
            .Should().Be(FileSystemOutcome.Invalid);
        (await Browser(Mounted()).CreateFolderAsync(_temp.Full("missing"), "Videos", CancellationToken.None)).Outcome
            .Should().Be(FileSystemOutcome.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    public async Task A_name_that_is_not_one_plain_segment_is_refused_and_nothing_is_made(string name)
    {
        var result = await Browser(Mounted()).CreateFolderAsync(_temp.Root, name, CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Invalid);
        Directory.GetDirectories(_temp.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_over_255_characters_is_refused()
    {
        var result = await Browser(Mounted()).CreateFolderAsync(_temp.Root, new string('a', 256), CancellationToken.None);

        result.Outcome.Should().Be(FileSystemOutcome.Invalid);
    }

    private ContainerMounts Mounted() => new(
        _ => true,
        () => ["1 0 0:50 / / rw - overlay overlay rw", $"2 1 0:60 / {_temp.Root} rw - cifs //nas/x rw"]);

    private static FileSystemBrowser Browser(ContainerMounts mounts, string? recycleBin = null) => new(
        mounts,
        new WondarrPaths("/config"),
        new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = recycleBin }));
}
