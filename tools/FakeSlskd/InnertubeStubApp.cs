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

    /// <summary>The songs-filter <c>params</c> constant the client sends (InnertubeSearchRequest).</summary>
    public const string SongsParams = "bWVzaWNfZWdfeG1sX2ZpbHRlcnNfZGVzY3JpcHRvcg==";

    /// <summary>The videos-filter <c>params</c> constant the client sends.</summary>
    public const string VideosParams = "bWVzaWNfZWdfeG1sX2ZpbHRlcnNfdmlkZW9zX2Rlc2NyaXB0b3I=";

    /// <summary>Builds the stub. The caller starts it.</summary>
    /// <param name="options">Configuration; the fixture directory comes from the environment.</param>
    /// <param name="state">The state shared with the fake (the request log).</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var builder = FakeSlskdHost.CreateBuilder($"http://127.0.0.1:{options.InnertubePort}");

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new InnertubeFixtures(options.InnertubeFixtureDir));

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

    /// <summary>Loads the fixtures from a directory; a missing directory answers everything empty.</summary>
    /// <param name="directory">The <c>tests/fixtures/ytmusic</c> directory, or <c>null</c>.</param>
    public InnertubeFixtures(string? directory)
    {
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
