using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;
using Wondarr.Sources.Slskd;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests;

/// <summary>
/// The defaults a moved media root moves (<see cref="WondarrPaths.MediaRootDefaults"/>) against the
/// options' own <c>/data</c> defaults. A new <c>/data</c> default without a line there fails here,
/// rather than as an install with <c>WONDARR_MEDIA_ROOT</c> writing into an unmounted <c>/data</c>.
/// </summary>
public sealed class MediaRootDefaultsTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));

    public MediaRootDefaultsTests() => Directory.CreateDirectory(_configDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }

    [Fact]
    public void Every_data_default_of_the_options_moves_with_the_media_root()
    {
        var expected = new Dictionary<string, string?>();

        foreach (var (section, options) in new (string, object)[]
        {
            ("Soulseek", new SoulseekOptions()),
            ("YouTube", new YouTubeOptions()),
            ("Import", new ImportOptions()),
        })
        {
            foreach (var property in options.GetType().GetProperties().Where(property => property.PropertyType == typeof(string)))
            {
                if (property.GetValue(options) is string value && value.StartsWith("/data/", StringComparison.Ordinal))
                {
                    expected[$"{section}:{property.Name}"] = "/storage" + value["/data".Length..];
                }
            }
        }

        expected.Should().NotBeEmpty();
        Paths().MediaRootDefaults().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void The_file_and_the_environment_still_win_over_a_moved_default()
    {
        File.WriteAllText(Path.Combine(_configDir, "config.yml"), "youtube:\n  downloads_dir: /x/youtube\n");
        var environment = new Hashtable { ["APP__IMPORT__CONTAINER_STAGING_PATH"] = "/y/containers" };

        var configuration = new ConfigurationBuilder()
            .AddWondarrConfiguration(Paths(), environment)
            .Build();

        configuration.GetSection("YouTube").Get<YouTubeOptions>()!.DownloadsDir.Should().Be("/x/youtube");
        configuration.GetSection("Import").Get<ImportOptions>()!.ContainerStagingPath.Should().Be("/y/containers");
        configuration.GetSection("Soulseek").Get<SoulseekOptions>()!.DownloadsDir.Should().Be("/storage/downloads/slskd");
    }

    private WondarrPaths Paths() => new(_configDir) { MediaRoot = "/storage" };
}
