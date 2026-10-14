using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wondarr.Core.Identity;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The synced import lists' API: the schema, a CSV list created from an Exportify export, a CSV
/// preview, a sync through the command queue, and the refusals.
/// </summary>
public sealed class ImportListApiTests
{
    private const string ListEndpoint = "/api/v1/importlist";
    private const string ItemEndpoint = "/api/v1/importlistitem";
    private const string CommandEndpoint = "/api/v1/command";

    // Only a bound on a hang, never a speed requirement. A sync takes about a second alone, but in a full
    // run the command waits its turn behind hundreds of tests' hosts and the process-wide per-list sync
    // gate (ImportListSyncGates), and a 30 s wait was not always enough.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    private const string Exportify =
        "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\"\n"
        + "\"spotify:track:69kOkLUCkxIZYexIgSG8rq\",\"Get Lucky\",\"spotify:artist:1\",\"Daft Punk,Pharrell Williams\",\"spotify:album:1\",\"Random Access Memories\",\"\",\"Daft Punk\",\"2013-05-20\",\"\",\"1\",\"8\",\"369626\",\"\",\"false\",\"80\",\"USQX91300108\",\"\",\"\"\n"
        + "\"spotify:track:0000000000000000000000\",\"Nothing\",\"spotify:artist:0\",\"Nobody\",\"spotify:album:0\",\"None\",\"\",\"Nobody\",\"2020\",\"\",\"1\",\"1\",\"200000\",\"\",\"false\",\"1\",\"\",\"\",\"\"\n";

    // Owner question 4: a list whose provider is no longer registered is masked by key name, not shown raw.
    [Fact]
    public async Task A_list_whose_provider_is_gone_comes_back_with_the_keys_that_look_secret_masked()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        long id;

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<Wondarr.Core.Persistence.WondarrDbContext>();
            var list = new Wondarr.Core.Domain.ImportList
            {
                Type = "retired-provider",
                Name = "Vanished",
                Settings = """{"user":"someone","apiKey":"wonderwall","sessionCookie":"ck-0123"}""",
                QualityProfileId = Wondarr.Core.Domain.SeedData.StandardProfileId,
                LibraryId = Wondarr.Core.Domain.SeedData.DefaultLibraryId,
            };
            database.ImportLists.Add(list);
            await database.SaveChangesAsync();
            id = list.Id;
        }

        using var response = await client.GetAsync(new Uri($"{ListEndpoint}/{id}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("wonderwall").And.NotContain("ck-0123");

        var settings = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("settings");
        settings.GetProperty("apiKey").GetString().Should().Be("********");
        settings.GetProperty("sessionCookie").GetString().Should().Be("********");
        settings.GetProperty("user").GetString().Should().Be("someone");
    }

    [Fact]
    public async Task The_schema_names_the_csv_provider_and_its_column_fields()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{ListEndpoint}/schema", UriKind.Relative));
        var schema = await SongApiTests.ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        schema.EnumerateArray().Select(provider => provider.GetProperty("type").GetString()).Should()
            .Contain(["csv", "deezerPlaylist", "deezerArtistTop", "youtubeMusicPlaylist", "lastfmLoved", "lastfmTop", "listenbrainzLoved", "listenbrainzPlaylist", "referenceLibrary"]);

        var csv = schema.EnumerateArray().Single(provider => provider.GetProperty("type").GetString() == "csv");
        csv.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString())
            .Should().Contain(["titleColumn", "artistColumn", "isrcColumn"]);
    }

    [Fact]
    public async Task A_csv_preview_reads_an_exportify_export_without_storing_it()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri($"{ListEndpoint}/csv/preview", UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new { sourceText = Exportify })));
        var preview = await SongApiTests.ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.GetProperty("format").GetString().Should().Be("exportify");
        preview.GetProperty("rowCount").GetInt32().Should().Be(2);
        preview.GetProperty("sample")[0].GetProperty("isrc").GetString().Should().Be("USQX91300108");
        preview.GetProperty("problems").GetArrayLength().Should().Be(0);

        using var lists = await client.GetAsync(new Uri(ListEndpoint, UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(lists)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_csv_list_syncs_through_the_command_queue_and_resolves_by_isrc()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<string>() == "USQX91300108"
                ? new ResolveResult { Status = ResolveStatus.Resolved, Identity = SongApiTests.Identity() }
                : new ResolveResult { Status = ResolveStatus.Unresolved, Reason = "No match." }));

        using var factory = SongApiTests.FakeProviders(resolver);
        using var client = SongApiTests.Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(ListEndpoint, UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new
            {
                type = "csv",
                name = "Road trip",
                settings = new { },
                sourceText = Exportify,
                syncIntervalHours = 0,
            })));
        var list = await SongApiTests.ReadJsonAsync(created);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        list.GetProperty("hasFile").GetBoolean().Should().BeTrue();
        list.GetProperty("policy").GetString().Should().Be("AddOnly");
        var listId = list.GetProperty("id").GetInt64();

        using var sync = await client.PostAsync(new Uri($"{ListEndpoint}/{listId}/sync", UriKind.Relative), null);
        sync.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var commandId = (await SongApiTests.ReadJsonAsync(sync)).GetProperty("commandId").GetInt64();

        var finished = await WaitForTerminalStatusAsync(client, commandId);
        finished.GetProperty("status").GetString().Should().Be("completed");
        finished.GetProperty("message").GetString().Should().StartWith("Read 2 items (2 new, 0 no longer in the list)");

        using var header = await client.GetAsync(new Uri($"{ListEndpoint}/{listId}", UriKind.Relative));
        var counts = (await SongApiTests.ReadJsonAsync(header)).GetProperty("counts");
        counts.GetProperty("added").GetInt32().Should().Be(1);
        counts.GetProperty("unresolved").GetInt32().Should().Be(1);

        using var items = await client.GetAsync(new Uri($"{ItemEndpoint}?importListId={listId}", UriKind.Relative));
        var records = (await SongApiTests.ReadJsonAsync(items)).GetProperty("records");
        records[0].GetProperty("line").GetInt32().Should().Be(1);
        records[0].GetProperty("text").GetString().Should().Be("Daft Punk - Get Lucky");
        records[1].GetProperty("line").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Trusting_the_next_read_sets_the_flag_and_refuses_a_pasted_or_unknown_list()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(ListEndpoint, UriKind.Relative),
            SongApiTests.Json(JsonSerializer.Serialize(new { type = "csv", name = "Road trip", settings = new { }, sourceText = Exportify })));
        var listId = (await SongApiTests.ReadJsonAsync(created)).GetProperty("id").GetInt64();
        (await SongApiTests.ReadJsonAsync(created)).GetProperty("trustNextRead").GetBoolean().Should().BeFalse();

        using var trusted = await client.PostAsync(new Uri($"{ListEndpoint}/{listId}/trustnextread", UriKind.Relative), null);
        trusted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SongApiTests.ReadJsonAsync(trusted)).GetProperty("trustNextRead").GetBoolean().Should().BeTrue();

        using var read = await client.GetAsync(new Uri($"{ListEndpoint}/{listId}", UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(read)).GetProperty("trustNextRead").GetBoolean().Should().BeTrue();

        using var pasted = await client.PostAsync(
            new Uri("/api/v1/song/bulk", UriKind.Relative),
            SongApiTests.Json("""{"text":"Queen - Bohemian Rhapsody"}"""));
        var pastedId = (await SongApiTests.ReadJsonAsync(pasted)).GetProperty("importListId").GetInt64();
        using var pastedTrust = await client.PostAsync(new Uri($"{ListEndpoint}/{pastedId}/trustnextread", UriKind.Relative), null);
        pastedTrust.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var missing = await client.PostAsync(new Uri($"{ListEndpoint}/9999/trustnextread", UriKind.Relative), null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refuses_an_unknown_type_a_missing_file_and_a_sync_of_a_pasted_list()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var unknown = await client.PostAsync(
            new Uri(ListEndpoint, UriKind.Relative),
            SongApiTests.Json("""{"type":"spotify","name":"x","settings":{}}"""));
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var noFile = await client.PostAsync(
            new Uri(ListEndpoint, UriKind.Relative),
            SongApiTests.Json("""{"type":"csv","name":"x","settings":{}}"""));
        noFile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noFile.Content.ReadAsStringAsync()).Should().Contain("Upload a CSV file");

        using var pasted = await client.PostAsync(
            new Uri("/api/v1/song/bulk", UriKind.Relative),
            SongApiTests.Json("""{"text":"Queen - Bohemian Rhapsody"}"""));
        var pastedId = (await SongApiTests.ReadJsonAsync(pasted)).GetProperty("importListId").GetInt64();

        using var syncPasted = await client.PostAsync(new Uri($"{ListEndpoint}/{pastedId}/sync", UriKind.Relative), null);
        syncPasted.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var missing = await client.PostAsync(new Uri($"{ListEndpoint}/9999/sync", UriKind.Relative), null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> WaitForTerminalStatusAsync(HttpClient client, long id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var last = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(new Uri($"{CommandEndpoint}/{id}", UriKind.Relative));
            last = await SongApiTests.ReadJsonAsync(response);

            if (last.GetProperty("status").GetString() is "completed" or "failed" or "aborted")
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"Command {id} never finished; last seen as {last}");
    }
}
