using System.Net;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Library page's backend over HTTP: the song list filters, the mass editor, the tag list and the
/// saved views (custom filters).
/// </summary>
public sealed class SongEditorApiTests
{
    private const string Song = "/api/v1/song";
    private const string Editor = "/api/v1/song/editor";
    private const string Tag = "/api/v1/tag";
    private const string CustomFilter = "/api/v1/customfilter";

    [Fact]
    public async Task Every_new_endpoint_refuses_a_caller_without_an_api_key()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = factory.CreateClient();

        var requests = new (HttpMethod Method, string Url, string? Body)[]
        {
            (HttpMethod.Put, Editor, """{"songIds":[1],"monitored":true}"""),
            (HttpMethod.Delete, Editor, """{"songIds":[1]}"""),
            (HttpMethod.Get, Tag, null),
            (HttpMethod.Get, CustomFilter, null),
            (HttpMethod.Get, CustomFilter + "/1", null),
            (HttpMethod.Post, CustomFilter, """{"type":"library","label":"x","filters":[]}"""),
            (HttpMethod.Put, CustomFilter + "/1", """{"type":"library","label":"x","filters":[]}"""),
            (HttpMethod.Delete, CustomFilter + "/1", null),
            (HttpMethod.Get, Song + "?term=x&tag=y", null),
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            if (body is not null)
            {
                request.Content = SongApiTests.Json(body);
            }

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {url}");
        }
    }

    [Fact]
    public async Task The_song_list_filters_on_the_query_string_and_returns_tags()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var a = await SongApiTests.SeedSongAsync(factory, "Alpha 100%", null, monitored: true);
        await SongApiTests.SeedSongAsync(factory, "Alphabet", null, monitored: true);
        await SetTagsAsync(factory, a, "Chill");

        var page = await GetAsync(client, Song + "?term=100%25");
        page.GetProperty("totalRecords").GetInt32().Should().Be(1);
        var song = page.GetProperty("records")[0];
        song.GetProperty("title").GetString().Should().Be("Alpha 100%");
        song.GetProperty("tags")[0].GetString().Should().Be("chill");

        (await GetAsync(client, Song + "?tag=CHILL&monitored=true&hasFile=false")).GetProperty("totalRecords").GetInt32().Should().Be(1);
        (await GetAsync(client, Song + "?cutoffMet=false")).GetProperty("totalRecords").GetInt32().Should().Be(0);
        (await GetAsync(client, Song + "?sortKey=quality&sortDirection=descending")).GetProperty("totalRecords").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_editor_changes_many_songs_and_the_tag_list_counts_them()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var a = await SongApiTests.SeedSongAsync(factory, "A", null, monitored: true);
        var b = await SongApiTests.SeedSongAsync(factory, "B", null, monitored: true);

        using var edited = await client.PutAsync(
            new Uri(Editor, UriKind.Relative),
            SongApiTests.Json($$"""{"songIds":[{{a}},{{b}}],"monitored":false,"tags":[" Road Trip","chill"],"applyTags":"add"}"""));

        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await SongApiTests.ReadJsonAsync(edited);
        body.GetProperty("songs").GetArrayLength().Should().Be(2);
        body.GetProperty("songs")[0].GetProperty("monitored").GetBoolean().Should().BeFalse();
        body.GetProperty("songs")[0].GetProperty("tags").GetArrayLength().Should().Be(2);
        body.GetProperty("moveCommandIds").GetArrayLength().Should().Be(0);

        var tags = await GetAsync(client, Tag);
        tags.GetArrayLength().Should().Be(2);
        tags[0].GetProperty("label").GetString().Should().Be("chill");
        tags[0].GetProperty("songCount").GetInt32().Should().Be(2);
        tags[1].GetProperty("label").GetString().Should().Be("road trip");
    }

    [Fact]
    public async Task A_library_change_answers_with_the_queued_command()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var a = await SongApiTests.SeedSongAsync(factory, "A", null, monitored: true);
        await AddLibraryAsync(factory);

        using var edited = await client.PutAsync(
            new Uri(Editor, UriKind.Relative),
            SongApiTests.Json($$"""{"songIds":[{{a}}],"libraryId":2}"""));

        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await SongApiTests.ReadJsonAsync(edited);
        body.GetProperty("moveCommandIds").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task The_editor_refuses_unknown_songs_and_unusable_bodies()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var a = await SongApiTests.SeedSongAsync(factory, "A", null, monitored: true);

        using var unknown = await client.PutAsync(
            new Uri(Editor, UriKind.Relative),
            SongApiTests.Json($$"""{"songIds":[{{a}},424242],"monitored":false}"""));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SongApiTests.ReadJsonAsync(unknown)).GetProperty("songIds")[0].GetInt64().Should().Be(424242);
        (await GetAsync(client, Song + "/" + a)).GetProperty("monitored").GetBoolean().Should().BeTrue();

        var tooMany = string.Join(',', Enumerable.Range(1, 1001));
        var bad = new[]
        {
            $$"""{"songIds":[{{a}}]}""",
            $$"""{"songIds":[{{a}}],"tags":["x"]}""",
            $$"""{"songIds":[{{a}}],"qualityProfileId":999}""",
            $$"""{"songIds":[{{a}}],"libraryId":999}""",
            """{"monitored":true}""",
            $$"""{"songIds":[{{tooMany}}],"monitored":true}""",
        };

        foreach (var json in bad)
        {
            using var response = await client.PutAsync(new Uri(Editor, UriKind.Relative), SongApiTests.Json(json));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, json[..Math.Min(json.Length, 60)]);
        }
    }

    [Fact]
    public async Task The_mass_delete_removes_the_songs_and_refuses_unknown_ids()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var a = await SongApiTests.SeedSongAsync(factory, "A", null, monitored: true);
        var b = await SongApiTests.SeedSongAsync(factory, "B", null, monitored: true);

        using var unknown = await Send(client, HttpMethod.Delete, Editor, $$"""{"songIds":[{{a}},9999]}""");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(client, Song)).GetProperty("totalRecords").GetInt32().Should().Be(2);

        using var deleted = await Send(client, HttpMethod.Delete, Editor, $$"""{"songIds":[{{a}},{{b}}]}""");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SongApiTests.ReadJsonAsync(deleted)).GetProperty("deleted").GetInt32().Should().Be(2);
        (await GetAsync(client, Song)).GetProperty("totalRecords").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Saved_views_round_trip_and_refuse_duplicates_and_bad_filters()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        const string Body = """{"type":"library","label":"No file","filters":[{"key":"hasFile","value":[false],"type":"equal"}]}""";

        using var created = await client.PostAsync(new Uri(CustomFilter, UriKind.Relative), SongApiTests.Json(Body));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var row = await SongApiTests.ReadJsonAsync(created);
        var id = row.GetProperty("id").GetInt64();
        row.GetProperty("filters")[0].GetProperty("key").GetString().Should().Be("hasFile");

        using var duplicate = await client.PostAsync(new Uri(CustomFilter, UriKind.Relative), SongApiTests.Json(Body));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var notArray = await client.PostAsync(
            new Uri(CustomFilter, UriKind.Relative),
            SongApiTests.Json("""{"type":"library","label":"Bad","filters":{"key":"a"}}"""));
        notArray.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var missing = await client.PostAsync(
            new Uri(CustomFilter, UriKind.Relative),
            SongApiTests.Json("""{"type":"library","label":"Bad"}"""));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await GetAsync(client, CustomFilter + "?type=library")).GetArrayLength().Should().Be(1);
        (await GetAsync(client, CustomFilter + "?type=other")).GetArrayLength().Should().Be(0);

        using var updated = await client.PutAsync(
            new Uri($"{CustomFilter}/{id}", UriKind.Relative),
            SongApiTests.Json("""{"type":"library","label":"Renamed","filters":[]}"""));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, $"{CustomFilter}/{id}")).GetProperty("label").GetString().Should().Be("Renamed");

        using var deleted = await client.DeleteAsync(new Uri($"{CustomFilter}/{id}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        using var gone = await client.GetAsync(new Uri($"{CustomFilter}/{id}", UriKind.Relative));
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);

        return await SongApiTests.ReadJsonAsync(response);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string body)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        return await client.SendAsync(request);
    }

    private static async Task SetTagsAsync(WondarrAppFactory factory, long songId, params string[] tags)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var song = await context.Songs.SingleAsync(row => row.Id == songId);
        song.Tags = [.. tags.Select(tag => tag.ToLowerInvariant())];
        await context.SaveChangesAsync();
    }

    private static async Task AddLibraryAsync(WondarrAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        context.Libraries.Add(new Library { Name = "Second", RootPath = "/music2" });
        await context.SaveChangesAsync();
    }
}
