using System.Net;
using System.Text.Json;
using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Compilarr.Api.Tests;

public sealed class WantedHistoryBlocklistApiTests
{
    private const string MissingEndpoint = "/api/v1/wanted/missing";
    private const string CutoffEndpoint = "/api/v1/wanted/cutoff";
    private const string HistoryEndpoint = "/api/v1/history";
    private const string BlocklistEndpoint = "/api/v1/blocklist";

    private const long Mp3256 = 23;

    [Fact]
    public async Task Every_lifecycle_endpoint_requires_the_api_key()
    {
        using var factory = new CompilarrAppFactory();
        using var client = factory.CreateClient();

        foreach (var endpoint in new[] { MissingEndpoint, CutoffEndpoint, HistoryEndpoint, BlocklistEndpoint })
        {
            using var response = await client.GetAsync(new Uri(endpoint, UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"GET {endpoint} is behind the API key");
        }

        using var deleted = await client.DeleteAsync(new Uri($"{BlocklistEndpoint}/1", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wanted_lists_the_missing_song_and_the_one_below_its_cutoff()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);
        await SeedSongsAsync(factory, withFileQualityId: ("Gamma", Mp3256));

        using var missing = await client.GetAsync(new Uri(MissingEndpoint, UriKind.Relative));
        var missingPage = await ReadJsonAsync(missing);

        missing.StatusCode.Should().Be(HttpStatusCode.OK);
        missingPage.GetProperty("totalRecords").GetInt32().Should().Be(1);
        missingPage.GetProperty("page").GetInt32().Should().Be(1);
        missingPage.GetProperty("pageSize").GetInt32().Should().Be(20);
        missingPage.GetProperty("sortKey").GetString().Should().Be("added");
        missingPage.GetProperty("sortDirection").GetString().Should().Be("descending");

        var song = missingPage.GetProperty("records")[0];
        song.GetProperty("title").GetString().Should().Be("Alpha");
        song.GetProperty("hasFile").GetBoolean().Should().BeFalse();
        song.GetProperty("qualityId").ValueKind.Should().Be(JsonValueKind.Null);
        song.GetProperty("albumContext").ValueKind.Should().Be(JsonValueKind.Null);
        song.GetProperty("artistCredit").GetString().Should().Be("Aphex Twin");

        using var cutoff = await client.GetAsync(new Uri(CutoffEndpoint, UriKind.Relative));
        var cutoffPage = await ReadJsonAsync(cutoff);

        cutoffPage.GetProperty("totalRecords").GetInt32().Should().Be(1);
        cutoffPage.GetProperty("records")[0].GetProperty("title").GetString().Should().Be("Gamma");
        cutoffPage.GetProperty("records")[0].GetProperty("hasFile").GetBoolean().Should().BeTrue();
        cutoffPage.GetProperty("records")[0].GetProperty("qualityId").GetInt64().Should().Be(Mp3256);
    }

    [Fact]
    public async Task History_is_newest_first_filters_by_song_and_event_and_returns_data_as_an_object()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);
        var songs = await SeedSongsAsync(factory, withFileQualityId: null);

        await SeedHistoryAsync(
            factory,
            (songs[0], HistoryEventType.Grabbed, """{"candidate":"peer:one"}"""),
            (songs[1], HistoryEventType.Imported, """{"path":"/data/music/b.mp3"}"""),
            (songs[0], HistoryEventType.Imported, "{}"));

        using var all = await client.GetAsync(new Uri(HistoryEndpoint, UriKind.Relative));
        var page = await ReadJsonAsync(all);

        all.StatusCode.Should().Be(HttpStatusCode.OK);
        page.GetProperty("totalRecords").GetInt32().Should().Be(3);
        page.GetProperty("sortKey").GetString().Should().Be("date");

        var newest = page.GetProperty("records")[0];
        newest.GetProperty("eventType").GetString().Should().Be("imported");
        newest.GetProperty("songId").GetInt64().Should().Be(songs[0]);
        newest.GetProperty("song").GetProperty("title").GetString().Should().Be("Alpha");
        newest.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object);

        var grabbed = page.GetProperty("records")[2];
        grabbed.GetProperty("eventType").GetString().Should().Be("grabbed");
        grabbed.GetProperty("data").GetProperty("candidate").GetString().Should().Be("peer:one");
        grabbed.GetProperty("date").ValueKind.Should().Be(JsonValueKind.String);

        using var bySong = await client.GetAsync(new Uri($"{HistoryEndpoint}?songId={songs[0]}", UriKind.Relative));
        (await ReadJsonAsync(bySong)).GetProperty("totalRecords").GetInt32().Should().Be(2);

        using var byType = await client.GetAsync(new Uri($"{HistoryEndpoint}?eventType=grabbed", UriKind.Relative));
        var grabbedPage = await ReadJsonAsync(byType);

        grabbedPage.GetProperty("totalRecords").GetInt32().Should().Be(1);
        grabbedPage.GetProperty("records")[0].GetProperty("songId").GetInt64().Should().Be(songs[0]);
    }

    [Fact]
    public async Task Deleting_a_blocklist_entry_returns_200_and_then_404()
    {
        using var factory = new CompilarrAppFactory();
        using var client = Authenticated(factory);
        var songs = await SeedSongsAsync(factory, withFileQualityId: null);
        var id = await SeedBlocklistAsync(factory, songs[0], "slskd", "peer:one");

        using var listed = await client.GetAsync(new Uri(BlocklistEndpoint, UriKind.Relative));
        var page = await ReadJsonAsync(listed);

        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        page.GetProperty("totalRecords").GetInt32().Should().Be(1);

        var resource = page.GetProperty("records")[0];
        resource.GetProperty("id").GetInt64().Should().Be(id);
        resource.GetProperty("songId").GetInt64().Should().Be(songs[0]);
        resource.GetProperty("sourceType").GetString().Should().Be("slskd");
        resource.GetProperty("blocklistKey").GetString().Should().Be("peer:one");
        resource.GetProperty("reason").GetString().Should().Be("Verification failed");
        resource.GetProperty("expiresAt").ValueKind.Should().Be(JsonValueKind.Null);

        using var deleted = await client.DeleteAsync(new Uri($"{BlocklistEndpoint}/{id}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await client.DeleteAsync(new Uri($"{BlocklistEndpoint}/{id}", UriKind.Relative));
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient Authenticated(CompilarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>
    /// Seeds "Alpha" (monitored, no file) and, when <paramref name="withFileQualityId"/> is given,
    /// a second monitored song with a file at that quality. Returns the two song ids in that order.
    /// </summary>
    private static async Task<long[]> SeedSongsAsync(
        CompilarrAppFactory factory,
        (string Title, long QualityId)? withFileQualityId)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        var artist = new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var alpha = NewSong(artist, "Alpha");
        context.Songs.Add(alpha);
        await context.SaveChangesAsync();

        var second = NewSong(artist, withFileQualityId?.Title ?? "Bravo");
        context.Songs.Add(second);
        await context.SaveChangesAsync();

        if (withFileQualityId is { } file)
        {
            context.SongFiles.Add(new SongFile
            {
                SongId = second.Id,
                Path = "/data/music/gamma.mp3",
                Size = 1024,
                Codec = "mp3",
                Container = "mpeg",
                QualityId = file.QualityId,
                SourceType = "soulseek",
                ImportedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        return [alpha.Id, second.Id];
    }

    private static async Task SeedHistoryAsync(
        CompilarrAppFactory factory,
        params (long SongId, HistoryEventType EventType, string Data)[] rows)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        foreach (var (songId, eventType, data) in rows)
        {
            context.History.Add(new HistoryItem { SongId = songId, EventType = eventType, Data = data });
            await context.SaveChangesAsync();
        }
    }

    private static async Task<long> SeedBlocklistAsync(
        CompilarrAppFactory factory,
        long songId,
        string sourceType,
        string key)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        var item = new BlocklistItem
        {
            SongId = songId,
            SourceType = sourceType,
            BlocklistKey = key,
            Reason = "Verification failed",
        };

        context.Blocklist.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    private static Song NewSong(Artist artist, string title) => new()
    {
        Title = title,
        ArtistCredit = artist.Name,
        PrimaryArtistId = artist.Id,
        QualityProfileId = SeedData.StandardProfileId,
        LibraryId = SeedData.DefaultLibraryId,
        AddedBy = "api",
    };
}
