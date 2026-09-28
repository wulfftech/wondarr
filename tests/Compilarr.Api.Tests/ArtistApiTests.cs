using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>The artist list and one artist, with the song count the Library UI shows.</summary>
public sealed class ArtistApiTests
{
    private const string ArtistEndpoint = "/api/v1/artist";

    [Fact]
    public async Task The_artist_list_counts_the_songs_credited_to_each_artist()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        var songId = await SongApiTests.SeedSongAsync(
            factory,
            "Get Lucky",
            "833f00e1-781f-4edd-90e4-e52712618862",
            monitored: true);

        using var listed = await client.GetAsync(new Uri(ArtistEndpoint, UriKind.Relative));
        var artists = await SongApiTests.ReadJsonAsync(listed);

        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        artists.GetArrayLength().Should().Be(1);

        var artist = artists[0];
        var artistId = artist.GetProperty("id").GetInt64();

        artist.GetProperty("name").GetString().Should().Be("Daft Punk");
        artist.GetProperty("sortName").GetString().Should().Be("Daft Punk");
        artist.GetProperty("mbArtistId").ValueKind.Should().Be(JsonValueKind.Null);
        artist.GetProperty("deezerId").ValueKind.Should().Be(JsonValueKind.Null);
        artist.GetProperty("songCount").GetInt32().Should().Be(1);

        using var one = await client.GetAsync(new Uri($"{ArtistEndpoint}/{artistId}", UriKind.Relative));
        var resource = await SongApiTests.ReadJsonAsync(one);

        one.StatusCode.Should().Be(HttpStatusCode.OK);
        resource.GetProperty("id").GetInt64().Should().Be(artistId);
        resource.GetProperty("songCount").GetInt32().Should().Be(1);

        // The song the count came from is really there.
        using var song = await client.GetAsync(new Uri($"/api/v1/song/{songId}", UriKind.Relative));
        song.StatusCode.Should().Be(HttpStatusCode.OK);

        using var missing = await client.GetAsync(new Uri($"{ArtistEndpoint}/{artistId + 1000}", UriKind.Relative));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_artist_endpoint_requires_the_api_key()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = factory.CreateClient();

        using var listed = await client.GetAsync(new Uri(ArtistEndpoint, UriKind.Relative));
        listed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var one = await client.GetAsync(new Uri($"{ArtistEndpoint}/1", UriKind.Relative));
        one.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
