using System.Collections;
using Wondarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

public class WondarrPathsTests
{
    [Fact]
    public void The_config_dir_setting_wins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("ConfigDir", "/srv/from-setting")])
            .Build();
        var environment = new Hashtable { ["WONDARR_CONFIG_DIR"] = "/srv/from-environment" };

        var paths = WondarrPaths.Resolve(configuration, environment);

        paths.ConfigDir.Should().Be(Path.GetFullPath("/srv/from-setting"));
    }

    [Fact]
    public void The_environment_variable_is_used_when_no_setting_is_present()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new Hashtable { ["WONDARR_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "wondarr-resolved") };

        var paths = WondarrPaths.Resolve(configuration, environment);

        paths.ConfigDir.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wondarr-resolved")));
    }

    [Fact]
    public void It_falls_back_to_the_platform_default()
    {
        var paths = WondarrPaths.Resolve(new ConfigurationBuilder().Build(), new Hashtable());

        var expected = OperatingSystem.IsLinux()
            ? "/config"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wondarr");

        paths.ConfigDir.Should().Be(Path.GetFullPath(expected));
        Path.IsPathFullyQualified(paths.ConfigDir).Should().BeTrue();
    }

    [Fact]
    public void Every_path_hangs_off_the_config_dir()
    {
        var paths = new WondarrPaths(Path.Combine(Path.GetTempPath(), "wondarr-paths"));

        paths.ConfigFile.Should().Be(Path.Combine(paths.ConfigDir, "config.yml"));
        paths.DatabaseFile.Should().Be(Path.Combine(paths.ConfigDir, "wondarr.db"));
        paths.LogsDir.Should().Be(Path.Combine(paths.ConfigDir, "logs"));
        paths.SlskdDir.Should().Be(Path.Combine(paths.ConfigDir, "slskd"));
        paths.BackupsDir.Should().Be(Path.Combine(paths.ConfigDir, "backups"));
    }

    [Fact]
    public void The_pre_rename_environment_variable_still_works()
    {
        var configuration = new ConfigurationBuilder().Build();
        var legacy = Path.Combine(Path.GetTempPath(), "compilarr-legacy-dir");
        var environment = new Hashtable { ["COMPILARR_CONFIG_DIR"] = legacy };

        WondarrPaths.Resolve(configuration, environment).ConfigDir.Should().Be(Path.GetFullPath(legacy));

        environment["WONDARR_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "wondarr-new-dir");
        WondarrPaths.Resolve(configuration, environment).ConfigDir
            .Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wondarr-new-dir")));
    }

    [Fact]
    public void The_media_root_is_data_when_nothing_moves_it()
    {
        var paths = WondarrPaths.Resolve(new ConfigurationBuilder().Build(), new Hashtable());

        paths.MediaRoot.Should().Be("/data");
        paths.DefaultLibraryRoot.Should().Be("/data/music");
        paths.MediaRootDefaults().Should().BeEmpty("the options' own defaults are the /data paths already");
    }

    [Fact]
    public void The_media_root_variable_moves_the_library_and_download_defaults()
    {
        var environment = new Hashtable { ["WONDARR_MEDIA_ROOT"] = "/storage/" };

        var paths = WondarrPaths.Resolve(new ConfigurationBuilder().Build(), environment);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath("/storage"));
        var under = root.TrimEnd('/', '\\');
        paths.MediaRoot.Should().Be(root);
        paths.DefaultLibraryRoot.Should().Be($"{under}/music");
        paths.MediaRootDefaults().Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Soulseek:DownloadsDir"] = $"{under}/downloads/slskd",
            ["Soulseek:IncompleteDir"] = $"{under}/downloads/slskd/incomplete",
            ["YouTube:DownloadsDir"] = $"{under}/downloads/youtube",
            ["Import:ContainerStagingPath"] = $"{under}/downloads/containers",
        });
    }

    [Fact]
    public void The_media_root_setting_wins_over_the_variable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("MediaRoot", "/srv/from-setting")])
            .Build();
        var environment = new Hashtable { ["WONDARR_MEDIA_ROOT"] = "/srv/from-environment" };

        WondarrPaths.Resolve(configuration, environment).MediaRoot
            .Should().Be(Path.GetFullPath("/srv/from-setting"));
    }

    [Fact]
    public void A_compilarr_database_is_adopted_with_its_wal_and_shm_files()
    {
        using var directory = new TemporaryConfigDirectory();
        var paths = directory.Paths;
        File.WriteAllText(paths.LegacyDatabaseFile, "db");
        File.WriteAllText(paths.LegacyDatabaseFile + "-wal", "wal");
        File.WriteAllText(paths.LegacyDatabaseFile + "-shm", "shm");

        paths.AdoptLegacyDatabase().Should().BeTrue();

        File.ReadAllText(paths.DatabaseFile).Should().Be("db");
        File.ReadAllText(paths.DatabaseFile + "-wal").Should().Be("wal");
        File.ReadAllText(paths.DatabaseFile + "-shm").Should().Be("shm");
        File.Exists(paths.LegacyDatabaseFile).Should().BeFalse();
        paths.AdoptLegacyDatabase().Should().BeFalse("a second start has nothing left to adopt");
    }

    [Fact]
    public void An_existing_wondarr_database_is_never_overwritten()
    {
        using var directory = new TemporaryConfigDirectory();
        var paths = directory.Paths;
        File.WriteAllText(paths.DatabaseFile, "current");
        File.WriteAllText(paths.LegacyDatabaseFile, "old");

        paths.AdoptLegacyDatabase().Should().BeFalse();

        File.ReadAllText(paths.DatabaseFile).Should().Be("current");
        File.ReadAllText(paths.LegacyDatabaseFile).Should().Be("old");
    }
}
