using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The library endpoints: the seeded Plexamp library and the edits the Settings UI makes.</summary>
public sealed class LibraryApiTests
{
    private const string LibrariesEndpoint = "/api/v1/library";

    [Fact]
    public async Task Listing_libraries_returns_the_default_music_library()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri(LibrariesEndpoint, UriKind.Relative));
        var libraries = (JsonArray)(await ReadJsonAsync(response))!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        libraries.Should().ContainSingle();

        var library = (JsonObject)libraries[0]!;
        library["id"]!.GetValue<long>().Should().Be(1);
        library["name"]!.GetValue<string>().Should().Be("Music");
        library["rootPath"]!.GetValue<string>().Should().Be("/data/music");
        library["layout"]!.GetValue<string>().Should().Be("plexamp");
        library["albumPolicy"]!.GetValue<string>().Should().Be("fewestAlbums");
        library["namingTemplate"]!.GetValue<string>()
            .Should().Be("{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}");
        library["minTracksPerRealAlbum"]!.GetValue<int>().Should().Be(2);
        library["plexSectionId"].Should().BeNull();
        library["plexLibraryPath"].Should().BeNull();
        library["isDefault"]!.GetValue<bool>().Should().BeTrue();
        library["sidecarOptions"].Should().BeOfType<JsonObject>();
    }

    [Fact]
    public async Task Reading_a_library_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(LibrariesEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Updating_a_library_persists_the_album_policy_and_the_track_threshold()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["minTracksPerRealAlbum"] = 3;
        library["albumPolicy"] = "singlesOnly";

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var returned = (JsonObject)(await ReadJsonAsync(response))!;
        returned["minTracksPerRealAlbum"]!.GetValue<int>().Should().Be(3);
        returned["albumPolicy"]!.GetValue<string>().Should().Be("singlesOnly");

        var reread = await GetLibraryAsync(client, 1);
        reread["minTracksPerRealAlbum"]!.GetValue<int>().Should().Be(3);
        reread["albumPolicy"]!.GetValue<string>().Should().Be("singlesOnly");
    }

    [Fact]
    public async Task Updating_a_library_persists_the_plex_section_and_path()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["plexSectionId"] = "3";
        library["plexLibraryPath"] = "/plex/music";

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var returned = (JsonObject)(await ReadJsonAsync(response))!;
        returned["plexSectionId"]!.GetValue<string>().Should().Be("3");
        returned["plexLibraryPath"]!.GetValue<string>().Should().Be("/plex/music");

        var reread = await GetLibraryAsync(client, 1);
        reread["plexSectionId"]!.GetValue<string>().Should().Be("3");
        reread["plexLibraryPath"]!.GetValue<string>().Should().Be("/plex/music");
    }

    [Fact]
    public async Task Updating_a_library_with_an_empty_naming_template_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["namingTemplate"] = string.Empty;

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("namingTemplate");
    }

    [Fact]
    public async Task Clearing_is_default_on_the_only_library_is_a_bad_request()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["isDefault"] = false;

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("isDefault");

        var reread = await GetLibraryAsync(client, 1);
        reread["isDefault"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_library_without_an_output_policy_reports_the_default()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);

        // Version 2 (DECISIONS build session 7 #4): YouTube → AAC 256, everything else kept.
        var outputPolicy = (JsonObject)library["outputPolicy"]!;
        outputPolicy["version"]!.GetValue<int>().Should().Be(2);
        var policy = (JsonObject)outputPolicy["youtube"]!;
        policy["codec"]!.GetValue<string>().Should().Be("aac");
        policy["mode"]!.GetValue<string>().Should().Be("cbr");
        policy["bitrateKbps"]!.GetValue<int>().Should().Be(256);
        policy["vbrQuality"]!.GetValue<int>().Should().Be(0);
        policy["sampleRate"]!.GetValue<string>().Should().Be("keep");
        ((JsonObject)outputPolicy["lossy"]!)["codec"]!.GetValue<string>().Should().Be("keep");
        ((JsonObject)outputPolicy["lossless"]!)["codec"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public async Task A_version_1_output_policy_is_stored_as_the_youtube_rule_of_a_version_2_one()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["outputPolicy"] = new JsonObject { ["codec"] = "mp3", ["bitrateKbps"] = 320 };

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var outputPolicy = (JsonObject)(await GetLibraryAsync(client, 1))["outputPolicy"]!;
        outputPolicy["version"]!.GetValue<int>().Should().Be(2);
        ((JsonObject)outputPolicy["youtube"]!)["codec"]!.GetValue<string>().Should().Be("mp3");
        ((JsonObject)outputPolicy["youtube"]!)["bitrateKbps"]!.GetValue<int>().Should().Be(320);
        ((JsonObject)outputPolicy["lossless"]!)["codec"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public async Task Updating_a_library_persists_the_output_policy()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["outputPolicy"] = new JsonObject
        {
            ["version"] = 2,
            ["lossless"] = new JsonObject
            {
                ["codec"] = "mp3",
                ["mode"] = "vbr",
                ["vbrQuality"] = 2,
            },
        };

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await GetLibraryAsync(client, 1);
        var outputPolicy = (JsonObject)reread["outputPolicy"]!;
        var policy = (JsonObject)outputPolicy["lossless"]!;
        policy["codec"]!.GetValue<string>().Should().Be("mp3");
        policy["mode"]!.GetValue<string>().Should().Be("vbr");
        policy["vbrQuality"]!.GetValue<int>().Should().Be(2);
        // The keys the body left out come back with their defaults, and the rules it left out too.
        policy["bitrateKbps"]!.GetValue<int>().Should().Be(256);
        policy["sampleRate"]!.GetValue<string>().Should().Be("keep");
        ((JsonObject)outputPolicy["youtube"]!)["codec"]!.GetValue<string>().Should().Be("aac");

        // What the API returned can be sent straight back.
        using var echoed = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(reread));
        echoed.StatusCode.Should().Be(HttpStatusCode.OK, await echoed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_explicit_null_output_policy_clears_back_to_the_default()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        // Set a policy first, so clearing it has something to clear.
        var library = await GetLibraryAsync(client, 1);
        library["outputPolicy"] = new JsonObject { ["codec"] = "mp3" };

        using (var put = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library)))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // A PUT that carries an explicit null clears the policy: the library reads back the default.
        library = await GetLibraryAsync(client, 1);
        library["outputPolicy"] = null;

        using (var put = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library)))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var reread = await GetLibraryAsync(client, 1);
        var policy = (JsonObject)((JsonObject)reread["outputPolicy"]!)["youtube"]!;
        policy["codec"]!.GetValue<string>().Should().Be("aac", "the null cleared the stored policy");
        policy["bitrateKbps"]!.GetValue<int>().Should().Be(256);
    }

    [Fact]
    public async Task A_lossless_output_policy_is_a_bad_request_that_names_the_key()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var library = await GetLibraryAsync(client, 1);
        library["outputPolicy"] = new JsonObject { ["codec"] = "flac" };

        using var response = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/1", UriKind.Relative),
            JsonContent(library));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A version-1 object is the YouTube rule, so that is where the error is named.
        (await ValidationErrorsAsync(response)).Should().ContainKey("youtube.codec");

        // The stored policy is unchanged.
        var reread = await GetLibraryAsync(client, 1);
        ((JsonObject)((JsonObject)reread["outputPolicy"]!)["youtube"]!)["codec"]!.GetValue<string>().Should().Be("aac");
    }

    [Fact]
    public async Task Reading_and_updating_an_unknown_library_is_a_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var read = await client.GetAsync(new Uri($"{LibrariesEndpoint}/987654", UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var library = await GetLibraryAsync(client, 1);
        library["id"] = 0;

        using var updated = await client.PutAsync(
            new Uri($"{LibrariesEndpoint}/987654", UriKind.Relative),
            JsonContent(library));

        updated.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonObject> GetLibraryAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync(new Uri($"{LibrariesEndpoint}/{id}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (JsonObject)(await ReadJsonAsync(response))!;
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
}
