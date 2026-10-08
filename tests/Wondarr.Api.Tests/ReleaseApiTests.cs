using System.Net;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The interactive search over HTTP: a real search run over a faked source, so every candidate comes
/// back with the id the manual grab takes, and the grab writes a real queue item.
/// </summary>
public sealed class ReleaseApiTests
{
    private const string ReleaseEndpoint = "/api/v1/release";

    /// <summary>The candidate the source returns as a clean, matching file.</summary>
    private const string GoodName = "Daft Punk - Get Lucky.flac";

    /// <summary>A candidate whose duration is nowhere near the song's.</summary>
    private const string WrongLengthName = "Daft Punk - Get Lucky (edit).flac";

    [Fact]
    public async Task The_interactive_search_returns_every_candidate_with_its_score_and_rejections()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");

        using var response = await client.GetAsync(
            new Uri($"{ReleaseEndpoint}?songId={songId}", UriKind.Relative));
        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("searchRunId").GetInt64().Should().BeGreaterThan(0);
        body.GetProperty("message").ValueKind.Should().NotBe(JsonValueKind.Undefined);

        var releases = body.GetProperty("releases");
        releases.GetArrayLength().Should().Be(2);

        // Best first, as the engine judged them.
        releases[0].GetProperty("score").GetInt32()
            .Should().BeGreaterThanOrEqualTo(releases[1].GetProperty("score").GetInt32());

        var rejected = ByName(releases, WrongLengthName);
        rejected.GetProperty("accepted").GetBoolean().Should().BeFalse();
        var reasons = rejected.GetProperty("rejections").EnumerateArray()
            .Select(rejection => rejection.GetProperty("reason").GetString())
            .ToList();

        reasons.Should().Contain("durationOutOfTolerance");
        rejected.GetProperty("rejections")[0].GetProperty("message").GetString().Should().NotBeNullOrEmpty();

        foreach (var release in releases.EnumerateArray())
        {
            release.GetProperty("candidateId").GetInt64().Should().BeGreaterThan(0);
            release.GetProperty("sourceType").GetString().Should().Be(SourceTypes.Soulseek);
            release.GetProperty("qualityName").GetString().Should().NotBeNullOrEmpty();
            release.GetProperty("sizeBytes").GetInt64().Should().Be(42_000_000);

            var breakdown = release.GetProperty("scoreBreakdown");
            breakdown.GetProperty("total").GetInt32()
                .Should().Be(release.GetProperty("score").GetInt32());

            release.GetProperty("parsed").GetProperty("artist").GetString().Should().Be("Daft Punk");
        }
    }

    [Fact]
    public async Task Searching_for_an_unknown_song_is_a_not_found()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{ReleaseEndpoint}?songId=987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Grabbing_a_candidate_from_the_search_creates_a_queue_item()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");
        var candidateId = await GoodCandidateIdAsync(client, songId);

        using var response = await client.PostAsync(
            new Uri(ReleaseEndpoint, UriKind.Relative),
            Json($"{{\"candidateId\": {candidateId}}}"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var queueItemId = (await ReadJsonAsync(response)).GetProperty("queueItemId").GetInt64();
        queueItemId.Should().BeGreaterThan(0);

        var item = await ReadQueueItemAsync(factory, queueItemId);
        item.Should().NotBeNull();
        item!.SongId.Should().Be(songId);
        item.CandidateId.Should().Be(candidateId);
        item.State.Should().Be(QueueItemState.Queued);
    }

    [Fact]
    public async Task Grabbing_a_second_candidate_for_the_same_song_is_a_conflict()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");
        var candidateId = await GoodCandidateIdAsync(client, songId);

        using var first = await client.PostAsync(
            new Uri(ReleaseEndpoint, UriKind.Relative),
            Json($"{{\"candidateId\": {candidateId}}}"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        using var second = await client.PostAsync(
            new Uri(ReleaseEndpoint, UriKind.Relative),
            Json($"{{\"candidateId\": {candidateId}}}"));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(second)).GetProperty("detail").GetString().Should().Be("Song is already downloading");
    }

    [Fact]
    public async Task Grabbing_an_unknown_candidate_is_a_not_found()
    {
        using var factory = Factory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(ReleaseEndpoint, UriKind.Relative),
            Json("{\"candidateId\": 987654}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_release_endpoints_require_a_key()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var search = await client.GetAsync(new Uri($"{ReleaseEndpoint}?songId=1", UriKind.Relative));
        using var grab = await client.PostAsync(
            new Uri(ReleaseEndpoint, UriKind.Relative),
            Json("{\"candidateId\": 1}"));

        search.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        grab.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The candidate id of <see cref="GoodName"/> in the search's own answer.</summary>
    private static async Task<long> GoodCandidateIdAsync(HttpClient client, long songId)
    {
        using var response = await client.GetAsync(new Uri($"{ReleaseEndpoint}?songId={songId}", UriKind.Relative));
        var releases = (await ReadJsonAsync(response)).GetProperty("releases");

        return ByName(releases, GoodName).GetProperty("candidateId").GetInt64();
    }

    private static JsonElement ByName(JsonElement releases, string displayName) =>
        releases.EnumerateArray().First(release =>
            release.GetProperty("displayName").GetString() == displayName);

    /// <summary>A factory whose only source is a scripted one, so no test reaches the network.</summary>
    private static WondarrAppFactory Factory()
    {
        var source = Substitute.For<ISourceProvider>();
        source.SourceType.Returns(SourceTypes.Soulseek);
        source.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns((true, (string?)null));
        source.SearchAsync(Arg.Any<SongSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SourceSearchResult(
                [Good(), WrongLength()],
                ["Daft Punk Get Lucky"])));
        source.GrabAsync(Arg.Any<Candidate>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GrabHandle(SourceTypes.Soulseek, "{\"transferId\": 1}")));

        // The queue tracker polls in the background; on a loaded machine it can poll before the
        // second grab arrives. The grab stays in flight, so the conflict check always sees it.
        source.GetStatusAsync(Arg.Any<GrabHandle>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DownloadStatus(DownloadState.Downloading, 0.1, 1_000)));

        return new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<ISourceProvider>();
            services.AddSingleton(source);
        });
    }

    /// <summary>A file that matches the seeded song: right title, right artist, right length.</summary>
    private static Candidate Good() => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = "peer:get-lucky.flac",
        DisplayName = GoodName,
        RemotePath = $"@@peer\\Music\\{GoodName}",
        Provider = "peer",
        Extension = "flac",
        DurationMs = 369_000,
        SizeBytes = 42_000_000,
        QualityId = FlacQualityId,
        Availability = new CandidateAvailability(FreeUploadSlot: true, QueueLength: 0, UploadSpeedBytesPerSecond: 500_000),
        Parsed = new ParsedName("Daft Punk", "Get Lucky", "Random Access Memories", 8, VersionFlags.None, [], VersionFlags.None, [], false),
        Query = "Daft Punk Get Lucky",
    };

    /// <summary>A file of the same song that is far too short to be the recording.</summary>
    private static Candidate WrongLength() => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = "peer:get-lucky-edit.flac",
        DisplayName = WrongLengthName,
        RemotePath = $"@@peer\\Music\\{WrongLengthName}",
        Provider = "peer",
        Extension = "flac",
        DurationMs = 90_000,
        SizeBytes = 42_000_000,
        QualityId = FlacQualityId,
        Parsed = new ParsedName("Daft Punk", "Get Lucky (edit)", "Random Access Memories", 8, VersionFlags.None, [], VersionFlags.None, [], false),
        Query = "Daft Punk Get Lucky",
    };

    /// <summary>The quality ladder's FLAC entry.</summary>
    private const long FlacQualityId = 36;

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    private static async Task<QueueItem?> ReadQueueItemAsync(WondarrAppFactory factory, long id)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await context.QueueItems.FirstOrDefaultAsync(item => item.Id == id);
    }
}
