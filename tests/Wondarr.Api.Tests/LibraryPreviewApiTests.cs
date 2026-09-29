using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The library editor's naming preview: a template, a song and the path they build.</summary>
public sealed class LibraryPreviewApiTests
{
    private const string PreviewEndpoint = "/api/v1/library/1/preview";

    [Fact]
    public async Task The_preview_renders_the_library_template_for_a_song()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");

        using var response = await client.PostAsync(
            new Uri(PreviewEndpoint, UriKind.Relative),
            Json($"{{\"songId\": {songId}}}"));

        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("errors").GetArrayLength().Should().Be(0);
        body.GetProperty("path").GetString()
            .Should().Be("/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac");
    }

    [Fact]
    public async Task The_preview_renders_a_custom_template_without_saving_it()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);
        var songId = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");

        using var response = await client.PostAsync(
            new Uri(PreviewEndpoint, UriKind.Relative),
            Json($"{{\"songId\": {songId}, \"template\": \"{{Artist Name}} - {{Track Title}}\"}}"));

        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("path").GetString().Should().Be("/data/music/Daft Punk - Get Lucky.flac");

        // The library's own template is untouched: the preview never saves.
        using var library = await client.GetAsync(new Uri("/api/v1/library/1", UriKind.Relative));
        (await ReadJsonAsync(library)).GetProperty("namingTemplate").GetString()
            .Should().Be("{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}");
    }

    [Fact]
    public async Task An_invalid_template_answers_with_its_errors_and_no_path()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri(PreviewEndpoint, UriKind.Relative),
            Json("{\"template\": \"{Not A Token}\"}"));

        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("path").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_preview_without_a_song_renders_the_sample()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await client.PostAsync(new Uri(PreviewEndpoint, UriKind.Relative), Json("{}"));
        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("path").GetString()
            .Should().Be("/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac");
    }

    [Fact]
    public async Task Previewing_an_unknown_library_or_song_is_a_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var library = await client.PostAsync(
            new Uri("/api/v1/library/987654/preview", UriKind.Relative),
            Json("{}"));

        using var song = await client.PostAsync(
            new Uri(PreviewEndpoint, UriKind.Relative),
            Json("{\"songId\": 987654}"));

        library.StatusCode.Should().Be(HttpStatusCode.NotFound);
        song.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_preview_requires_a_key()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri(PreviewEndpoint, UriKind.Relative), Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
