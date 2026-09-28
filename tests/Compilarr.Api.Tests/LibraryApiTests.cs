using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>The library endpoints: the seeded Plexamp library and the edits the Settings UI makes.</summary>
public sealed class LibraryApiTests
{
    private const string LibrariesEndpoint = "/api/v1/library";

    [Fact]
    public async Task Listing_libraries_returns_the_default_music_library()
    {
        using var factory = new CompilarrAppFactory();
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
        library["isDefault"]!.GetValue<bool>().Should().BeTrue();
        library["sidecarOptions"].Should().BeOfType<JsonObject>();
    }

    [Fact]
    public async Task Reading_a_library_without_a_key_is_unauthorized()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(LibrariesEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Updating_a_library_persists_the_album_policy_and_the_track_threshold()
    {
        using var factory = new CompilarrAppFactory();
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
    public async Task Updating_a_library_with_an_empty_naming_template_is_a_bad_request()
    {
        using var factory = new CompilarrAppFactory();
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
        using var factory = new CompilarrAppFactory();
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
    public async Task Reading_and_updating_an_unknown_library_is_a_not_found()
    {
        using var factory = new CompilarrAppFactory();
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

    private static HttpClient Authenticated(CompilarrAppFactory factory)
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
