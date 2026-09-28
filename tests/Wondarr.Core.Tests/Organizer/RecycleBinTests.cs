using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
}