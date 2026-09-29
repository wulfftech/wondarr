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

    private FilePlacer Placer(IDiskOperations? disk = null)
    {
        disk ??= new DiskOperations();
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

    [Fact]
    public async Task Leaves_a_hard_link_s_mode_alone_and_still_sets_the_folders()
    {
        if (OperatingSystem.IsWindows())
        {
            // Unix modes do not exist here; the placer deliberately does nothing.
            return;
        }

        var source = _temp.CreateFile("downloads/track.mp3", "audio");
        File.SetUnixFileMode(source, ImportOptions.ParseMode("0600"));

        _options.SetPermissions = true;
        _options.FileMode = "0644";
        _options.FolderMode = "0755";

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.HardLinkOrCopy),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);

        // A hard link is the download's own inode: a mode set on it would be set on the download too,
        // so the placer leaves the file alone and only takes charge of the folders it made.
        var linked = new DiskOperations().AreSameFile(source, result.FinalPath!);

        File.GetUnixFileMode(source).Should().Be(
            ImportOptions.ParseMode("0600"),
            linked ? "the library path is the download's own file" : "the download was never the target");

        File.GetUnixFileMode(_temp.Full("library/Artist")).Should().Be(ImportOptions.ParseMode("0755"));
    }

    [Fact]
    public async Task Fails_without_touching_anything_when_the_file_to_replace_is_the_source()
    {
        // The file to replace is the download itself: recycling it would lose what is being placed.
        var source = _temp.CreateFile("library/Downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, source),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.RecycledPath.Should().BeNull();
        result.Error.Should().Contain("not a file to replace");
        File.ReadAllText(source).Should().Be("audio");
        File.Exists(_temp.Full("library/Artist/Album/track.mp3")).Should().BeFalse();
    }

    [Fact]
    public async Task Fails_without_touching_anything_when_the_file_to_replace_is_outside_the_library()
    {
        var outside = _temp.CreateFile("downloads/old.mp3", "old");
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, outside),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.RecycledPath.Should().BeNull();
        File.ReadAllText(outside).Should().Be("old");
        File.ReadAllText(source).Should().Be("audio");
    }

    [Fact]
    public async Task Puts_the_replaced_file_back_when_the_transfer_fails_after_the_recycle()
    {
        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "old");
        var source = _temp.CreateFile("downloads/track.mp3", "new");
        var target = _temp.Full("library/Artist/Album/track.mp3");

        var disk = new FaultyDisk
        {
            FailMoveTarget = path => PathRules.AreEqual(path, target),
            FailCopyTarget = path => PathRules.AreEqual(path, target),
        };

        var result = await Placer(disk).PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, replaced),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.RecycledPath.Should().BeNull("the replaced file went back where it came from");
        File.ReadAllText(replaced).Should().Be("old", "a failed placement changes nothing");
        File.ReadAllText(source).Should().Be("new");
        _temp.Files().Should().NotContain(
            path => PathRules.IsInside(RecycleRoot, path)
                && !string.Equals(Path.GetFileName(path), RecycleBin.MarkerFileName, StringComparison.Ordinal),
            "nothing but the bin's own marker is left in the bin after a rollback");
    }

    [Fact]
    public async Task Reports_the_target_when_the_file_landed_but_the_download_could_not_be_removed()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");
        var target = _temp.Full("library/Artist/Album/track.mp3");

        // The move fails as a cross-device move does, the copy succeeds, deleting the source does not.
        var disk = new FaultyDisk
        {
            FailMoveTarget = path => PathRules.AreEqual(path, target),
            FailDelete = path => PathRules.AreEqual(path, source),
        };

        var result = await Placer(disk).PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FinalPath.Should().Be(target, "the caller has to know the library already holds it");
        File.ReadAllText(target).Should().Be("audio");
        File.ReadAllText(source).Should().Be("audio");
    }

    [Fact]
    public async Task Placing_a_file_the_library_already_holds_does_nothing()
    {
        var source = _temp.CreateFile("library/Artist/Album/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Copy),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.FinalPath.Should().Be(source);
        File.ReadAllText(source).Should().Be("audio");
        _temp.Files().Should().HaveCount(1, "nothing was copied, numbered or recycled");
    }

    [Fact]
    public async Task Refuses_a_target_that_is_the_library_root_itself()
    {
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            new PlacementRequest(source, Library, string.Empty, string.Empty, TransferMode.Move, null),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside the library root");
        File.ReadAllText(source).Should().Be("audio");
    }

    [Fact]
    public async Task Refuses_a_relative_path_that_would_land_in_a_sibling_of_the_library()
    {
        Directory.CreateDirectory(_temp.Full("library2"));
        var source = _temp.CreateFile("downloads/track.mp3", "audio");

        var result = await Placer().PlaceAsync(
            Request(source, "../library2/track", TransferMode.Move),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        File.Exists(_temp.Full("library2/track.mp3")).Should().BeFalse();
        File.ReadAllText(source).Should().Be("audio");
    }

    [Fact]
    public async Task Fails_without_touching_anything_when_the_recycle_bin_cannot_be_used()
    {
        // A bin inside the library would have Wondarr recycle a file into the tree it is being moved
        // within, and clean the library later: the placer reports it instead of moving anything.
        _options.RecycleBinPath = _temp.Full("library/recycle");

        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "old");
        var source = _temp.CreateFile("downloads/track.mp3", "new");

        var result = await Placer().PlaceAsync(
            Request(source, "Artist/Album/track", TransferMode.Move, replaced),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FinalPath.Should().BeNull();
        result.RecycledPath.Should().BeNull();
        result.Error.Should().Contain("outside the library");
        File.ReadAllText(replaced).Should().Be("old");
        File.ReadAllText(source).Should().Be("new");
        Directory.Exists(_temp.Full("library/recycle")).Should().BeFalse();
    }

    private string RecycleRoot => _temp.Full("recycle");

    /// <summary>
    /// A disk that can be told to fail one call, so the placer's fallbacks and its rollback are
    /// reachable from a test. Everything else is the real file system.
    /// </summary>
    private sealed class FaultyDisk : IDiskOperations
    {
        private readonly DiskOperations _inner = new();
        private bool _moveFailed;

        /// <summary>Fails the first move whose target this matches, as a cross-device move would.</summary>
        public Func<string, bool>? FailMoveTarget { get; init; }

        /// <summary>Fails every copy whose target this matches.</summary>
        public Func<string, bool>? FailCopyTarget { get; init; }

        /// <summary>Fails every delete whose path this matches.</summary>
        public Func<string, bool>? FailDelete { get; init; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateEmptyFile(string path) => _inner.CreateEmptyFile(path);

        public long GetFileSize(string path) => _inner.GetFileSize(path);

        public void MoveFile(string source, string target)
        {
            if (!_moveFailed && FailMoveTarget?.Invoke(target) == true)
            {
                // Once only: the rollback that follows a failure is a move to the same folder.
                _moveFailed = true;

                throw new IOException($"Simulated a failed move to '{target}'.");
            }

            _inner.MoveFile(source, target);
        }

        public void CopyFile(string source, string target)
        {
            if (FailCopyTarget?.Invoke(target) == true)
            {
                throw new IOException($"Simulated a failed copy to '{target}'.");
            }

            _inner.CopyFile(source, target);
        }

        public bool TryCreateHardLink(string source, string target) => _inner.TryCreateHardLink(source, target);

        public void DeleteFile(string path)
        {
            if (FailDelete?.Invoke(path) == true)
            {
                throw new IOException($"Simulated a failed delete of '{path}'.");
            }

            _inner.DeleteFile(path);
        }

        public void SetUnixFileMode(string path, UnixFileMode mode) => _inner.SetUnixFileMode(path, mode);

        public bool AreSameFile(string first, string second) => _inner.AreSameFile(first, second);

        public IEnumerable<string> EnumerateFiles(string directory) => _inner.EnumerateFiles(directory);

        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime utc) => _inner.SetLastWriteTimeUtc(path, utc);

        public void DeleteEmptyDirectory(string path) => _inner.DeleteEmptyDirectory(path);
    }
}