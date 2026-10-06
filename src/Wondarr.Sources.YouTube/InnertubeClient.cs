using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>The YouTube Music half of the YouTube source: the InnerTube <c>search</c> call.</summary>
public interface IInnertubeClient
{
    /// <summary>Runs one search and parses its response.</summary>
    Task<InnertubeSearchResult> SearchAsync(InnertubeSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// InnerTube refused the search. A 400 is a bug in the request, a 429 or 503 is YouTube rate limiting
/// or being down: whether either is worth retrying is the caller's call, so nothing is retried here.
/// </summary>
public sealed class InnertubeException : Exception
{
    /// <param name="statusCode">The status InnerTube answered with.</param>
    /// <param name="detail">The first line of what the error body said.</param>
    public InnertubeException(HttpStatusCode statusCode, string detail)
        : base($"InnerTube search failed with HTTP {(int)statusCode} {statusCode}: {detail}")
    {
        StatusCode = statusCode;
    }

    /// <summary>The status InnerTube answered with.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// A small, keyless InnerTube client (research_youtube.md §0.1): it posts the WEB_REMIX <c>search</c>
/// request ytmusicapi posts — no API key, no cookies beyond the consent one, no visitor id — and hands
/// the response to <see cref="InnertubeResponseParser"/>. One POST per search; nothing is cached.
/// </summary>
public sealed class InnertubeClient : IInnertubeClient
{
    /// <summary>The name of the <see cref="HttpClient"/> the registration builds for this client.</summary>
    public const string HttpClientName = "innertube";

    /// <summary>The search endpoint, relative to the base address (music.youtube.com).</summary>
    public const string SearchPath = "youtubei/v1/search?alt=json";

    /// <summary>The InnerTube client name for YouTube Music.</summary>
    public const string ClientName = "WEB_REMIX";

    /// <summary>
    /// The Firefox 88 user agent ytmusicapi sends. A browser UA keeps InnerTube from serving the
    /// embedded-player variants of the response; the exact version is what ytmusicapi pins.
    /// </summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0";

    /// <summary>The consent cookie: no consent wall, no personalised results.</summary>
    public const string ConsentCookie = "SOCS=CAI";

    /// <summary>How much of an error body ends up in the exception message.</summary>
    private const int MaxErrorDetailLength = 500;

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly TimeProvider _timeProvider;

    public InnertubeClient(HttpClient http, IOptionsMonitor<YouTubeOptions> options, TimeProvider timeProvider)
    {
        _http = http;
        _options = options;
        _timeProvider = timeProvider;
        _http.BaseAddress ??= new Uri(options.CurrentValue.BaseUrl, UriKind.Absolute);
    }

    public async Task<InnertubeSearchResult> SearchAsync(InnertubeSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchPath)
        {
            Content = Body(request),
        };

        message.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        message.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
        message.Headers.TryAddWithoutValidation("Accept", "*/*");

        // Headers first: the body is hundreds of kilobytes and is only read once the status is known.
        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InnertubeException(response.StatusCode, ErrorDetail(body));
        }

        return InnertubeResponseParser.Parse(body, request.Query, request.Filter);
    }

    /// <summary>The request body: the WEB_REMIX context, the query and the filter's <c>params</c>.</summary>
    private ByteArrayContent Body(InnertubeSearchRequest request)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("context");
            writer.WriteStartObject("client");
            writer.WriteString("clientName", ClientName);
            writer.WriteString("clientVersion", ClientVersion());
            writer.WriteString("hl", "en");
            writer.WriteEndObject();
            writer.WriteStartObject("user");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteString("query", request.Query);

            if (request.Params is { } parameters)
            {
                writer.WriteString("params", parameters);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// The client version InnerTube expects: today's date, as ytmusicapi computes it per request
    /// ("1.20261005.01.00" on 2026-10-05). A stale one still works; a future one does not.
    /// </summary>
    private string ClientVersion() =>
        string.Concat(
            "1.",
            _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ".01.00");
    /// <summary>
    /// The first line of what InnerTube said, for the exception message. A JSON error body's own
    /// message is used when there is one (the recorded 400's is "Invalid JSON payload received. …");
    /// anything else falls back to the body's first line, so a wall of HTML never reaches a log.
    /// </summary>
    private static string ErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = document.RootElement;
            var message = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var error)
                ? Text(error, "message")
                : Text(root, "message");

            if (message is not null)
            {
                return FirstLine(message);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw body's first line is all there is.
        }

        return FirstLine(body);
    }

    /// <summary>The string value of one property, when it is a string.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Everything before the first line break, trimmed and bounded.</summary>
    private static string FirstLine(string text)
    {
        var line = text;
        var end = text.IndexOfAny(['\r', '\n']);

        if (end >= 0)
        {
            line = text[..end];
        }

        line = line.Trim();
        return line.Length <= MaxErrorDetailLength ? line : string.Concat(line[..MaxErrorDetailLength], "…");
    }
}
