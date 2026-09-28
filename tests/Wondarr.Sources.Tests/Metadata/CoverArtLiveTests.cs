using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.ITunes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// Opt-in tests against the real Cover Art Archive, Deezer and iTunes, through the real DI pipeline.
/// They make three requests, spaced by the shipped gates — four seconds at the slowest, well inside
/// what any of the three allows.
/// </summary>
public sealed class CoverArtLiveTests
{
    /// <summary>A Night at the Opera, which has had a front cover for years.</summary>
    private const string NightAtTheOperaReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";

    [LiveFact]
    public async Task The_cover_art_archive_has_a_front_image_for_a_night_at_the_opera()
    {
        using var provider = BuildProvider();

        var url = await provider.GetRequiredService<ICoverArtArchiveClient>()
            .GetReleaseGroupFrontUrlAsync(NightAtTheOperaReleaseGroupId);

        url.Should().Be($"https://coverartarchive.org/release-group/{NightAtTheOperaReleaseGroupId}/front-500");
    }

    [LiveFact]
    public async Task Deezer_knows_bohemian_rhapsody_by_its_isrc()
    {
        using var provider = BuildProvider();

        var track = await provider.GetRequiredService<IDeezerClient>()
            .GetTrackByIsrcAsync("GBUM71029604");

        track.Should().NotBeNull();
        track!.Duration.Should().BeGreaterThan(0);
        track.Id.Should().BeGreaterThan(0);
    }

    [LiveFact]
    public async Task ITunes_finds_bohemian_rhapsody()
    {
        using var provider = BuildProvider();

        var results = await provider.GetRequiredService<IITunesClient>()
            .SearchSongsAsync("queen bohemian rhapsody");

        results.Should().NotBeEmpty();
        results[0].TrackName.Should().NotBeNullOrEmpty();
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
