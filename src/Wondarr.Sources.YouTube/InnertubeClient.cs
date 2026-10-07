using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>The YouTube Music half of the YouTube source: the InnerTube <c>search</c> and <c>browse</c> calls.</summary>
public interface IInnertubeClient
{
    /// <summary>Runs one search and parses its response.</summary>
    Task<InnertubeSearchResult> SearchAsync(InnertubeSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every row of a public or unlisted playlist, in playlist order, paging through InnerTube's
    /// continuations. Reading a playlist is metadata only: it does not need the YouTube source enabled.
    /// </summary>
    /// <param name="playlistId">The playlist id, with or without its <c>VL</c> browse prefix.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InnertubeException">InnerTube refused the request.</exception>
    Task<IReadOnlyList<InnertubePlaylistRow>> BrowsePlaylistAsync(string playlistId, CancellationToken cancellationToken);
}

/// <summary>
/// InnerTube refused the request. A 400 is a bug in the request, a 404 is an unknown playlist, and a
/// 429 or 503 is YouTube rate limiting or being down: whether any of them is worth retrying is the
/// caller's call, so nothing is retried here.
/// </summary>
public sealed class InnertubeException : Exception
{
    /// <param name="statusCode">The status InnerTube answered with.</param>
    /// <param name="detail">The first line of what the error body said.</param>
    /// <param name="operation">What the call was, for the message (<c>search</c>, <c>browse</c>).</param>
    public InnertubeException(HttpStatusCode statusCode, string detail, string operation = "search")
        : base($"InnerTube {operation} failed with HTTP {(int)statusCode} {statusCode}: {detail}")
    {
        StatusCode = statusCode;
    }

    /// <summary>The status InnerTube answered with.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// A small, keyless InnerTube client (research_youtube.md §0.1): it posts the WEB_REMIX <c>search</c>
/// and <c>browse</c> requests ytmusicapi posts — no API key, no cookies beyond the consent one, no
/// visitor id — and hands the responses to <see cref="InnertubeResponseParser"/> and
/// <see cref="InnertubePlaylistParser"/>. One POST per page; nothing is cached.
/// </summary>
public sealed class InnertubeClient : IInnertubeClient
{
    /// <summary>The name of the <see cref="HttpClient"/> the registration builds for this client.</summary>
    public const string HttpClientName = "innertube";

    /// <summary>The search endpoint, relative to the base address (music.youtube.com).</summary>
    public const string SearchPath = "youtubei/v1/search?alt=json";

    /// <summary>The browse endpoint, relative to the base address (music.youtube.com).</summary>
    public const string BrowsePath = "youtubei/v1/browse?alt=json";

    /// <summary>The browse id prefix of a playlist page.</summary>
    public const string PlaylistBrowsePrefix = "VL";

    /// <summary>At most 100 pages of a playlist: 10 000 rows, more than any playlist needs.</summary>
    public const int MaxPlaylistPages = 100;

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

        var body = await PostAsync(SearchPath, Body(request), cancellationToken).ConfigureAwait(false);

        return InnertubeResponseParser.Parse(body, request.Query, request.Filter);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InnertubePlaylistRow>> BrowsePlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var browseId = playlistId.StartsWith(PlaylistBrowsePrefix, StringComparison.Ordinal)
            ? playlistId
            : string.Concat(PlaylistBrowsePrefix, playlistId);

        var rows = new List<InnertubePlaylistRow>();
        string? continuation = null;

        for (var page = 0; page < MaxPlaylistPages; page++)
        {
            var body = await PostAsync(BrowsePath, BrowseBody(browseId, continuation), cancellationToken)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var (pageRows, next) = continuation is null
                ? InnertubePlaylistParser.ParseFirstPage(document.RootElement)
                : InnertubePlaylistParser.ParseContinuation(document.RootElement);

            rows.AddRange(pageRows);

            if (next is null || pageRows.Count == 0)
            {
                break;
            }

            continuation = next;
        }

        return rows;
    }

    /// <summary>
    /// Posts one request and reads its body. Headers first: the body is hundreds of kilobytes and is
    /// only read once the status is known.
    /// </summary>
    /// <exception cref="InnertubeException">InnerTube answered with anything but a success status.</exception>
    private async Task<string> PostAsync(string relativeUri, ByteArrayContent content, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, relativeUri)
        {
            Content = content,
        };

        message.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        message.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
        message.Headers.TryAddWithoutValidation("Accept", "*/*");

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? body
            : throw new InnertubeException(response.StatusCode, ErrorDetail(body), Operation(relativeUri));
    }

    /// <summary>What the call was, for the exception's message.</summary>
    private static string Operation(string relativeUri) =>
        relativeUri.Contains("/browse", StringComparison.Ordinal) ? "browse" : "search";

    /// <summary>The request body: the WEB_REMIX context, the query and the filter's <c>params</c>.</summary>
    private ByteArrayContent Body(InnertubeSearchRequest request)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writer.WriteString("query", request.Query);

            if (request.Params is { } parameters)
            {
                writer.WriteString("params", parameters);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Json(buffer);
    }

    /// <summary>
    /// A browse request's body: the WEB_REMIX context and either the playlist's browse id (the first
    /// page) or the continuation token of the page before it (every page after).
    /// </summary>
    private ByteArrayContent BrowseBody(string browseId, string? continuation)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteContext(writer);

            if (continuation is null)
            {
                writer.WriteString("browseId", browseId);
            }
            else
            {
                writer.WriteString("continuation", continuation);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Json(buffer);
    }

    /// <summary>The WEB_REMIX context ytmusicapi sends: the client, today's version, English.</summary>
    private void WriteContext(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("context");
        writer.WriteStartObject("client");
        writer.WriteString("clientName", ClientName);
        writer.WriteString("clientVersion", ClientVersion());
        writer.WriteString("hl", "en");
        writer.WriteEndObject();
        writer.WriteStartObject("user");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The body as JSON content, ready to send.</summary>
    private static ByteArrayContent Json(MemoryStream buffer)
    {
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
