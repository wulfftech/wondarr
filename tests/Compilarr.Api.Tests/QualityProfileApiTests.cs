using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// The quality ladder and quality profile endpoints, driven through the real host so the API key
/// policy, the JSON shapes and the RFC 7807 errors are all exercised.
/// </summary>
public sealed class QualityProfileApiTests
{
    private const string DefinitionsEndpoint = "/api/v1/qualitydefinition";
    private const string ProfilesEndpoint = "/api/v1/qualityprofile";

    [Fact]
    public async Task Listing_the_quality_ladder_returns_the_43_seeded_qualities()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri(DefinitionsEndpoint, UriKind.Relative));
        var qualities = (JsonArray)(await ReadJsonAsync(response))!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        qualities.Should().HaveCount(43);

        var opus = (JsonObject)qualities.Single(quality => (long)quality!["id"]! == 28)!;
        opus["name"]!.GetValue<string>().Should().Be("OPUS-160");
        opus["group"]!.GetValue<string>().Should().Be("Mid lossy");
        opus["codec"]!.GetValue<string>().Should().Be("opus");
        opus["lossless"]!.GetValue<bool>().Should().BeFalse();
        opus["minBitrate"]!.GetValue<int>().Should().Be(144);
        opus["maxBitrate"]!.GetValue<int>().Should().Be(176);
        opus["bitDepth"].Should().BeNull();
    }

    [Fact]
    public async Task Reading_the_quality_ladder_without_a_key_is_unauthorized()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(DefinitionsEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Listing_profiles_returns_the_two_seeded_profiles()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri(ProfilesEndpoint, UriKind.Relative));
        var profiles = (JsonArray)(await ReadJsonAsync(response))!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        profiles.Should().HaveCount(2);

        var standard = (JsonObject)profiles.Single(profile => profile!["name"]!.GetValue<string>() == "Standard 320")!;
        standard["id"]!.GetValue<long>().Should().Be(1);
        standard["cutoff"]!.GetValue<long>().Should().Be(29);
        standard["upgradeAllowed"]!.GetValue<bool>().Should().BeTrue();
        standard["durationToleranceMs"]!.GetValue<int>().Should().Be(3000);

        var lossless = (JsonObject)profiles.Single(profile => profile!["name"]!.GetValue<string>() == "Lossless")!;
        lossless["cutoff"]!.GetValue<long>().Should().Be(36);

        // The cutoff group holds MP3-320 and AAC-256 together: a 256 kbps grab counts as met.
        var cutoffItem = ((JsonArray)standard["items"]!)
            .Select(item => (JsonObject)item!)
            .Single(item => ((JsonArray)item["qualities"]!).Any(quality => (long)quality!["id"]! == 29));

        ((JsonArray)cutoffItem["qualities"]!).Select(quality => (long)quality!["id"]!)
            .Should().Contain(25);
        cutoffItem["allowed"]!.GetValue<bool>().Should().BeTrue();
        ((JsonArray)cutoffItem["qualities"]!).Select(quality => quality!["name"]!.GetValue<string>())
            .Should().Contain("AAC-256");
    }

    [Fact]
    public async Task Posting_a_copy_of_a_profile_creates_it_and_deleting_it_removes_it()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var copy = await GetProfileAsync(client, 1);
        copy["id"] = 0;
        copy["name"] = "Test";

        using var posted = await client.PostAsync(new Uri(ProfilesEndpoint, UriKind.Relative), JsonContent(copy));
        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        posted.Headers.Location.Should().NotBeNull();

        var created = (JsonObject)(await ReadJsonAsync(posted))!;
        var id = created["id"]!.GetValue<long>();
        id.Should().Be(3);
        created["name"]!.GetValue<string>().Should().Be("Test");
        posted.Headers.Location!.OriginalString.Should().EndWith($"/api/v1/qualityprofile/{id}");

        using var deleted = await client.DeleteAsync(new Uri($"{ProfilesEndpoint}/{id}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var reread = await client.GetAsync(new Uri($"{ProfilesEndpoint}/{id}", UriKind.Relative));
        reread.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Posting_a_second_profile_with_the_same_name_in_another_case_is_rejected()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var first = await GetProfileAsync(client, 1);
        first["id"] = 0;
        first["name"] = "Test";

        using var posted = await client.PostAsync(new Uri(ProfilesEndpoint, UriKind.Relative), JsonContent(first));
        posted.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await GetProfileAsync(client, 1);
        second["id"] = 0;
        second["name"] = "test";

        using var duplicate = await client.PostAsync(new Uri(ProfilesEndpoint, UriKind.Relative), JsonContent(second));
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ValidationErrorsAsync(duplicate)).Should().ContainKey("name");
    }

    [Fact]
    public async Task Putting_a_cutoff_in_a_rejected_group_is_a_bad_request_naming_the_cutoff()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var profile = await GetProfileAsync(client, 1);
        profile["cutoff"] = 26; // Vorbis Q7 sits in a rejected group.

        using var response = await PutProfileAsync(client, 1, profile);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var errors = await ValidationErrorsAsync(response);
        errors.Should().ContainKey("cutoff");
    }

    [Fact]
    public async Task Putting_a_repeated_quality_is_a_bad_request()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var profile = await GetProfileAsync(client, 1);
        ((JsonArray)((JsonObject)((JsonArray)profile["items"]!)[0]!)["qualities"]!)
            .Add(JsonNode.Parse("{\"id\":29}"));

        using var response = await PutProfileAsync(client, 1, profile);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var errors = await ValidationErrorsAsync(response);
        errors.Should().ContainKey("items");
        errors["items"].Should().Contain(message => message.Contains("listed 2 times"));
    }

    [Fact]
    public async Task Putting_a_profile_that_omits_a_quality_is_a_bad_request()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var profile = await GetProfileAsync(client, 1);
        var items = (JsonArray)profile["items"]!;
        var lastItem = (JsonObject)items[items.Count - 1]!;
        var qualities = (JsonArray)lastItem["qualities"]!;

        for (var index = qualities.Count - 1; index >= 0; index--)
        {
            if ((long)qualities[index]!["id"]! == 43)
            {
                qualities.RemoveAt(index);
            }
        }

        using var response = await PutProfileAsync(client, 1, profile);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var errors = await ValidationErrorsAsync(response);
        errors.Should().ContainKey("items");
        errors["items"].Should().Contain(message => message.Contains("(43)"));
    }

    [Fact]
    public async Task Putting_a_body_id_that_does_not_match_the_route_is_a_bad_request()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        var profile = await GetProfileAsync(client, 2);
        profile["id"] = 1;

        using var response = await PutProfileAsync(client, 2, profile);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("id");
    }

    [Fact]
    public async Task Deleting_a_profile_a_song_uses_is_a_conflict()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        await AddSongUsingProfileAsync(factory, SeedData.StandardProfileId);

        using var response = await client.DeleteAsync(new Uri($"{ProfilesEndpoint}/{SeedData.StandardProfileId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("still used by a song");
    }

    [Fact]
    public async Task Deleting_the_last_remaining_profile_is_a_conflict()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        using var deleted = await client.DeleteAsync(new Uri($"{ProfilesEndpoint}/{SeedData.LosslessProfileId}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var last = await client.DeleteAsync(new Uri($"{ProfilesEndpoint}/{SeedData.StandardProfileId}", UriKind.Relative));

        last.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reading_and_deleting_an_unknown_profile_is_a_not_found()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);

        using var read = await client.GetAsync(new Uri($"{ProfilesEndpoint}/987654", UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var deleted = await client.DeleteAsync(new Uri($"{ProfilesEndpoint}/987654", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await GetProfileAsync(client, 1);
        body["id"] = 0;

        using var updated = await PutProfileAsync(client, 987_654, body);
        updated.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient Authenticated(CompilarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonObject> GetProfileAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync(new Uri($"{ProfilesEndpoint}/{id}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (JsonObject)(await ReadJsonAsync(response))!;
    }

    private static Task<HttpResponseMessage> PutProfileAsync(HttpClient client, long id, JsonObject profile)
    {
        return client.PutAsync(new Uri($"{ProfilesEndpoint}/{id}", UriKind.Relative), JsonContent(profile));
    }

    private static StringContent JsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

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

    /// <summary>Inserts a song that uses <paramref name="profileId"/> straight through the context.</summary>
    private static async Task AddSongUsingProfileAsync(CompilarrAppFactory factory, long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        context.Songs.Add(new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            PrimaryArtist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" },
            QualityProfileId = profileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        });

        await context.SaveChangesAsync();
    }
}
