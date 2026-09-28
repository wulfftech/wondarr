using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class FilePlacerTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly ImportOptions _options = new();

    public FilePlacerTests()
    {
        // Everything the tests touch — library, downloads and recycle bin — lives under one root
        // that Dispose removes and checks.
        _options.RecycleBinPath = _temp.Full("recycle");
    }

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Library => _temp.Full("library");

    private FilePlacer Placer()
    {
        var disk = new DiskOperations();
        var paths = new WondarrPaths(_temp.Full("config"));
        var monitor = new TestOptionsMonitor<ImportOptions>(_options);
        var recycleBin = new RecycleBin(disk, monitor, paths, TimeProvider.System, NullLogger<RecycleBin>.Instance);

        return new FilePlacer(disk, recycleBin, monitor, NullLogger<FilePlacer>.Instance);
    }

    private PlacementRequest Request(
        string source,
        string relative,
        TransferMode mode = TransferMode.Copy,
        string? replaces = null) =>
        new(source, Library, relative, "mp3", mode, replaces);

    [Fact]
    public async Task Move_places_the_file_and_consumes_the_source()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.ModeUsed.Should().Be(TransferMode.Move);
        result.FinalPath.Should().Be(_temp.Full("library/Artist/Album/track.mp3"));
        File.ReadAllText(result.FinalPath!).Should().Be("audio");
        File.Exists(source).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_places_the_file_and_keeps_the_source_intact()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.ModeUsed.Should().Be(TransferMode.Copy);
        File.ReadAllText(result.FinalPath!).Should().Be("audio");
        File.ReadAllText(source).Should().Be("audio");
        _temp.Files().Should().NotContain(path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HardLinkOrCopy_leaves_the_source_and_a_second_path_to_its_blocks()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.HardLinkOrCopy),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.ModeUsed.Should().Be(TransferMode.HardLinkOrCopy);
        File.ReadAllText(result.FinalPath!).Should().Be("audio");
        File.ReadAllText(source).Should().Be("audio");

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Nothing here can tell a link from a copy, so only the copy's outcome is asserted; the
            // DiskOperations test covers the link itself.
            return;
        }

        File.WriteAllText(source, "changed");

        File.ReadAllText(result.FinalPath!).Should().Be(
            "changed",
            "a hard link into the library is the download's own bytes");
    }

    [Fact]
    public async Task Collision_numbers_the_target_instead_of_overwriting_it()
    {
        _temp.CreateFile("library/Artist/Album/track.mp3", "already here");
        var source = _temp.CreateFile("downloads/track.mp3", "new");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.FinalPath.Should().Be(_temp.Full("library/Artist/Album/track (2).mp3"));
        File.ReadAllText(_temp.Full("library/Artist/Album/track.mp3")).Should().Be("already here");
        File.ReadAllText(result.FinalPath!).Should().Be("new");
    }

    [Fact]
    public async Task Refuses_a_relative_path_that_would_escape_the_library_root()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "../outside", TransferMode.Move),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside the library root");
        File.Exists(_temp.Full("outside.mp3")).Should().BeFalse();
        File.Exists(source).Should().BeTrue("a refused placement leaves the download alone");
    }

    [Fact]
    public async Task Fails_without_touching_anything_when_the_source_is_missing()
    {
        var result = await Placer().PlaceAsync(
            Request(_temp.Full("downloads/gone.mp3"), "Artist/Album/track", TransferMode.Move),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FinalPath.Should().BeNull();
        result.Error.Should().Contain("does not exist");
        Directory.Exists(Library).Should().BeFalse();
    }

    [Fact]
    public async Task Upgrade_recycles_the_replaced_file_under_its_library_layout()
    {
        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "old");
        var source = _temp.CreateFile("downloads/track.mp3", "new");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, replaced),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.RecycledPath.Should().Be(_temp.Full("recycle/Artist/Album/track.mp3"));
        File.ReadAllText(result.RecycledPath!).Should().Be("old");
        File.ReadAllText(result.FinalPath!).Should().Be("new");
    }

    [Fact]
    public async Task Upgrade_recycles_beside_a_file_already_in_the_bin()
    {
        _temp.CreateFile("recycle/Artist/Album/track.mp3", "first");
        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "old");
        var source = _temp.CreateFile("downloads/track.mp3", "new");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, replaced),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.RecycledPath.Should().Be(_temp.Full("recycle/Artist/Album/track (1).mp3"));
        File.ReadAllText(_temp.Full("recycle/Artist/Album/track.mp3")).Should().Be("first");
        File.ReadAllText(result.RecycledPath!).Should().Be("old");
    }

    [Fact]
    public async Task Applies_the_configured_modes_to_the_file_and_the_folders_it_created()
    {
        if (OperatingSystem.IsWindows())
        {
            // Unix modes do not exist here; the placer deliberately does nothing.
            return;
        }

        _options.SetPermissions = true;
        _options.FileMode = "0644";
        _options.FolderMode = "0755";

        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.GetUnixFileMode(result.FinalPath!).Should().Be(ImportOptions.ParseMode("0644"));
        File.GetUnixFileMode(_temp.Full("library/Artist")).Should().Be(ImportOptions.ParseMode("0755"));
        File.GetUnixFileMode(_temp.Full("library/Artist/Album")).Should().Be(ImportOptions.ParseMode("0755"));
    }

    [Fact]
    public async Task Leaves_the_permissions_of_a_pre_existing_folder_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var album = _temp.Full("library/Artist/Album");
        Directory.CreateDirectory(album);
        File.SetUnixFileMode(album, ImportOptions.ParseMode("0700"));

        _options.SetPermissions = true;
        _options.FileMode = "0664";
        _options.FolderMode = "0775";

        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.GetUnixFileMode(album).Should().Be(ImportOptions.ParseMode("0700"));
    }
}