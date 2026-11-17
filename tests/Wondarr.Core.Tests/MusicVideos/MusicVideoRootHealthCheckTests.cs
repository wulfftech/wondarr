using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Configuration;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.HealthCheck;
using HealthReport = Wondarr.Core.HealthCheck.HealthCheck;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Tests.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>The music video root's health check: an error only while the feature is on and the root is not usable.</summary>
public sealed class MusicVideoRootHealthCheckTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly LibraryRoots _roots = new LibraryRoots(new DiskOperations());
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();
    private WondarrDbContext? _context;

    public void Dispose()
    {
        _context?.Dispose();
        _database.Dispose();
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task While_the_feature_is_off_nothing_is_reported_even_for_a_missing_root()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = false, RootPath = _temp.Full("missing") });

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task While_the_feature_is_off_the_disk_is_not_even_read()
    {
        var roots = Substitute.For<ILibraryRoots>();
        var check = Check(new MusicVideoOptions { Enabled = false, RootPath = "/x" }, roots);

        await check.CheckAsync(CancellationToken.None);

        await roots.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
    }

    [Fact]
    public async Task An_enabled_feature_with_no_root_is_an_error()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = true });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("no video folder is set");
    }

    [Fact]
    public async Task An_enabled_feature_with_a_missing_root_is_an_error()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = _temp.Full("missing") });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task An_enabled_feature_with_an_unmarked_root_is_an_error()
    {
        Directory.CreateDirectory(_temp.Full("videos"));

        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = _temp.Full("videos") });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain(LibraryRoots.MarkerFileName);
    }

    [Fact]
    public async Task An_enabled_feature_with_a_marked_root_is_fine()
    {
        _roots.Mark(_temp.Full("videos"));

        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = _temp.Full("videos") });

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task An_enabled_feature_with_a_root_that_does_not_answer_is_an_error()
    {
        var roots = Substitute.For<ILibraryRoots>();
        roots.CheckAsync("/mnt/hung", Arg.Any<CancellationToken>()).Returns(LibraryRootStatus.Unresponsive);
        var check = Check(new MusicVideoOptions { Enabled = true, RootPath = "/mnt/hung" }, roots);

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("5 seconds");
    }

    [Fact]
    public async Task A_root_set_through_config_overlapping_a_library_is_an_error_naming_the_problem()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = "/data/music/videos" });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("overlaps the library 'Music'");
    }

    [Fact]
    public async Task A_relative_root_set_through_config_is_an_error_naming_the_problem()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = "videos" });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("absolute");
    }

    [Fact]
    public async Task A_root_inside_the_data_directory_is_an_error_naming_the_problem()
    {
        var result = await CheckAsync(new MusicVideoOptions { Enabled = true, RootPath = "/config/videos" });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("data directory");
    }

    private MusicVideoRootHealthCheck Check(MusicVideoOptions options, ILibraryRoots roots)
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        return new MusicVideoRootHealthCheck(
            new TestOptionsMonitor<MusicVideoOptions>(options),
            roots,
            _context ??= _database.CreateContext(_time),
            new WondarrPaths("/config"),
            new TestOptionsMonitor<ImportOptions>(new ImportOptions()));
    }

    private Task<HealthReport> CheckAsync(MusicVideoOptions options) =>
        Check(options, _roots).CheckAsync(CancellationToken.None);
}
