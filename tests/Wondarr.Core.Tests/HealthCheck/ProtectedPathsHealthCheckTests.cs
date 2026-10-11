using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.HealthCheck;

/// <summary>
/// A configuration written before the rule that keeps folder settings out of Wondarr's data
/// directory still loads; the health check is what names the setting to change.
/// </summary>
public sealed class ProtectedPathsHealthCheckTests : IDisposable
{
    private static readonly WondarrPaths DataDirectory = new(Path.Combine(Path.GetTempPath(), "wondarr-test-config"));

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_clean_configuration_is_ok()
    {
        var result = await CheckAsync(new Dictionary<string, string?>
        {
            ["Soulseek:SharedFolders:0"] = "/data/music",
            ["Soulseek:DownloadsDir"] = "/data/downloads/slskd",
            ["YouTube:DownloadsDir"] = "/data/downloads/youtube",
        });

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task A_shared_folder_in_the_data_directory_is_an_error_naming_the_setting()
    {
        var result = await CheckAsync(new Dictionary<string, string?>
        {
            ["Soulseek:SharedFolders:0"] = "/data/music",
            ["Soulseek:SharedFolders:1"] = DataDirectory.ConfigDir,
        });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().StartWith("soulseek.shared_folders: sharing ").And.Contain("would publish Wondarr's API key and passwords");
    }

    [Fact]
    public async Task A_soulseek_downloads_dir_in_the_data_directory_is_an_error_naming_the_setting()
    {
        var result = await CheckAsync(new Dictionary<string, string?>
        {
            ["Soulseek:DownloadsDir"] = Path.Combine(DataDirectory.ConfigDir, "downloads"),
        });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().StartWith("soulseek.downloads_dir:");
    }

    [Fact]
    public async Task A_youtube_downloads_dir_at_the_file_system_root_is_an_error_naming_the_setting()
    {
        var result = await CheckAsync(new Dictionary<string, string?>
        {
            ["YouTube:DownloadsDir"] = Path.GetPathRoot(DataDirectory.ConfigDir),
        });

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().StartWith("youtube.downloads_dir:");
    }

    [Fact]
    public async Task A_library_and_a_reference_library_root_in_the_data_directory_are_errors_naming_them()
    {
        await _database.MigrateAsync(_time);

        await using (var context = _database.CreateContext(_time))
        {
            var seeded = await context.Libraries.SingleAsync();
            seeded.RootPath = Path.Combine(DataDirectory.ConfigDir, "music");
            context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Mine", RootPath = DataDirectory.ConfigDir });
            await context.SaveChangesAsync();
        }

        var result = await CheckAsync(new Dictionary<string, string?>(), migrate: false);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("library 'Music' root:").And.Contain("reference library 'Mine' root:");
    }

    private async Task<Wondarr.Core.HealthCheck.HealthCheck> CheckAsync(Dictionary<string, string?> settings, bool migrate = true)
    {
        if (migrate)
        {
            await _database.MigrateAsync(_time);
        }

        await using var context = _database.CreateContext(_time);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return await new ProtectedPathsHealthCheck(context, configuration, DataDirectory).CheckAsync(CancellationToken.None);
    }
}
