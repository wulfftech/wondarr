using Wondarr.Core.HealthCheck;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdDownloadFolderHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wondarr-slskd-storage",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_writable_download_folder_is_ok_and_leaves_no_probe_behind()
    {
        var folder = Path.Combine(_root, "downloads", "slskd");
        var options = Options(folder);

        var result = await CheckAsync(options);

        result.Source.Should().Be(SlskdDownloadFolderHealthCheck.CheckName);
        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be($"{folder} is writable");
        result.WikiUrl.Should().BeNull();
        Directory.Exists(folder).Should().BeTrue();
        Directory.GetFiles(folder).Should().BeEmpty("the write probe is deleted again");
    }

    [Fact]
    public async Task A_download_folder_that_cannot_be_created_is_an_error_naming_it()
    {
        // A file stands where the downloads directory should go: Directory.CreateDirectory fails on
        // every platform, which is the same shape as an unwritable /data.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocked");
        await File.WriteAllTextAsync(blocker, string.Empty);
        var folder = Path.Combine(blocker, "downloads");
        var options = Options(folder);

        var result = await CheckAsync(options);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().StartWith($"{folder} is not writable: ");
        result.Message.Should().NotBe($"{folder} is not writable: ");
    }

    [Fact]
    public async Task External_mode_is_ok_because_Wondarr_owns_the_folder()
    {
        var options = Options(Path.Combine(_root, "downloads"));
        options.Mode = SoulseekMode.External;

        var result = await CheckAsync(options);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("External slskd mode (checked in Phase 5)");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    private static SoulseekOptions Options(string downloadsDir) => new() { DownloadsDir = downloadsDir };

    private static Task<HealthCheck> CheckAsync(SoulseekOptions options) =>
        new SlskdDownloadFolderHealthCheck(SlskdTestData.Monitor(options)).CheckAsync(CancellationToken.None);
}

public class SoulseekSharingHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wondarr-slskd-sharing",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sharing_off_is_a_warning_about_being_banned()
    {
        var options = Options(shareLibrary: false, [Path.Combine(_root, "music")]);

        var result = await CheckAsync(options);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be("Sharing is off: Soulseek users often ban peers who share nothing");
    }

    [Fact]
    public async Task Sharing_on_but_nothing_on_disk_is_a_warning_listing_the_folders()
    {
        var missing = Path.Combine(_root, "music");
        var options = Options(shareLibrary: true, [missing, Path.Combine(_root, "music-old")]);

        var result = await CheckAsync(options);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be($"None of the shared folders exist: {missing}, {Path.Combine(_root, "music-old")}");
    }

    [Fact]
    public async Task Sharing_folders_that_exist_is_ok_and_counts_them()
    {
        var music = Path.Combine(_root, "music");
        var musicOld = Path.Combine(_root, "music-old");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(musicOld);
        var options = Options(shareLibrary: true, [music, musicOld, Path.Combine(_root, "gone")]);

        var result = await CheckAsync(options);

        result.Source.Should().Be(SoulseekSharingHealthCheck.CheckName);
        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("Sharing 2 folder(s)");
    }

    [Fact]
    public async Task External_mode_is_ok_because_Wondarr_owns_no_library()
    {
        var options = Options(shareLibrary: false, []);
        options.Mode = SoulseekMode.External;

        var result = await CheckAsync(options);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("External slskd mode (checked in Phase 5)");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    private static SoulseekOptions Options(bool shareLibrary, List<string> sharedFolders) => new()
    {
        ShareLibrary = shareLibrary,
        SharedFolders = sharedFolders,
    };

    private static Task<HealthCheck> CheckAsync(SoulseekOptions options) =>
        new SoulseekSharingHealthCheck(SlskdTestData.Monitor(options)).CheckAsync(CancellationToken.None);
}
