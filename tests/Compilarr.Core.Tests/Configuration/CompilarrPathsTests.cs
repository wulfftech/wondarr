using System.Collections;
using Compilarr.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Compilarr.Core.Tests.Configuration;

public class CompilarrPathsTests
{
    [Fact]
    public void The_config_dir_setting_wins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("ConfigDir", "/srv/from-setting")])
            .Build();
        var environment = new Hashtable { ["COMPILARR_CONFIG_DIR"] = "/srv/from-environment" };

        var paths = CompilarrPaths.Resolve(configuration, environment);

        paths.ConfigDir.Should().Be(Path.GetFullPath("/srv/from-setting"));
    }

    [Fact]
    public void The_environment_variable_is_used_when_no_setting_is_present()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new Hashtable { ["COMPILARR_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "compilarr-resolved") };

        var paths = CompilarrPaths.Resolve(configuration, environment);

        paths.ConfigDir.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "compilarr-resolved")));
    }

    [Fact]
    public void It_falls_back_to_the_platform_default()
    {
        var paths = CompilarrPaths.Resolve(new ConfigurationBuilder().Build(), new Hashtable());

        var expected = OperatingSystem.IsLinux()
            ? "/config"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compilarr");

        paths.ConfigDir.Should().Be(Path.GetFullPath(expected));
        Path.IsPathFullyQualified(paths.ConfigDir).Should().BeTrue();
    }

    [Fact]
    public void Every_path_hangs_off_the_config_dir()
    {
        var paths = new CompilarrPaths(Path.Combine(Path.GetTempPath(), "compilarr-paths"));

        paths.ConfigFile.Should().Be(Path.Combine(paths.ConfigDir, "config.yml"));
        paths.DatabaseFile.Should().Be(Path.Combine(paths.ConfigDir, "compilarr.db"));
        paths.LogsDir.Should().Be(Path.Combine(paths.ConfigDir, "logs"));
        paths.SlskdDir.Should().Be(Path.Combine(paths.ConfigDir, "slskd"));
        paths.BackupsDir.Should().Be(Path.Combine(paths.ConfigDir, "backups"));
    }
}
