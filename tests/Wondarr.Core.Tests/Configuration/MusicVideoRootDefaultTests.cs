using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Wondarr.Core.Configuration;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

/// <summary>The music video root's default: <c>&lt;media root&gt;/music-videos</c>, below config.yml and the environment.</summary>
public sealed class MusicVideoRootDefaultTests
{
    [Fact]
    public void Without_a_media_root_the_default_is_data_music_videos()
    {
        using var directory = new TemporaryConfigDirectory();

        Bind(directory.Paths, new Hashtable()).RootPath.Should().Be("/data/music-videos");
    }

    [Fact]
    public void The_media_root_variable_moves_the_default()
    {
        using var directory = new TemporaryConfigDirectory();
        var environment = new Hashtable { ["WONDARR_MEDIA_ROOT"] = "/storage" };
        var paths = WondarrPaths.Resolve(
            new ConfigurationBuilder().Build(),
            environment) with { ConfigDir = directory.Paths.ConfigDir };

        Bind(paths, new Hashtable()).RootPath.Should().Be($"{paths.MediaRoot.TrimEnd('/', '\\')}/music-videos");
        paths.DefaultMusicVideoRoot.Should().Be(Bind(paths, new Hashtable()).RootPath);
    }

    [Fact]
    public void Config_yml_wins_over_the_default()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("music_videos:\n  root_path: /mnt/videos\n");

        Bind(directory.Paths, new Hashtable()).RootPath.Should().Be("/mnt/videos");
    }

    [Fact]
    public void The_environment_wins_over_config_yml_and_the_default()
    {
        using var directory = new TemporaryConfigDirectory();
        directory.WriteConfig("music_videos:\n  root_path: /mnt/videos\n");
        var environment = new Hashtable { ["APP__MUSIC_VIDEOS__ROOT_PATH"] = "/mnt/from-env" };

        Bind(directory.Paths, environment).RootPath.Should().Be("/mnt/from-env");
    }

    private static MusicVideoOptions Bind(WondarrPaths paths, Hashtable environment) =>
        new ConfigurationBuilder()
            .AddWondarrConfiguration(paths, environment)
            .Build()
            .GetSection("MusicVideos")
            .Get<MusicVideoOptions>()!;
}
