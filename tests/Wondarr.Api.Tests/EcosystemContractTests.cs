using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wondarr.Core.Domain;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The calls Lidarr's ecosystem makes, made exactly as the tools make them (verified from their source
/// on 2026-10-07; ARCHITECTURE §5.6), asserting exactly the fields each one reads: Homepage's Lidarr
/// widget, Unpackerr's queue poll and autobrr's release push. A shape that drifts breaks here first.
/// </summary>
public sealed class EcosystemContractTests
{
    [Fact]
    public async Task Homepage_reads_the_artist_count_the_missing_total_and_the_queue_total_with_the_key_in_the_query()
    {
        using var factory = new WondarrAppFactory();
        using var anonymous = factory.CreateClient();
        await QueueApiTests.SeedSongAsync(factory, "Get Lucky");
        var key = Uri.EscapeDataString(factory.ApiKey);

        // widget.js: GET {url}/api/v1/{endpoint}?apikey={key} — no header.
        var artists = await GetJsonAsync(anonymous, $"/api/v1/artist?apikey={key}");
        artists.ValueKind.Should().Be(JsonValueKind.Array, "component.jsx reads artistsData.length");
        artists.GetArrayLength().Should().Be(1);

        var wanted = await GetJsonAsync(anonymous, $"/api/v1/wanted/missing?apikey={key}");
        wanted.GetProperty("totalRecords").GetInt32().Should().Be(1);

        var queue = await GetJsonAsync(anonymous, $"/api/v1/queue/status?apikey={key}");
        queue.GetProperty("totalCount").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Unpackerr_pages_the_queue_and_reads_lidarrs_record_fields()
    {
        using var factory = new WondarrAppFactory();
        using var client = QueueApiTests.Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");
        var candidateId = await QueueApiTests.SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        await QueueApiTests.SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);

        // golift.io/starr lidarr.GetQueuePageContext: page, pageSize, sortKey=timeleft, includeUnknownArtistItems.
        var page = await GetJsonAsync(client, "/api/v1/queue?page=1&pageSize=10&sortKey=timeleft&includeUnknownArtistItems=true");

        page.GetProperty("totalRecords").GetInt32().Should().Be(1);
        var record = page.GetProperty("records")[0];
        record.GetProperty("title").GetString().Should().Be("Get Lucky.flac");
        record.GetProperty("status").GetString().Should().Be("downloading");
        record.GetProperty("trackedDownloadStatus").GetString().Should().Be("ok");
        record.GetProperty("protocol").GetString().Should().Be("soulseek", "Unpackerr extracts only torrent and usenet downloads");
        record.GetProperty("size").ValueKind.Should().Be(JsonValueKind.Number);
        record.GetProperty("sizeleft").ValueKind.Should().Be(JsonValueKind.Number);
        record.GetProperty("downloadId").ValueKind.Should().Be(JsonValueKind.String);
        record.GetProperty("statusMessages").ValueKind.Should().Be(JsonValueKind.Array);
        record.TryGetProperty("outputPath", out _).Should().BeTrue();
        record.GetProperty("artistId").ValueKind.Should().Be(JsonValueKind.Number);

        var second = await GetJsonAsync(client, "/api/v1/queue?page=2&pageSize=10&sortKey=timeleft&includeUnknownArtistItems=true");
        second.GetProperty("records").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Autobrr_tests_the_connection_through_system_status()
    {
        using var factory = new WondarrAppFactory();
        using var client = QueueApiTests.Authenticated(factory);
        using var anonymous = factory.CreateClient();

        (await GetJsonAsync(client, "/api/v1/system/status")).GetProperty("version").GetString().Should().NotBeNullOrEmpty();

        using var refused = await anonymous.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "autobrr reports 401 as bad credentials");
    }

    [Fact]
    public async Task Autobrr_gets_one_decision_object_naming_the_wanted_song_and_why_it_is_rejected()
    {
        using var factory = new WondarrAppFactory();
        using var client = QueueApiTests.Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/release/push", UriKind.Relative), new
        {
            title = "Daft Punk - Get Lucky [FLAC]",
            downloadUrl = "https://tracker.example/download/123?passkey=secret",
            size = 31_000_000,
            indexer = "example",
            downloadProtocol = "torrent",
            protocol = "torrent",
            publishDate = "2026-10-07T00:00:00Z",
        });
        var decision = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        decision.ValueKind.Should().Be(JsonValueKind.Object, "autobrr decodes a single object, not an array");
        decision.GetProperty("approved").GetBoolean().Should().BeFalse();
        decision.GetProperty("rejected").GetBoolean().Should().BeTrue();
        decision.GetProperty("temporarilyRejected").GetBoolean().Should().BeFalse();
        decision.GetProperty("songId").GetInt64().Should().Be(songId);
        var rejections = decision.GetProperty("rejections").EnumerateArray().Select(item => item.GetString()).ToList();
        rejections.Should().ContainSingle().Which.Should().Contain("No download client for protocol 'torrent'");
    }

    [Fact]
    public async Task A_push_for_a_song_nobody_wants_says_so()
    {
        using var factory = new WondarrAppFactory();
        using var client = QueueApiTests.Authenticated(factory);

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/release/push", UriKind.Relative),
            new { title = "Somebody - Something Else", protocol = "usenet" });
        var decision = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        decision.GetProperty("rejected").GetBoolean().Should().BeTrue();
        decision.GetProperty("rejections")[0].GetString().Should().Be("No wanted song matches 'Somebody - Something Else'");
    }

    [Fact]
    public async Task A_push_without_a_title_is_a_400_in_the_array_shape_autobrr_reads()
    {
        using var factory = new WondarrAppFactory();
        using var client = QueueApiTests.Authenticated(factory);

        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/release/push", UriKind.Relative), new { protocol = "torrent" });
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body[0].GetProperty("propertyName").GetString().Should().Be("Title");
        body[0].GetProperty("errorMessage").GetString().Should().Be("Title is required");
        body[0].GetProperty("severity").GetString().Should().Be("error");
    }

    [Fact]
    public async Task The_api_reference_and_its_document_are_served_without_a_key()
    {
        using var factory = new WondarrAppFactory();
        using var anonymous = factory.CreateClient();

        using var page = await anonymous.GetAsync(new Uri("/docs", UriKind.Relative));
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        using var document = await anonymous.GetAsync(new Uri("/docs/v1/openapi.json", UriKind.Relative));
        document.StatusCode.Should().Be(HttpStatusCode.OK);

        // The endpoints it describes still need the key.
        using var songs = await anonymous.GetAsync(new Uri("/api/v1/song", UriKind.Relative));
        songs.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);

        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
}
