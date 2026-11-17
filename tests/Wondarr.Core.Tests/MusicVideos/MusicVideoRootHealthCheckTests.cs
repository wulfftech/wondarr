using FluentAssertions;
using NSubstitute;
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

    public void Dispose()
    {
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
        var check = new MusicVideoRootHealthCheck(
            new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = false, RootPath = "/x" }),
            roots);

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
        var check = new MusicVideoRootHealthCheck(
            new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = true, RootPath = "/mnt/hung" }),
            roots);

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("5 seconds");
    }

    private Task<HealthReport> CheckAsync(MusicVideoOptions options) =>
        new MusicVideoRootHealthCheck(new TestOptionsMonitor<MusicVideoOptions>(options), _roots)
            .CheckAsync(CancellationToken.None);
}
