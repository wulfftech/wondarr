using System.Net;
using System.Text.Json;
using FluentAssertions;
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

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Exportify =
        "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\"\n"
        + "\"spotify:track:69kOkLUCkxIZYexIgSG8rq\",\"Get Lucky\",\"spotify:artist:1\",\"Daft Punk,Pharrell Williams\",\"spotify:album:1\",\"Random Access Memories\",\"\",\"Daft Punk\",\"2013-05-20\",\"\",\"1\",\"8\",\"369626\",\"\",\"false\",\"80\",\"USQX91300108\",\"\",\"\"\n"
        + "\"spotify:track:0000000000000000000000\",\"Nothing\",\"spotify:artist:0\",\"Nobody\",\"spotify:album:0\",\"None\",\"\",\"Nobody\",\"2020\",\"\",\"1\",\"1\",\"200000\",\"\",\"false\",\"1\",\"\",\"\",\"\"\n";

    [Fact]
    public async Task The_schema_names_the_csv_provider_and_its_column_fields()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{ListEndpoint}/schema", UriKind.Relative));
        var schema = await SongApiTests.ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        schema.EnumerateArray().Select(provider => provider.GetProperty("type").GetString()).Should()
            .Contain(["csv", "deezerPlaylist", "deezerArtistTop", "youtubeMusicPlaylist"]);

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
