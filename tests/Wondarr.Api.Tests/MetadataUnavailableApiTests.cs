using System.Net;
using System.Text.Json;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// A busy MusicBrainz is not a server error: the lookups answer with what the other provider found
/// and say so in <c>X-Wondarr-Partial</c>, and when nobody answers it is a 503 problem with a plain
/// detail and a <c>Retry-After</c>, never a 500.
/// </summary>
public sealed class MetadataUnavailableApiTests
{
    private const string SongLookup = "/api/v1/song/lookup";
    private const string AlbumLookup = "/api/v1/album/lookup?term=queen";
    private const string PartialHeader = "X-Wondarr-Partial";

    [Fact]
    public async Task A_song_lookup_with_musicbrainz_down_returns_the_list_and_the_partial_header()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.SearchPartialAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PartialSearch<SongCandidate>(
                [new SongCandidate { Source = "deezer", DeezerId = 67238735, Title = "Get Lucky", ArtistCredit = "Daft Punk", Score = 80 }],
                ["musicbrainz"]));

        using var factory = SongApiTests.FakeProviders(resolver: resolver);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(SongLookup, UriKind.Relative), SongApiTests.Json("""{"term":"Daft Punk - Get Lucky"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(PartialHeader).Should().Equal("musicbrainz");

        var body = await SongApiTests.ReadJsonAsync(response);
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_complete_song_lookup_carries_no_partial_header()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.SearchPartialAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PartialSearch<SongCandidate>([], []));

        using var factory = SongApiTests.FakeProviders(resolver: resolver);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(SongLookup, UriKind.Relative), SongApiTests.Json("""{"term":"Daft Punk - Get Lucky"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(PartialHeader).Should().BeFalse();
    }

    [Fact]
    public async Task A_song_lookup_where_every_provider_fails_is_a_503_problem_with_retry_after()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.SearchPartialAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<PartialSearch<SongCandidate>>(_ => throw new ProvidersUnavailableException(["musicbrainz", "deezer"], null));

        using var factory = SongApiTests.FakeProviders(resolver: resolver);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(SongLookup, UriKind.Relative), SongApiTests.Json("""{"term":"Daft Punk - Get Lucky"}"""));

        await AssertUnavailableAsync(
            response,
            "Song search is unavailable",
            "MusicBrainz and Deezer did not answer; try again in a minute.");
    }

    [Fact]
    public async Task A_song_lookup_names_only_the_providers_that_failed()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.SearchPartialAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<PartialSearch<SongCandidate>>(_ => throw new ProvidersUnavailableException(["deezer"], null));

        using var factory = SongApiTests.FakeProviders(resolver: resolver);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(SongLookup, UriKind.Relative), SongApiTests.Json("""{"term":"Daft Punk - Get Lucky"}"""));

        await AssertUnavailableAsync(response, "Song search is unavailable", "Deezer did not answer; try again in a minute.");
    }

    [Fact]
    public async Task The_retry_after_is_what_the_provider_asked_for_capped_at_five_minutes()
    {
        var asked = await ReleasesStatusAsync(new MetadataProviderException(
            "musicbrainz", HttpStatusCode.TooManyRequests, "MusicBrainz answered 429.", TimeSpan.FromSeconds(120)));
        asked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        asked.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(120));

        var tooLong = await ReleasesStatusAsync(new MetadataProviderException(
            "musicbrainz", HttpStatusCode.ServiceUnavailable, "MusicBrainz answered 503.", TimeSpan.FromHours(2)));
        tooLong.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task A_provider_that_rejects_the_request_is_a_502_naming_it()
    {
        using var response = await ReleasesStatusAsync(new MetadataProviderException(
            "musicbrainz", HttpStatusCode.Forbidden, "MusicBrainz answered 403 for a GET request."));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Headers.RetryAfter.Should().BeNull();

        var problem = await SongApiTests.ReadJsonAsync(response);
        problem.GetProperty("detail").GetString().Should().Contain("MusicBrainz rejected the request");
    }

    [Fact]
    public async Task No_connection_to_a_provider_is_a_503()
    {
        using var response = await ReleasesStatusAsync(new HttpRequestException("connection refused"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
    }

    private static async Task<HttpResponseMessage> ReleasesStatusAsync(Exception failure)
    {
        using var factory = SongApiTests.FakeProviders();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>()
                .GetReleasesForReleaseGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<MbRelease>>(_ => throw failure);
        }

        using var client = SongApiTests.Authenticated(factory);

        return await client.GetAsync(
            new Uri("/api/v1/album/releasegroup/6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd/releases", UriKind.Relative));
    }

    [Fact]
    public async Task An_album_lookup_with_musicbrainz_down_returns_deezers_albums_and_the_partial_header()
    {
        using var factory = SongApiTests.FakeProviders();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>()
                .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<MbReleaseGroupSearchResult>(_ => throw Busy());
            scope.ServiceProvider.GetRequiredService<IDeezerClient>()
                .SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new DeezerAlbumSearchResult
                {
                    Data = [new DeezerAlbum { Id = 1007321681, Title = "A Night at the Opera", RecordType = "album" }],
                });
        }

        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri(AlbumLookup, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(PartialHeader).Should().Equal("musicbrainz");

        var hits = await SongApiTests.ReadJsonAsync(response);
        hits.GetArrayLength().Should().Be(1);
        hits[0].GetProperty("source").GetString().Should().Be("deezer");
    }

    [Fact]
    public async Task An_album_lookup_where_both_providers_fail_is_a_503_problem()
    {
        using var factory = SongApiTests.FakeProviders();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>()
                .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<MbReleaseGroupSearchResult>(_ => throw Busy());
            scope.ServiceProvider.GetRequiredService<IDeezerClient>()
                .SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<DeezerAlbumSearchResult>(_ => throw new MetadataProviderException("deezer", HttpStatusCode.BadGateway, "Deezer answered 502."));
        }

        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri(AlbumLookup, UriKind.Relative));

        await AssertUnavailableAsync(
            response,
            "Album search is unavailable",
            "MusicBrainz and Deezer did not answer; try again in a minute.");
    }

    [Fact]
    public async Task Any_other_album_endpoint_maps_a_provider_failure_to_a_503_instead_of_a_500()
    {
        using var factory = SongApiTests.FakeProviders();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>()
                .GetReleasesForReleaseGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<MbRelease>>(_ => throw Busy());
        }

        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri("/api/v1/album/releasegroup/6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd/releases", UriKind.Relative));

        await AssertUnavailableAsync(
            response,
            "A metadata provider is unavailable",
            "MusicBrainz did not answer; try again in a minute.");
    }

    private static MetadataProviderException Busy() =>
        new("musicbrainz", HttpStatusCode.ServiceUnavailable, "MusicBrainz answered 503 for a GET request.");

    private static async Task AssertUnavailableAsync(HttpResponseMessage response, string title, string detail)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));

        var problem = await SongApiTests.ReadJsonAsync(response);
        problem.GetProperty("title").GetString().Should().Be(title);
        problem.GetProperty("detail").GetString().Should().Be(detail);
        problem.GetProperty("status").GetInt32().Should().Be(503);
    }
}
