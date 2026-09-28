using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// Opt-in tests against the real MusicBrainz, through the real DI pipeline. They make three
/// requests, spaced by the shipped one-second gate — well inside what MusicBrainz allows.
/// </summary>
public sealed class MusicBrainzLiveTests
{
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";
    private const string BohemianRhapsodyIsrc = "GBUM71029604";

    [LiveFact]
    public async Task Isrc_lookup_finds_bohemian_rhapsody()
    {
        using var provider = BuildProvider();

        var recordings = await provider.GetRequiredService<IMusicBrainzClient>()
            .GetRecordingsByIsrcAsync(BohemianRhapsodyIsrc);

        recordings.Should().Contain(recording => recording.Id == BohemianRhapsodyId);
    }

    [LiveFact]
    public async Task Recording_lookup_returns_the_length_the_fixture_recorded()
    {
        using var provider = BuildProvider();

        var recording = await provider.GetRequiredService<IMusicBrainzClient>()
            .GetRecordingAsync(BohemianRhapsodyId);

        recording.Should().NotBeNull();
        recording!.Length.Should().NotBeNull();
        recording.Length!.Value.Should().BeCloseTo(355106, 2000);
    }

    [LiveFact]
    public async Task The_first_browse_page_carries_a_full_hundred_releases()
    {
        using var provider = BuildProvider();

        var releases = await provider.GetRequiredService<IMusicBrainzClient>()
            .GetReleasesForRecordingAsync(BohemianRhapsodyId, maxPages: 1);

        releases.Count.Should().BeGreaterThanOrEqualTo(100);
        releases.Should().OnlyContain(release => release.Status == "Official");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        return services.BuildServiceProvider();
    }
}
