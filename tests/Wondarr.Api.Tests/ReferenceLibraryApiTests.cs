using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The reference-library endpoints: the folders Wondarr reads but never writes (LIBRARY_OUTPUT §7.6).
/// The rules themselves are covered by <c>ReferenceLibraryServiceTests</c>; here they are checked as the
/// API reports them.
/// </summary>
public sealed class ReferenceLibraryApiTests
{
    private const string Endpoint = "/api/v1/referencelibrary";

    [Fact]
    public async Task Adding_a_library_returns_it_with_empty_counts()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":" Music I own ","rootPath":"/reference/music","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith("/api/v1/referencelibrary/1");

        var library = (JsonObject)(await ReadJsonAsync(response))!;
        library["id"]!.GetValue<long>().Should().Be(1);
        library["name"]!.GetValue<string>().Should().Be("Music I own");
        library["rootPath"]!.GetValue<string>().Should().Be("/reference/music");
        library["mode"]!.GetValue<string>().Should().Be("reference");
        library["libraryId"].Should().BeNull();
        library["enabled"]!.GetValue<bool>().Should().BeTrue();
        library["lastScannedAt"].Should().BeNull();
        library["lastScanMessage"].Should().BeNull();
        ((JsonObject)library["counts"]!)["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Adding_a_library_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Music","rootPath":"/reference/music","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_empty_name_is_a_bad_request_on_the_name_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"   ","rootPath":"/reference/music","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("name");
    }

    [Fact]
    public async Task A_duplicate_name_is_a_bad_request_on_the_name_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        await AddLibraryAsync(client, "Music", "/reference/music", "reference");

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"music","rootPath":"/reference/other","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("name");
    }

    [Fact]
    public async Task A_relative_root_is_a_bad_request_on_the_root_path_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Music","rootPath":"reference/music","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("rootPath");
    }

    [Fact]
    public async Task A_root_inside_the_managed_library_is_a_bad_request_on_the_root_path_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        // The seeded managed library lives at /data/music.
        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Inside","rootPath":"/data/music/inner","mode":"reference","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("rootPath");
    }

    [Fact]
    public async Task Adopting_without_a_library_is_a_bad_request_on_the_library_id_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Adopt","rootPath":"/reference/music","mode":"adopt","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("libraryId");
    }

    [Fact]
    public async Task An_unknown_mode_is_a_bad_request_on_the_mode_field()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Music","rootPath":"/reference/music","mode":"readonly","enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ValidationErrorsAsync(response)).Should().ContainKey("mode");
    }

    [Fact]
    public async Task Adopting_into_a_known_library_is_stored_with_its_id()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var response = await PostAsync(
            client,
            Endpoint,
            """{"name":"Adopt","rootPath":"/reference/music","mode":"adopt","libraryId":1,"enabled":true}""");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var library = (JsonObject)(await ReadJsonAsync(response))!;
        library["mode"]!.GetValue<string>().Should().Be("adopt");
        library["libraryId"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public async Task Listing_and_reading_report_the_file_counts_by_state()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var id = await AddLibraryAsync(client, "Music", "/reference/music", "reference");
        await SeedFilesAsync(factory, id, ("a.flac", "ambiguous"), ("b.flac", "ambiguous"), ("c.flac", "identified"));

        using var listed = await client.GetAsync(new Uri(Endpoint, UriKind.Relative));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        var libraries = (JsonArray)(await ReadJsonAsync(listed))!;
        libraries.Should().ContainSingle();

        var counts = (JsonObject)((JsonObject)libraries[0]!)["counts"]!;
        counts["total"]!.GetValue<int>().Should().Be(3);
        counts["ambiguous"]!.GetValue<int>().Should().Be(2);
        counts["identified"]!.GetValue<int>().Should().Be(1);
        counts["unmatched"]!.GetValue<int>().Should().Be(0);

        var read = await GetLibraryAsync(client, id);
        read["rootPath"]!.GetValue<string>().Should().Be("/reference/music");
        ((JsonObject)read["counts"]!)["total"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Updating_a_library_persists_the_new_values()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var id = await AddLibraryAsync(client, "Music", "/reference/music", "reference");

        using var response = await client.PutAsync(
            new Uri($"{Endpoint}/{id}", UriKind.Relative),
            Json("""{"name":"Music I own","rootPath":"/reference/other","mode":"reference","enabled":false}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await GetLibraryAsync(client, id);
        reread["name"]!.GetValue<string>().Should().Be("Music I own");
        reread["rootPath"]!.GetValue<string>().Should().Be("/reference/other");
        reread["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Reading_updating_deleting_and_scanning_an_unknown_library_are_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        using var read = await client.GetAsync(new Uri($"{Endpoint}/987654", UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var updated = await client.PutAsync(
            new Uri($"{Endpoint}/987654", UriKind.Relative),
            Json("""{"name":"Music","rootPath":"/reference/music","mode":"reference","enabled":true}"""));
        updated.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scanned = await client.PostAsync(new Uri($"{Endpoint}/987654/scan", UriKind.Relative), null);
        scanned.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/987654", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_library_answers_with_an_empty_ok_and_removes_it()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        var id = await AddLibraryAsync(client, "Music", "/reference/music", "reference");
        await SeedFilesAsync(factory, id, ("a.flac", "identified"));

        using var deleted = await client.DeleteAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await deleted.Content.ReadAsStringAsync()).Should().BeEmpty();

        using var read = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        (await context.ReferenceFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Scanning_queues_one_scan_command_naming_that_library()
    {
        using var factory = new WondarrAppFactory();
        using var client = Authenticated(factory);

        // A real, empty folder: the handler the queue wakes walks it for real.
        var root = Path.Combine(factory.ConfigDir, "reference");
        Directory.CreateDirectory(root);

        var id = await AddLibraryAsync(client, "Music", root, "reference");

        using var response = await client.PostAsync(new Uri($"{Endpoint}/{id}/scan", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Contain("/api/v1/command/");

        var command = (JsonObject)(await ReadJsonAsync(response))!;
        command["name"]!.GetValue<string>().Should().Be("ReferenceLibraryScan");
        command["trigger"]!.GetValue<string>().Should().Be("manual");

        var queuedId = command["id"]!.GetValue<long>();

        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var row = await context.Commands.AsNoTracking().SingleAsync(record => record.Id == queuedId);
        row.Name.Should().Be("ReferenceLibraryScan");
        row.Body.Should().Contain($"\"referenceLibraryId\":{id}");
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<long> AddLibraryAsync(HttpClient client, string name, string rootPath, string mode)
    {
        // Built as JSON rather than interpolated: a root path may contain backslashes.
        var body = new JsonObject
        {
            ["name"] = name,
            ["rootPath"] = rootPath,
            ["mode"] = mode,
            ["enabled"] = true,
        };

        using var response = await PostAsync(client, Endpoint, body.ToJsonString());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return ((JsonObject)(await ReadJsonAsync(response))!)["id"]!.GetValue<long>();
    }

    private static async Task<JsonObject> GetLibraryAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync(new Uri($"{Endpoint}/{id}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (JsonObject)(await ReadJsonAsync(response))!;
    }

    /// <summary>Seeds reference files straight into the database: the scan is P3-02's, not this task's.</summary>
    private static async Task SeedFilesAsync(
        WondarrAppFactory factory,
        long referenceLibraryId,
        params (string RelativePath, string State)[] files)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        foreach (var (relativePath, state) in files)
        {
            context.ReferenceFiles.Add(new ReferenceFile
            {
                ReferenceLibraryId = referenceLibraryId,
                RelativePath = relativePath,
                Size = 1,
                ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                State = Enum.Parse<ReferenceFileState>(state, ignoreCase: true),
            });
        }

        await context.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string body) =>
        client.PostAsync(new Uri(url, UriKind.Relative), Json(body));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

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
