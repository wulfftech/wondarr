using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Songs;
using Wondarr.Core.Tagging;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Match queue endpoints: the files identification could not settle, and the choices that settle
/// them (LIBRARY_OUTPUT §7.6). The choices themselves are covered by <c>ReferenceMatchServiceTests</c>;
/// here they are checked as the API reports them.
/// </summary>
public sealed class MatchQueueApiTests
{
    private const string Endpoint = "/api/v1/matchqueue";

    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>A recording id the resolver knows, and one it does not.</summary>
    private const string KnownRecordingId = "0a0a0a0a-1111-2222-3333-444444444444";

    [Fact]
    public async Task The_queue_lists_only_the_unsettled_files_of_the_library_with_their_facts()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        using var factory = SongApiTests.FakeProviders(resolver);
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var ambiguous = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await SeedCandidateAsync(factory, ambiguous, rank: 2, "m-b");
        await SeedCandidateAsync(factory, ambiguous, rank: 1, "m-a");
        await SeedFileAsync(factory, libraryId, "b/nothing.flac", ReferenceFileState.Unmatched);
        await SeedFileAsync(factory, libraryId, "c/done.flac", ReferenceFileState.Identified);

        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = (JsonObject)(await ReadJsonAsync(response))!;
        page["page"]!.GetValue<int>().Should().Be(1);
        page["sortKey"]!.GetValue<string>().Should().Be("relativePath");
        page["sortDirection"]!.GetValue<string>().Should().Be("ascending");
        page["totalRecords"]!.GetValue<int>().Should().Be(2);

        var records = (JsonArray)page["records"]!;
        records.Select(record => ((JsonObject)record!)["relativePath"]!.GetValue<string>())
            .Should().Equal("a/Get Lucky.flac", "b/nothing.flac");

        var item = (JsonObject)records[0]!;
        item["id"]!.GetValue<long>().Should().Be(ambiguous);
        item["referenceLibraryId"]!.GetValue<long>().Should().Be(libraryId);
        item["state"]!.GetValue<string>().Should().Be("ambiguous");

        var file = (JsonObject)item["file"]!;
        file["title"]!.GetValue<string>().Should().Be("Get Lucky");
        file["artist"]!.GetValue<string>().Should().Be("Daft Punk");
        file["album"]!.GetValue<string>().Should().Be("Random Access Memories");
        file["trackNumber"]!.GetValue<int>().Should().Be(1);
        file["durationMs"]!.GetValue<int>().Should().Be(248_000);
        file["codec"]!.GetValue<string>().Should().Be("flac");
        file["bitrate"]!.GetValue<int>().Should().Be(900);
        file["isrc"]!.GetValue<string>().Should().Be("USQX91300108");
        file["mbRecordingId"]!.GetValue<string>().Should().Be(KnownRecordingId);

        var candidates = (JsonArray)item["candidates"]!;
        candidates.Select(candidate => ((JsonObject)candidate!)["rank"]!.GetValue<int>())
            .Should().Equal(1, 2);

        var best = (JsonObject)candidates[0]!;
        best["score"]!.GetValue<double>().Should().Be(0.85);
        best["reason"]!.GetValue<string>().Should().Be("search 85");
        best["source"]!.GetValue<string>().Should().Be("musicbrainz");
        best["mbRecordingId"]!.GetValue<string>().Should().Be("m-a", "rank 1 was seeded as m-a");
        best["title"]!.GetValue<string>().Should().Be("Get Lucky");
        best["artistCredit"]!.GetValue<string>().Should().Be("Daft Punk");
        best["durationMs"]!.GetValue<int>().Should().Be(248_000);
        best["albumTitle"]!.GetValue<string>().Should().Be("Random Access Memories");
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_to_one_library()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        var music = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var other = await SeedLibraryAsync(factory, "Other", "/reference/other");
        await SeedFileAsync(factory, music, "a/one.flac", ReferenceFileState.Ambiguous);
        await SeedFileAsync(factory, other, "a/two.flac", ReferenceFileState.Ambiguous);

        using var response = await client.GetAsync(
            new Uri($"{Endpoint}?referenceLibraryId={other}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = (JsonObject)(await ReadJsonAsync(response))!;
        page["totalRecords"]!.GetValue<int>().Should().Be(1);

        var item = (JsonObject)((JsonArray)page["records"]!)[0]!;
        item["referenceLibraryId"]!.GetValue<long>().Should().Be(other);
        item["relativePath"]!.GetValue<string>().Should().Be("a/two.flac");
    }

    [Fact]
    public async Task Reading_the_queue_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accepting_a_candidate_rank_settles_the_file_and_adds_the_song()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(KnownRecordingId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(SongApiTests.Identity()));

        using var factory = SongApiTests.FakeProviders(resolver);
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var file = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        await SeedCandidateAsync(factory, file, rank: 1, KnownRecordingId);

        using var response = await PostAsync(client, $"{Endpoint}/{file}/resolve", """{"candidateRank":1}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)(await ReadJsonAsync(response))!;
        result["state"]!.GetValue<string>().Should().Be("identified");
        result["songId"]!.GetValue<long>().Should().BeGreaterThan(0);

        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var row = await context.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file);
        row.State.Should().Be(ReferenceFileState.Identified);
        row.SongId.Should().Be(result["songId"]!.GetValue<long>());
        row.Confidence.Should().Be(1.0);
        row.IdentifiedBy.Should().Be("manual");
    }

    [Fact]
    public async Task Skipping_a_file_leaves_it_alone_and_reports_it()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var file = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);

        using var response = await PostAsync(client, $"{Endpoint}/{file}/resolve", """{"skip":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)(await ReadJsonAsync(response))!;
        result["state"]!.GetValue<string>().Should().Be("skipped");
        result["songId"].Should().BeNull();

        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var row = await context.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == file);
        row.State.Should().Be(ReferenceFileState.Skipped);
        row.SongId.Should().BeNull();
        (await context.MatchCandidates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Settling_a_file_that_is_not_in_the_queue_is_a_conflict()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var file = await SeedFileAsync(factory, libraryId, "a/pending.flac", ReferenceFileState.Pending);

        using var response = await PostAsync(client, $"{Endpoint}/{file}/resolve", """{"candidateRank":1}""");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Settling_an_unknown_file_is_a_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await PostAsync(client, $"{Endpoint}/987654/resolve", """{"candidateRank":1}""");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_choice_that_names_nothing_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var file = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);

        using var none = await PostAsync(client, $"{Endpoint}/{file}/resolve", "{}");
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var both = await PostAsync(client, $"{Endpoint}/{file}/resolve", """{"candidateRank":1,"skip":true}""");
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_recording_id_that_is_not_a_musicbrainz_id_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var file = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);

        using var response = await PostAsync(
            client,
            $"{Endpoint}/{file}/resolve",
            """{"mbRecordingId":"not-a-guid"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("mbRecordingId");
    }

    [Fact]
    public async Task Accepting_the_best_candidate_of_many_files_settles_them()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(KnownRecordingId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(SongApiTests.Identity()));

        using var factory = SongApiTests.FakeProviders(resolver);
        using var client = SongApiTests.Authenticated(factory);

        var libraryId = await SeedLibraryAsync(factory, "Music", "/reference/music");
        var good = await SeedFileAsync(factory, libraryId, "a/Get Lucky.flac", ReferenceFileState.Ambiguous);
        var empty = await SeedFileAsync(factory, libraryId, "b/nothing.flac", ReferenceFileState.Ambiguous);
        await SeedCandidateAsync(factory, good, rank: 1, KnownRecordingId);

        using var response = await PostAsync(
            client,
            $"{Endpoint}/bulk",
            $$"""{"ids":[{{good}},{{empty}}]}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = (JsonObject)(await ReadJsonAsync(response))!;
        result["resolved"]!.GetValue<int>().Should().Be(1);
        result["failed"]!.GetValue<int>().Should().Be(1);

        var errors = (JsonArray)result["errors"]!;
        errors.Should().ContainSingle();
        errors[0]!.GetValue<string>().Should().StartWith("b/nothing.flac: ");
    }

    [Fact]
    public async Task Accepting_a_bulk_without_ids_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = SongApiTests.Authenticated(factory);

        using var empty = await PostAsync(client, $"{Endpoint}/bulk", """{"ids":[]}""");
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(empty)).Should().ContainKey("ids");

        using var none = await PostAsync(client, $"{Endpoint}/bulk", "{}");
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooMany = string.Concat(
            """{"ids":[""",
            string.Join(',', Enumerable.Range(1, ReferenceMatchService.MaxBulkSize + 1)),
            "]}");

        using var over = await PostAsync(client, $"{Endpoint}/bulk", tooMany);
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(over)).Should().ContainKey("ids");
    }

    // --- Seeding --------------------------------------------------------------------------------

    private static async Task<long> SeedLibraryAsync(WondarrAppFactory factory, string name, string rootPath)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var library = new ReferenceLibrary { Name = name, RootPath = rootPath };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library.Id;
    }

    /// <summary>Seeds one reference file with the tags and probe a scan would have stored.</summary>
    private static async Task<long> SeedFileAsync(
        WondarrAppFactory factory,
        long referenceLibraryId,
        string relativePath,
        ReferenceFileState state)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var file = new ReferenceFile
        {
            ReferenceLibraryId = referenceLibraryId,
            RelativePath = relativePath,
            Size = 25_000_000,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Tags = JsonSerializer.Serialize(
                new FileTags(
                    "Get Lucky",
                    "Daft Punk",
                    "Daft Punk",
                    "Random Access Memories",
                    "2013",
                    "USQX91300108",
                    KnownRecordingId,
                    null,
                    null,
                    null,
                    null,
                    1,
                    13,
                    1,
                    248_000),
                StoredJson),
            Probe = JsonSerializer.Serialize(
                new MediaInfo("flac", "flac", 900, 44_100, 16, 2, 248_000, true, 25_000_000),
                StoredJson),
            State = state,
        };

        context.ReferenceFiles.Add(file);
        await context.SaveChangesAsync();

        return file.Id;
    }

    private static async Task SeedCandidateAsync(
        WondarrAppFactory factory,
        long referenceFileId,
        int rank,
        string mbRecordingId)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        context.MatchCandidates.Add(new MatchCandidate
        {
            ReferenceFileId = referenceFileId,
            Rank = rank,
            Identity = new MatchIdentity(
                "musicbrainz",
                mbRecordingId,
                null,
                "Get Lucky",
                "Daft Punk",
                248_000,
                "Random Access Memories").ToJson(),
            Score = rank == 1 ? 0.85 : 0.6,
            Reason = rank == 1 ? "search 85" : "search 60",
        });

        await context.SaveChangesAsync();
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string body) =>
        client.PostAsync(new Uri(url, UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>Reads the RFC 7807 <c>errors</c> dictionary: property → messages.</summary>
    private static async Task<Dictionary<string, List<string>>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        var problem = (JsonObject)(await ReadJsonAsync(response))!;

        problem["status"]!.GetValue<int>().Should().Be(400);
        problem.Should().ContainKey("errors");

        return ((JsonObject)problem["errors"]!).ToDictionary(
            entry => entry.Key,
            entry => ((JsonArray)entry.Value!).Select(message => message!.GetValue<string>()).ToList());
    }
}
