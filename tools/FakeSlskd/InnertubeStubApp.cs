using System.Text.Json;

namespace FakeSlskd;

/// <summary>
/// The InnerTube stand-in: the YouTube source's search client is pointed at this app through
/// <c>youtube.base_url</c>, and it answers <c>POST /youtubei/v1/search?alt=json</c> from the
/// recorded fixtures under <c>tests/fixtures/ytmusic/</c>, keyed by the request's query and filter.
/// CI cannot reach music.youtube.com; the fixtures are real responses, so the parser sees the
/// verbatim shape. A test tool — never part of the image.
/// </summary>
public static class InnertubeStubApp
{
    /// <summary>The search path the client posts to, relative to the base URL.</summary>
    public const string SearchPath = "/youtubei/v1/search";

    /// <summary>The songs-filter <c>params</c> constant the client sends (<c>InnertubeModels.SongsParams</c>).</summary>
    public const string SongsParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    /// <summary>The videos-filter <c>params</c> constant the client sends (<c>InnertubeModels.VideosParams</c>).</summary>
    public const string VideosParams = "EgWKAQIQAWoMEA4QChADEAQQCRAF";

    /// <summary>Builds the stub. The caller starts it.</summary>
    /// <param name="options">Configuration; the fixture directory comes from the environment.</param>
    /// <param name="state">The state shared with the fake (the request log).</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var builder = FakeSlskdHost.CreateBuilder($"http://127.0.0.1:{options.InnertubePort}");

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new InnertubeFixtures(options.InnertubeFixtureDir, options.YouTubeScenarioPath));

        var app = builder.Build();

        app.Use(FakeSlskdHost.LogRequestsAsync);

        app.MapPost(SearchPath, async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            var root = document.RootElement;

            var query = root.TryGetProperty("query", out var queryElement)
                ? queryElement.GetString()
                : null;
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.Json(
                    new { error = new { code = 400, message = "Invalid request." } },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            state.RecordInnertubeSearch(query, parameters ?? string.Empty);

            var fixtures = context.RequestServices.GetRequiredService<InnertubeFixtures>();

            return Results.Text(
                fixtures.Answer(query, parameters),
                "application/json");
        });

        return app;
    }
}

/// <summary>
/// The recorded responses, keyed by what the client asked for. The fixture files are named
/// <c>search-&lt;kind&gt;.json</c> (<c>songs</c>, <c>videos</c>, <c>isrc</c>); a query that carries an
/// ISRC-shaped string (two letters, five digits, seven more characters) is answered by the ISRC
/// fixture, a query with the songs <c>params</c> by the songs fixture, and so on. A query with no
/// matching fixture answers the songs fixture with its results emptied — the shape of a search that
/// found nothing, which is how a "missing on Soulseek" gate case is expressed.
/// </summary>
public sealed class InnertubeFixtures
{
    /// <summary>The ISRC shape: two country letters, a three-character registrant, a two-digit year, a five-character designator.</summary>
    private const string IsrcPattern = @"^[A-Z]{2}[A-Z0-9]{3}\d{2}[A-Z0-9]{5}$";

    private readonly string? _isrc;
    private readonly string? _songs;
    private readonly string? _videos;
    private readonly string? _scenarioPath;

    /// <summary>Loads the fixtures from a directory; a missing directory answers everything empty.</summary>
    /// <param name="directory">The <c>tests/fixtures/ytmusic</c> directory, or <c>null</c>.</param>
    public InnertubeFixtures(string? directory, string? scenarioPath = null)
    {
        _scenarioPath = scenarioPath;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _isrc = Read(directory, "search-isrc.json");
        _songs = Read(directory, "search-songs.json");
        _videos = Read(directory, "search-videos.json");
    }

    /// <summary>The recorded body for a query, or an empty songs shelf when nothing matches.</summary>
    /// <param name="query">The search text.</param>
    /// <param name="parameters">The filter's <c>params</c>, or <c>null</c> for an unfiltered query.</param>
    public string Answer(string query, string? parameters)
    {
        // The Phase 4 gate's songs: answered from its scenario (the same file the fake yt-dlp reads), so a
        // search for a gate song finds that song's Art Track (or official video) and nothing else. The
        // recorded fixtures are one real song's results; every other song would only see a mismatch.
        if (GateScenario.Load(_scenarioPath) is { } scenario)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(query, IsrcPattern))
            {
                return EmptyShelf;
            }

            if (scenario.Find(query) is { } song)
            {
                var videos = string.Equals(parameters, InnertubeStubApp.VideosParams, StringComparison.Ordinal);

                return (videos, song.OmvVideoId, song.Expect) switch
                {
                    (true, { } omv, _) => Shelf(omv, song.Title + " (Official Video)", song.Artist, scenario.DurationOf(omv), "MUSIC_VIDEO_TYPE_OMV"),
                    (true, null, _) => EmptyShelf,
                    (false, _, "omv-rejected") => EmptyShelf,
                    _ => Shelf(song.VideoId, song.Title, song.Artist, song.DurationSeconds, "MUSIC_VIDEO_TYPE_ATV"),
                };
            }
        }

        // An ISRC query runs unfiltered (the recorded lesson: the songs filter on an ISRC returns junk).
        if (System.Text.RegularExpressions.Regex.IsMatch(query, IsrcPattern) && _isrc is not null)
        {
            return _isrc;
        }

        if (string.Equals(parameters, InnertubeStubApp.VideosParams, StringComparison.Ordinal) && _videos is not null)
        {
            return _videos;
        }

        if (_songs is not null)
        {
            return _songs;
        }

        return EmptyShelf;
    }

    /// <summary>The shape of a search that found nothing: the shelves are empty.</summary>
    /// <summary>A songs shelf with one item, in the shape of the recorded fixtures' items.</summary>
    /// <param name="videoId">The item's video id.</param>
    /// <param name="title">The item's title.</param>
    /// <param name="artist">The one artist it credits.</param>
    /// <param name="durationSeconds">Its length.</param>
    /// <param name="musicVideoType">ATV (Art Track) or OMV (official video).</param>
    public static string Shelf(string videoId, string title, string artist, int durationSeconds, string musicVideoType)
    {
        object Watch() => new
        {
            watchEndpoint = new
            {
                videoId,
                watchEndpointMusicSupportedConfigs = new { watchEndpointMusicConfig = new { musicVideoType } },
            },
        };

        var credits = new object[]
        {
            new { text = artist, navigationEndpoint = new { browseEndpoint = new { browseId = "UCgate" + Math.Abs(StringComparer.Ordinal.GetHashCode(artist) % 100000) } } },
            new { text = " \u2022 " },
            new { text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{durationSeconds / 60}:{durationSeconds % 60:00}") },
        };

        var item = new
        {
            musicResponsiveListItemRenderer = new
            {
                overlay = new
                {
                    musicItemThumbnailOverlayRenderer = new
                    {
                        content = new { musicPlayButtonRenderer = new { playNavigationEndpoint = Watch() } },
                    },
                },
                flexColumns = new object[]
                {
                    new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = new object[] { new { text = title, navigationEndpoint = Watch() } } } } },
                    new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = credits } } },
                },
                playlistItemData = new { videoId },
            },
        };

        var shelfSection = new { musicShelfRenderer = new { contents = new object[] { item } } };
        var document = new
        {
            contents = new
            {
                tabbedSearchResultsRenderer = new
                {
                    tabs = new object[]
                    {
                        new
                        {
                            tabRenderer = new
                            {
                                title = "YT Music",
                                selected = true,
                                content = new { sectionListRenderer = new { contents = new object[] { shelfSection } } },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(document);
    }

    public static string EmptyShelf { get; } = """
        {
          "contents": {
            "tabbedSearchResultsRenderer": {
              "tabs": [
                {
                  "tabRenderer": {
                    "title": "YT Music",
                    "selected": true,
                    "content": {
                      "sectionListRenderer": {
                        "contents": []
                      }
                    }
                  }
                }
              ]
            }
          }
        }
        """;

    private static string? Read(string directory, string file)
    {
        var path = Path.Combine(directory, file);

        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

/// <summary>
/// The parts of the Phase 4 gate scenario (<c>FAKE_YT_SCENARIO</c>, written by
/// <c>scripts/phase4-scenario.py</c>) the InnerTube stub answers from: each gate song's title, artist,
/// length, Art Track and optional official video. Read on every search, so the file may change.
/// </summary>
public sealed class GateScenario
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the gate songs by title.</summary>
    public Dictionary<string, GateSong> Songs { get; init; } = [];

    /// <summary>Gets the videos the fake yt-dlp knows, by id (only the length is read here).</summary>
    public Dictionary<string, GateVideo> Videos { get; init; } = [];

    /// <summary>Reads the scenario, or <see langword="null"/> when there is none.</summary>
    /// <param name="path">The scenario file.</param>
    /// <returns>The scenario.</returns>
    public static GateScenario? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var scenario = JsonSerializer.Deserialize<GateScenario>(File.ReadAllText(path), Options);

        if (scenario is not null)
        {
            foreach (var (title, song) in scenario.Songs)
            {
                song.Title = title;
            }
        }

        return scenario;
    }

    /// <summary>The gate song whose title the query contains (letters and digits only, any case).</summary>
    /// <param name="query">The search query.</param>
    /// <returns>The song, or <see langword="null"/>.</returns>
    public GateSong? Find(string query)
    {
        var folded = Fold(query);

        return Songs.Values
            .Where(song => !string.IsNullOrEmpty(song.Artist) && folded.Contains(Fold(song.Title), StringComparison.Ordinal))
            .OrderByDescending(song => song.Title.Length)
            .FirstOrDefault();
    }

    /// <summary>A video's length in seconds, or 240 when the scenario does not say.</summary>
    /// <param name="videoId">The video.</param>
    /// <returns>Its length.</returns>
    public int DurationOf(string videoId) =>
        Videos.TryGetValue(videoId, out var video) && video.DurationSeconds > 0 ? video.DurationSeconds : 240;

    private static string Fold(string text) =>
        new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}

/// <summary>One gate song.</summary>
public sealed class GateSong
{
    /// <summary>Gets or sets the song's title (the scenario's key).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets <c>art-track</c>, <c>omv-rejected</c> or <c>bot-check</c>.</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>Gets the Art Track's video id.</summary>
    public string VideoId { get; init; } = string.Empty;

    /// <summary>Gets the official video's id, for the OMV case.</summary>
    public string? OmvVideoId { get; init; }

    /// <summary>Gets the main artist the item credits.</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Gets the Art Track's length in seconds.</summary>
    public int DurationSeconds { get; init; }
}

/// <summary>One video of the gate scenario.</summary>
public sealed class GateVideo
{
    /// <summary>Gets the video's length in seconds.</summary>
    public int DurationSeconds { get; init; }
}
