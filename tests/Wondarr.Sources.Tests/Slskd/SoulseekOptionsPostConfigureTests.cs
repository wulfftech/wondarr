using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Sources.Slskd;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The shared-folder default goes through the real YAML provider and the real binder, because the
/// trap it guards against — a bound list being <em>appended</em> to the property's own default —
/// only shows up there.
/// </summary>
public sealed class SoulseekOptionsPostConfigureTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "wondarr-tests", Guid.NewGuid().ToString("N"));

    public SoulseekOptionsPostConfigureTests() => Directory.CreateDirectory(_configDir);

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
    public void A_file_that_never_mentions_the_key_gets_the_default_folder()
    {
        Write("soulseek:\n  username: listener\n");

        Options().SharedFolders.Should().Equal(["/data/music"]);
    }

    [Fact]
    public void A_written_list_replaces_the_default_rather_than_being_appended_to_it()
    {
        Write("soulseek:\n  shared_folders:\n    - /x\n");

        Options().SharedFolders.Should().Equal(["/x"]);
    }

    [Fact]
    public void An_explicitly_empty_list_stays_empty()
    {
        // What the settings page writes when the user shares nothing: the empty sequence plus the
        // marker the writer puts beside it.
        Write("soulseek:\n  shared_folders: []\n  shared_folders_set: true\n");

        Options().SharedFolders.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_list_written_without_the_marker_is_indistinguishable_from_a_missing_key()
    {
        // Documented consequence of how an empty YAML sequence flattens: only the writer's marker
        // makes "share nothing" survive a round trip, so a hand-written `[]` falls back to the
        // default rather than silently sharing nothing.
        Write("soulseek:\n  shared_folders: []\n");

        Options().SharedFolders.Should().Equal(["/data/music"]);
    }

    private void Write(string yaml) => File.WriteAllText(Path.Combine(_configDir, "config.yml"), yaml);

    /// <summary>
    /// Binds through the registration the app itself uses, so the test also proves the
    /// post-configure is wired up in <see cref="ServiceCollectionExtensions.AddWondarrSlskd"/>.
    /// </summary>
    private SoulseekOptions Options()
    {
        var paths = new WondarrPaths(_configDir);

        var configuration = new ConfigurationBuilder()
            .AddWondarrConfiguration(paths, new Hashtable())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // The host registers these; here they are the only two the options pipeline needs.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddWondarrSlskd(configuration);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<SoulseekOptions>>().Value;
    }
}