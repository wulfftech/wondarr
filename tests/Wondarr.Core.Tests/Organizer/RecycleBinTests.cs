using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public class RecycleBinTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempRoot _temp = new();
    private readonly ImportOptions _options = new();
    private readonly FakeTimeProvider _time = new(Now);

    public RecycleBinTests()
    {
        _options.RecycleBinPath = _temp.Full("recycle");
        _options.RecycleBinCleanupDays = 7;
    }

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Library => _temp.Full("library");

    private string RecycleRoot => _temp.Full("recycle");

    private RecycleBin Bin()
    {
        var disk = new DiskOperations();

        return new RecycleBin(
            disk,
            new TestOptionsMonitor<ImportOptions>(_options),
            new WondarrPaths(_temp.Full("config")),
            _time,
            NullLogger<RecycleBin>.Instance);
    }

    [Fact]
    public async Task Recycles_a_library_file_under_its_relative_path_and_restamps_it()
    {
        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "old");
        File.SetLastWriteTimeUtc(replaced, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var recycled = await Bin().RecycleAsync(replaced, Library, CancellationToken.None);

        recycled.Should().Be(_temp.Full("recycle/Artist/Album/track.mp3"));
        File.ReadAllText(recycled).Should().Be("old");
        File.Exists(replaced).Should().BeFalse();

        // The cleanup clock starts when the file was recycled, not when it was downloaded.
        File.GetLastWriteTimeUtc(recycled).Should().BeCloseTo(Now.UtcDateTime, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Numbers_a_recycled_file_when_the_bin_already_holds_that_name()
    {
        _temp.CreateFile("recycle/Artist/Album/track.mp3", "first");
        var replaced = _temp.CreateFile("library/Artist/Album/track.mp3", "second");

        var recycled = await Bin().RecycleAsync(replaced, Library, CancellationToken.None);

        recycled.Should().Be(_temp.Full("recycle/Artist/Album/track (1).mp3"));
        File.ReadAllText(_temp.Full("recycle/Artist/Album/track.mp3")).Should().Be("first");
        File.ReadAllText(recycled).Should().Be("second");
    }

    [Fact]
    public async Task Recycles_a_file_from_outside_the_library_under_its_own_name()
    {
        var loose = _temp.CreateFile("downloads/leftover.mp3", "loose");

        var recycled = await Bin().RecycleAsync(loose, Library, CancellationToken.None);

        recycled.Should().Be(_temp.Full("recycle/leftover.mp3"));
        File.ReadAllText(recycled).Should().Be("loose");
        File.Exists(loose).Should().BeFalse();
    }

    [Fact]
    public async Task Cleanup_deletes_only_the_recycled_files_older_than_the_configured_age()
    {
        var old = _temp.CreateFile("recycle/Artist/Album/old.mp3", "old");
        var fresh = _temp.CreateFile("recycle/Artist/Album/fresh.mp3", "fresh");

        File.SetLastWriteTimeUtc(old, Now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(fresh, Now.UtcDateTime.AddDays(-1));

        var deleted = await Bin().CleanupAsync(CancellationToken.None);

        deleted.Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.ReadAllText(fresh).Should().Be("fresh");
        Directory.Exists(_temp.Full("recycle/Artist/Album")).Should().BeTrue("the folder still holds a file");
    }

    [Fact]
    public async Task Cleanup_removes_the_folders_it_empties()
    {
        var old = _temp.CreateFile("recycle/Artist/Album/old.mp3", "old");
        File.SetLastWriteTimeUtc(old, Now.UtcDateTime.AddDays(-30));

        (await Bin().CleanupAsync(CancellationToken.None)).Should().Be(1);

        Directory.Exists(_temp.Full("recycle/Artist/Album")).Should().BeFalse();
        Directory.Exists(_temp.Full("recycle/Artist")).Should().BeFalse();
        Directory.Exists(RecycleRoot).Should().BeTrue("the bin itself stays");
    }

    [Fact]
    public async Task Cleanup_keeps_everything_when_the_age_is_zero()
    {
        _options.RecycleBinCleanupDays = 0;

        var ancient = _temp.CreateFile("recycle/Artist/old.mp3", "old");
        File.SetLastWriteTimeUtc(ancient, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        (await Bin().CleanupAsync(CancellationToken.None)).Should().Be(0);
        File.Exists(ancient).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_is_harmless_when_the_bin_does_not_exist_yet()
    {
        (await Bin().CleanupAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Reads_a_bin_path_with_a_trailing_separator_as_the_same_directory()
    {
        _options.RecycleBinPath = RecycleRoot + Path.DirectorySeparatorChar;

        Bin().Root.Should().Be(RecycleRoot, "the bin root is one place however it is spelled");

        var replaced = _temp.CreateFile("library/Artist/track.mp3", "old");

        var recycled = await Bin().RecycleAsync(replaced, Library, CancellationToken.None);

        recycled.Should().Be(_temp.Full("recycle/Artist/track.mp3"));
        File.ReadAllText(recycled).Should().Be("old");
    }

    [Fact]
    public async Task Refuses_a_bin_that_is_not_outside_the_library()
    {
        _options.RecycleBinPath = _temp.Full("library/recycle");

        var inside = _temp.CreateFile("library/Artist/track.mp3", "old");
        var at_the_root = _temp.CreateFile("library/track.mp3", "old");

        var recycleInside = async () => await Bin().RecycleAsync(inside, Library, CancellationToken.None);
        var recycleAtTheRoot = async () => await Bin().RecycleAsync(at_the_root, Library, CancellationToken.None);

        await recycleInside.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The recycle bin must be outside the library");

        _options.RecycleBinPath = Library;
        await recycleAtTheRoot.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The recycle bin must be outside the library");

        File.ReadAllText(inside).Should().Be("old");
        File.ReadAllText(at_the_root).Should().Be("old");
    }

    [Fact]
    public async Task Cleanup_leaves_an_empty_folder_outside_the_bin_alone()
    {
        var old = _temp.CreateFile("recycle/Artist/old.mp3", "old");
        File.SetLastWriteTimeUtc(old, Now.UtcDateTime.AddDays(-30));

        var outside = _temp.Full("outside/empty");
        Directory.CreateDirectory(outside);

        (await Bin().CleanupAsync(CancellationToken.None)).Should().Be(1);

        Directory.Exists(RecycleRoot).Should().BeTrue("the bin root itself is never removed");
        Directory.Exists(outside).Should().BeTrue("the walk never reaches past the bin");
        Directory.Exists(_temp.Full("outside")).Should().BeTrue();
    }

    [Fact]
    public void The_validator_refuses_a_bin_at_the_root_of_a_filesystem()
    {
        var validator = new ImportOptionsValidator();

        var root = Path.GetPathRoot(Path.GetTempPath())!;

        validator.Validate(null, new ImportOptions { RecycleBinPath = root })
            .Failed.Should().BeTrue("a bin at the root of a volume would make the cleanup walk the whole disk");

        validator.Validate(null, new ImportOptions { RecycleBinPath = root })
            .Failures.Should().Contain(failure => failure.StartsWith("import.recycle_bin_path", StringComparison.Ordinal));

        validator.Validate(null, new ImportOptions { RecycleBinPath = _temp.Full("recycle") })
            .Succeeded.Should().BeTrue();
    }
}