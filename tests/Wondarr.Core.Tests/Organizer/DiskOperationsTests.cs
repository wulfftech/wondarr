using Wondarr.Core.Organizer;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class DiskOperationsTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly DiskOperations _disk = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MoveFile_puts_the_file_at_the_target_and_drops_the_source()
    {
        var source = _temp.CreateFile("source.mp3", "one");
        var target = _temp.Full("placed.mp3");

        _disk.MoveFile(source, target);

        _disk.FileExists(target).Should().BeTrue();
        _disk.FileExists(source).Should().BeFalse();
        File.ReadAllText(target).Should().Be("one");
    }

    [Fact]
    public void MoveFile_never_overwrites_an_existing_target()
    {
        var source = _temp.CreateFile("source.mp3", "source");
        var target = _temp.CreateFile("placed.mp3", "existing");

        var move = () => _disk.MoveFile(source, target);

        move.Should().Throw<IOException>();
        File.ReadAllText(target).Should().Be("existing");
        File.ReadAllText(source).Should().Be("source");
    }

    [Fact]
    public void CopyFile_leaves_the_source_and_no_partial_behind()
    {
        var source = _temp.CreateFile("source.mp3", "one");
        var target = _temp.Full("placed.mp3");

        _disk.CopyFile(source, target);

        File.ReadAllText(source).Should().Be("one");
        File.ReadAllText(target).Should().Be("one");
        _temp.Files().Should().NotContain(path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void TryCreateHardLink_gives_a_second_path_to_the_same_bytes()
    {
        var source = _temp.CreateFile("source.mp3", "one");
        var link = _temp.Full("link.mp3");

        if (!_disk.TryCreateHardLink(source, link))
        {
            // The file system said no (another volume, a share, a platform without the call): the
            // placer falls back to copying, which the FilePlacer tests cover.
            _disk.FileExists(link).Should().BeFalse();
            return;
        }

        _disk.FileExists(link).Should().BeTrue();

        File.WriteAllText(source, "two");

        File.ReadAllText(link).Should().Be("two", "a hard link is the same file, not a copy of it");
    }

    [Fact]
    public void AreSameFile_reports_a_hard_link_as_one_file_where_the_platform_can_tell()
    {
        var source = _temp.CreateFile("source.mp3", "one");
        var copy = _temp.CreateFile("copy.mp3", "one");

        _disk.AreSameFile(source, source).Should().BeTrue();
        _disk.AreSameFile(source, copy).Should().BeFalse("equal content is not the same file");

        if (!OperatingSystem.IsLinux())
        {
            // Elsewhere only the full path decides, as the interface documents: two hard links at
            // two paths read as two files there, which at worst costs a collision suffix.
            return;
        }

        var link = _temp.Full("link.mp3");

        if (_disk.TryCreateHardLink(source, link))
        {
            _disk.AreSameFile(source, link).Should().BeTrue();
        }
    }

    [Fact]
    public void DeleteEmptyDirectory_removes_only_the_empty_ones()
    {
        _temp.CreateFile("full/track.mp3");
        Directory.CreateDirectory(_temp.Full("empty/nested"));

        _disk.DeleteEmptyDirectory(_temp.Full("empty/nested"));
        _disk.DeleteEmptyDirectory(_temp.Full("empty"));
        _disk.DeleteEmptyDirectory(_temp.Full("full"));

        _disk.DirectoryExists(_temp.Full("empty")).Should().BeFalse();
        _disk.DirectoryExists(_temp.Full("full")).Should().BeTrue();
    }

    [Fact]
    public void Reads_the_size_and_the_write_time_of_a_file()
    {
        var file = _temp.CreateFile("track.mp3", "12345");

        _disk.GetFileSize(file).Should().Be(5);

        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _disk.SetLastWriteTimeUtc(file, stamp);

        _disk.GetLastWriteTimeUtc(file).Should().BeCloseTo(stamp, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Enumerates_every_file_under_a_directory()
    {
        _temp.CreateFile("album/track.mp3");
        _temp.CreateFile("album/disc/track.mp3");
        _temp.CreateFile("other.mp3");

        _disk.EnumerateFiles(_temp.Root)
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo("track.mp3", "track.mp3", "other.mp3");
    }
}

/// <summary>
/// A unique temporary directory for one test, deleted when the test ends. The assertion in
/// <see cref="Dispose"/> is the point: no test in this folder may leave anything behind, inside or
/// outside its root.
/// </summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "wondarr-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The absolute path of something inside the root.</summary>
    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates a file inside the root, with its directories.</summary>
    public string CreateFile(string relative, string contents = "content")
    {
        var path = Full(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);

        return path;
    }

    /// <summary>Every file under the root, recursively.</summary>
    public IReadOnlyList<string> Files() =>
        Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            : [];

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        Directory.Exists(Root).Should().BeFalse("a test must not leave its temporary files behind");

        GC.SuppressFinalize(this);
    }
}

/// <summary>The options monitor the tests hand to a service: one value, no reloading.</summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
