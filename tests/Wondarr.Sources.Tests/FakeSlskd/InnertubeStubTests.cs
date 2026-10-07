using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using global::FakeSlskd; // qualified: this test namespace also ends in "FakeSlskd"
using FluentAssertions;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.FakeSlskd;

/// <summary>
/// The InnerTube stub and the AcoustID registration endpoint, exercised the way the Phase 4 gate
/// uses them: the app's own search client posts to the stub, and its parser reads the answer, so a
/// fixture that drifts away from the real InnerTube shape fails here rather than in the container.
/// </summary>
public sealed class InnertubeStubTests
{
    /// <summary>The repository's recorded fixtures, which the container mounts read-only.</summary>
    private static string FixturesDir
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory)
                .EnumerateParents()
                .FirstOrDefault(parent => parent.Name == "tests")?.Parent?.FullName;

            // The test bin tree: tests/Wondarr.Sources.Tests/bin/Debug/net10.0 -> up three levels.
            var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "ytmusic"));

            return Directory.Exists(candidate) ? candidate : Path.Combine(root ?? ".", "fixtures", "ytmusic");
        }
    }

    /// <summary>An ISRC the recorded fixture answers with the a-ha "Take On Me" card.</summary>
    private const string Isrc = "USWB19901214";

    [Fact]
    public void The_stub_uses_the_params_the_app_sends()
    {
        // Out of step once: the stub compared against made-up constants, so a videos search got the
        // songs fixture.
        InnertubeStubApp.SongsParams.Should().Be(InnertubeSearchRequest.SongsParams);
        InnertubeStubApp.VideosParams.Should().Be(InnertubeSearchRequest.VideosParams);
    }

    [Fact]
    public void A_gate_song_is_answered_from_the_scenario_not_the_fixtures()
    {
        var scenarioPath = Path.Combine(Path.GetTempPath(), $"phase4-scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(scenarioPath, """
            {
              "songs": {
                "Mr. Brightside": { "expect": "art-track", "videoId": "atvBright01", "artist": "The Killers", "durationSeconds": 222 },
                "Hey Ya!": { "expect": "omv-rejected", "videoId": "atvHeyYa001", "omvVideoId": "omvHeyYa001", "artist": "Outkast", "durationSeconds": 235 }
              },
              "videos": { "omvHeyYa001": { "durationSeconds": 255 } }
            }
            """);

        try
        {
            var fixtures = new InnertubeFixtures(FixturesDir, scenarioPath);

            var songs = InnertubeResponseParser.Parse(
                fixtures.Answer("The Killers - Mr. Brightside", InnertubeSearchRequest.SongsParams),
                "The Killers - Mr. Brightside",
                InnertubeSearchFilter.Songs);
            var atv = songs.Results.Should().ContainSingle().Subject;
            atv.VideoId.Should().Be("atvBright01");
            atv.Title.Should().Be("Mr. Brightside");
            atv.Artists.Should().Equal("The Killers");
            atv.DurationMs.Should().Be(222_000);
            atv.MusicVideoType.Should().Be("MUSIC_VIDEO_TYPE_ATV");

            // The OMV case: no Art Track, and the videos shelf holds the long official video.
            InnertubeResponseParser.Parse(fixtures.Answer("Outkast - Hey Ya!", InnertubeSearchRequest.SongsParams), "q", InnertubeSearchFilter.Songs)
                .Results.Should().BeEmpty();
            var omv = InnertubeResponseParser.Parse(
                    fixtures.Answer("Outkast - Hey Ya!", InnertubeSearchRequest.VideosParams),
                    "Outkast - Hey Ya!",
                    InnertubeSearchFilter.Videos)
                .Results.Should().ContainSingle().Subject;
            omv.VideoId.Should().Be("omvHeyYa001");
            omv.DurationMs.Should().Be(255_000);
            omv.MusicVideoType.Should().Be("MUSIC_VIDEO_TYPE_OMV");

            // ISRC queries find nothing while a gate scenario is loaded; other songs keep the fixtures.
            InnertubeResponseParser.Parse(fixtures.Answer(Isrc, null), Isrc, InnertubeSearchFilter.None)
                .TopResult.Should().BeNull();
            InnertubeResponseParser.Parse(fixtures.Answer("daft punk get lucky", InnertubeSearchRequest.SongsParams), "q", InnertubeSearchFilter.Songs)
                .Results.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(scenarioPath);
        }
    }

    [Fact]
    public async Task An_isrc_query_answers_the_recorded_card()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(
            "{}",
            innertubeFixtures: FixturesDir);

        harness.Innertube.Should().NotBeNull("the harness started the InnerTube stub");
        var client = harness.Innertube!;

        var results = await SearchAsync(client, Isrc, filter: null);

        // The card is the verified result: the ISRC query's own answer (spotDL's early return).
        results.TopResult.Should().NotBeNull("the ISRC fixture's card is the song");
        results.TopResult!.VideoId.Should().Be("HzdD8kbDzZA");
        results.TopResult.Title.Should().Be("Take on Me");
        results.TopResult.DurationMs.Should().Be(226000, "the card's subtitle carries 3:46");
        results.Results.Should().NotBeEmpty("the ISRC fixture also holds loosely related results");
    }

    [Fact]
    public async Task The_songs_filter_answers_the_recorded_art_tracks()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(
            "{}",
            innertubeFixtures: FixturesDir);

        harness.Innertube.Should().NotBeNull("the harness started the InnerTube stub");
        var client = harness.Innertube!;

        var results = await SearchAsync(client, "daft punk get lucky", InnertubeSearchFilter.Songs);

        results.TopResult.Should().BeNull("the songs fixture holds a shelf, not a card");
        results.Results.Should().HaveCountGreaterThan(3, "the fixture keeps six shelf results");

        var first = results.Results[0];

        first.VideoId.Should().Be("4D7u5KF7SP8");
        first.DurationMs.Should().Be(370000, "the first Art Track runs 6:10");
        first.MusicVideoType.Should().Be("MUSIC_VIDEO_TYPE_ATV");
    }

    [Fact]
    public async Task The_videos_filter_answers_the_recorded_ugc_reupload()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(
            "{}",
            innertubeFixtures: FixturesDir);

        harness.Innertube.Should().NotBeNull("the harness started the InnerTube stub");
        var client = harness.Innertube!;

        var results = await SearchAsync(client, "daft punk get lucky", InnertubeSearchFilter.Videos);

        results.Results.Should().NotBeEmpty();

        var first = results.Results[0];

        // The official-video hazard: a re-upload titled "Official Video" at 4:08 against the 6:10
        // song Ã¢â‚¬â€ the duration tolerance is what rejects it.
        first.MusicVideoType.Should().Be("MUSIC_VIDEO_TYPE_UGC");
        first.DurationMs.Should().Be(248000);
    }

    [Fact]
    public async Task Every_search_is_recorded_in_the_gate_log()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(
            "{}",
            innertubeFixtures: FixturesDir);

        harness.Innertube.Should().NotBeNull("the harness started the InnerTube stub");
        var client = harness.Innertube!;

        await SearchAsync(client, Isrc, filter: null);
        await SearchAsync(client, "daft punk get lucky", InnertubeSearchFilter.Songs);

        var log = await harness.GetJsonAsync("/fake/log");

        var searches = log["innertubeSearches"]!.AsArray();

        searches.Should().HaveCount(2, "the gate reads back what the app asked InnerTube for");
        searches[0]!["query"]!.GetValue<string>().Should().Be(Isrc);
        searches[0]!["params"]!.GetValue<string>().Should().BeEmpty("the ISRC query runs unfiltered");
        searches[1]!["query"]!.GetValue<string>().Should().Be("daft punk get lucky");
    }

    [Fact]
    public async Task A_registered_fingerprint_resolves_at_the_acoustid_stub()
    {
        await using var harness = await FakeSlskdHarness.StartAsync("{}");

        // The Phase 4 gate's registration: the fake yt-dlp posts the fingerprint of the Opus file it
        // generated together with the recording the gate wants it to verify as.
        var registration = new
        {
            fingerprint = "AQAAAEoUk0kU0kU0",
            recordingId = "833f00e1-781f-4edd-90e4-e52712618862",
            title = "Get Lucky",
            artists = new[] { new { id = "056e4f3e-d505-4dad-8ec1-d04f521cbb56", name = "Daft Punk" } },
            durationSeconds = 370,
        };

        using var register = await harness.AcoustId.PostAsJsonAsync("/v2/register", registration);
        register.StatusCode.Should().Be(HttpStatusCode.OK);

        // The app's own verification posts the form the real AcoustID client posts.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = "gate",
            ["fingerprint"] = "AQAAAEoUk0kU0kU0",
        });

        using var lookup = await harness.AcoustId.PostAsync("/v2/lookup", content);
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonNode.Parse(await lookup.Content.ReadAsStringAsync())!;

        body["status"]!.GetValue<string>().Should().Be("ok");

        var recording = body["results"]!.AsArray()[0]!["recordings"]!.AsArray()[0]!;

        recording["id"]!.GetValue<string>().Should().Be("833f00e1-781f-4edd-90e4-e52712618862");
        recording["title"]!.GetValue<string>().Should().Be("Get Lucky");
    }

    /// <summary>Posts one search the way the app's client does, and parses it with the app's parser.</summary>
    private static async Task<InnertubeSearchResult> SearchAsync(
        HttpClient client,
        string query,
        InnertubeSearchFilter? filter)
    {
        var body = new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20261005.01.00", hl = "en" }, user = new { } },
            query,
            @params = filter is { } value ? ParamsFor(value) : null,
        };

        using var response = await client.PostAsJsonAsync("youtubei/v1/search?alt=json", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();

        return InnertubeResponseParser.Parse(json, query, filter ?? InnertubeSearchFilter.None);
    }

    /// <summary>The <c>params</c> constant each filter carries, as the app's client sends them.</summary>
    private static string ParamsFor(InnertubeSearchFilter filter) => filter switch
    {
        InnertubeSearchFilter.Songs => InnertubeSearchRequest.SongsParams,
        InnertubeSearchFilter.Videos => InnertubeSearchRequest.VideosParams,
        _ => string.Empty,
    };
}

/// <summary>Enumerates a directory's parents until the root.</summary>
internal static class DirectoryInfoExtensions
{
    public static IEnumerable<DirectoryInfo> EnumerateParents(this DirectoryInfo start)
    {
        var current = start;

        while (current.Parent is { } parent)
        {
            yield return current;

            current = parent;
        }
    }
}
