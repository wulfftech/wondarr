// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Gazelle/GazelleParser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Kept from the original (with GazelleApi.cs's response shapes and GazelleRequestGenerator.cs's
// browse request): ajax.php?action=browse with artistname and groupname, one release per torrent of
// each group, the title "artist - group (year) [format encoding] [media]" (+ " [Cue]"), the info URL,
// the freeleech flags and the "usetoken=1 only when a token can be used" rule. Changed: the API key
// travels in the Authorization header instead of a cookie login; downloads use
// ajax.php?action=download. Added: the file list of the best-seeded torrents
// (ajax.php?action=torrent, "name{{{size}}}|||..."), a 5-requests-per-10-seconds limit per row, and
// Wondarr's typed errors.

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>A Gazelle row's settings.</summary>
/// <param name="Url">The tracker's address, for example <c>https://redacted.sh</c>.</param>
/// <param name="ApiKey">The user's API key; a secret, sent only as the <c>Authorization</c> header.</param>
/// <param name="UseFreeleechTokens">Whether a download spends a freeleech token when it can.</param>
public sealed record GazelleSettings(string Url, string ApiKey, bool UseFreeleechTokens)
{
    /// <summary>Reads a row's (or a draft's) settings.</summary>
    /// <param name="settings">The settings JSON.</param>
    public static GazelleSettings Read(JsonElement settings) => new(
        Text(settings, "url").Trim().TrimEnd('/'),
        Text(settings, "apiKey").Trim(),
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("useFreeleechTokens", out var token) && token.ValueKind == JsonValueKind.True);

    private static string Text(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>The Gazelle indexer type: a tracker (Redacted, Orpheus) searched directly with the user's API key.</summary>
public sealed class GazelleIndexerType : IIndexerType
{
    private readonly GazelleClient _client;

    /// <summary>Initialises a new instance of the <see cref="GazelleIndexerType"/> class.</summary>
    /// <param name="client">The Gazelle client the test goes through.</param>
    public GazelleIndexerType(GazelleClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <inheritdoc />
    public string Type => "gazelle";

    /// <inheritdoc />
    public DownloadProtocol? Protocol => DownloadProtocol.Torrent;

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "text", Required: true, HelpText: "The tracker's address, for example https://redacted.sh"),
        new("apiKey", "API key", "password", Required: true, Secret: true, HelpText: "Your API key from the tracker's settings (it needs the torrents scope)."),
        new("useFreeleechTokens", "Use freeleech tokens", "checkbox", Required: false, Advanced: true, HelpText: "Spend a token on each download that is not freeleech already."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = GazelleSettings.Read(settings);
        var messages = new List<string>();

        if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            messages.Add("The URL must be an http(s) address, for example https://redacted.sh.");
        }

        if (parsed.ApiKey.Length == 0)
        {
            messages.Add("An API key is required.");
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        if (Validate(settings) is { Count: > 0 } problems)
        {
            return new ProviderTestResult(false, problems[0]);
        }

        try
        {
            await _client.IndexAsync(0, GazelleSettings.Read(settings), cancellationToken).ConfigureAwait(false);
            return new ProviderTestResult(true, null);
        }
        catch (IndexerException exception)
        {
            return new ProviderTestResult(false, exception.Message);
        }
    }
}

/// <summary>
/// Searches and downloads from a Gazelle tracker (ADR-0009): the browse, one release per torrent, the
/// file lists of the five best-seeded torrents read before anything is downloaded, and at most five
/// requests in any ten seconds per row (the trackers' published API limit).
/// </summary>
public sealed partial class GazelleClient : IIndexerClient, IDisposable
{
    /// <summary>How many torrents of one answer have their file lists read.</summary>
    public const int FileListsPerSearch = 5;

    /// <summary>How many requests a row may send in <see cref="Window"/>.</summary>
    public const int RequestsPerWindow = 5;

    /// <summary>The tracker's rate-limit window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _clients;
    private readonly ISecretRegistry _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<GazelleClient> _logger;
    private readonly Lock _rowsGate = new();
    private readonly Dictionary<long, RowLimit> _rows = [];

    /// <summary>Initialises a new instance of the <see cref="GazelleClient"/> class.</summary>
    /// <param name="clients">Creates the indexer HTTP client.</param>
    /// <param name="secrets">The API key is registered here.</param>
    /// <param name="time">The clock the rate limit runs on.</param>
    /// <param name="logger">Logs paths and counts, never the key.</param>
    public GazelleClient(IHttpClientFactory clients, ISecretRegistry secrets, TimeProvider time, ILogger<GazelleClient> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _secrets = secrets;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_rowsGate)
        {
            foreach (var row in _rows.Values)
            {
                row.Turn.Dispose();
            }

            _rows.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(query);

        var settings = GazelleSettings.Read(NotificationSecrets.Read(indexer.Settings));
        var path = string.Concat(
            "/ajax.php?action=browse&artistname=", Uri.EscapeDataString(query.Artist),
            "&groupname=", Uri.EscapeDataString(query.Album),
            "&order_by=time&order_way=desc");

        using var browse = await GetJsonAsync(indexer.Id, settings, path, cancellationToken).ConfigureAwait(false);
        var releases = Parse(browse.RootElement, indexer, settings);

        // The file lists of the best-seeded torrents, so the matcher knows what is inside before a grab.
        var listed = new List<IndexerRelease>(releases.Count);
        var withLists = releases
            .OrderByDescending(release => release.Seeders ?? 0)
            .Take(FileListsPerSearch)
            .Select(release => release.ReleaseId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            if (!withLists.Contains(release.ReleaseId))
            {
                listed.Add(release);
                continue;
            }

            var torrentId = release.ReleaseId["gazelle-".Length..];
            using var details = await GetJsonAsync(indexer.Id, settings, "/ajax.php?action=torrent&id=" + torrentId, cancellationToken).ConfigureAwait(false);
            listed.Add(release with { FileList = FileList(details.RootElement) });
        }

        LogFound(_logger, listed.Count, indexer.Name);

        return listed;
    }

    /// <inheritdoc />
    public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(release);

        var settings = GazelleSettings.Read(NotificationSecrets.Read(indexer.Settings));
        var url = release.DownloadUrl ?? throw new IndexerException("The release has no download URL.");

        using var response = await SendAsync(indexer.Id, settings, new Uri(url), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"The tracker answered {(int)response.StatusCode} to the download.");
        }

        var content = await IndexerHttp.ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);

        // Gazelle reports a refused download (no token left, a bad id) as a 200 with a JSON failure.
        if (content.Length > 0 && content[0] == (byte)'{')
        {
            using var failure = JsonDocument.Parse(content);
            throw new IndexerException("The tracker said: " + (Text(failure.RootElement, "error") ?? "the download failed"));
        }

        return new IndexerDownload(content, null);
    }

    /// <summary>The connection test: <c>ajax.php?action=index</c> answers success.</summary>
    /// <param name="rowId">The row the request counts against (0 for a draft).</param>
    /// <param name="settings">The row's settings.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task IndexAsync(long rowId, GazelleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var document = await GetJsonAsync(rowId, settings, "/ajax.php?action=index", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One release per torrent of each group in a browse answer.</summary>
    /// <param name="root">The browse answer.</param>
    /// <param name="indexer">The Gazelle row.</param>
    /// <param name="settings">The row's settings.</param>
    public static IReadOnlyList<IndexerRelease> Parse(JsonElement root, Indexer indexer, GazelleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(settings);

        var releases = new List<IndexerRelease>();

        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return releases;
        }

        foreach (var group in results.EnumerateArray())
        {
            if (!group.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var artist = Text(group, "artist") ?? string.Empty;
            var album = Text(group, "groupName") ?? string.Empty;
            var year = Text(group, "groupYear");
            var groupId = Text(group, "groupId");

            foreach (var torrent in torrents.EnumerateArray())
            {
                if (Text(torrent, "torrentId") is not { Length: > 0 } torrentId)
                {
                    continue;
                }

                var freeleech = Flag(torrent, "isFreeleech") || Flag(torrent, "isFreeLeech") || Flag(torrent, "isNeutralLeech")
                    || Flag(torrent, "isFreeload") || Flag(torrent, "isPersonalFreeleech") || Flag(torrent, "isPersonalFreeLeech");
                var title = $"{artist} - {album} ({year}) [{Text(torrent, "format")} {Text(torrent, "encoding")}] [{Text(torrent, "media")}]";

                if (Flag(torrent, "hasCue"))
                {
                    title += " [Cue]";
                }

                var download = $"{settings.Url}/ajax.php?action=download&id={torrentId}";

                // Orpheus fails a download with usetoken=0, so the parameter is only ever sent as 1.
                if (settings.UseFreeleechTokens && !freeleech && Flag(torrent, "canUseToken"))
                {
                    download += "&usetoken=1";
                }

                var seeders = Integer(torrent, "seeders");
                var leechers = Integer(torrent, "leechers");

                releases.Add(new IndexerRelease(
                    WebUtility.HtmlDecode(title),
                    "gazelle-" + torrentId,
                    download,
                    null,
                    null,
                    LongInteger(torrent, "size"),
                    DateTimeOffset.TryParse(Text(torrent, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null,
                    seeders,
                    seeders is { } s && leechers is { } l ? s + l : null,
                    Integer(torrent, "snatches"),
                    [3000],
                    freeleech ? 0 : 1,
                    $"{settings.Url}/torrents.php?id={groupId}&torrentid={torrentId}",
                    DownloadProtocol.Torrent,
                    indexer.Id,
                    indexer.Name,
                    null));
            }
        }

        return releases;
    }

    /// <summary>
    /// A torrent's file list: <c>torrent.fileList</c> is <c>name{{{size}}}|||name{{{size}}}</c>, names
    /// HTML-encoded, under <c>torrent.filePath</c> when it has one.
    /// </summary>
    /// <param name="root">The <c>action=torrent</c> answer.</param>
    public static IReadOnlyList<ReleaseFile>? FileList(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("torrent", out var torrent)
            || Text(torrent, "fileList") is not { Length: > 0 } list)
        {
            return null;
        }

        var folder = WebUtility.HtmlDecode(Text(torrent, "filePath") ?? string.Empty).Trim('/');
        var files = new List<ReleaseFile>();

        foreach (var entry in list.Split("|||", StringSplitOptions.RemoveEmptyEntries))
        {
            var sizeStart = entry.LastIndexOf("{{{", StringComparison.Ordinal);
            var sizeEnd = entry.LastIndexOf("}}}", StringComparison.Ordinal);

            if (sizeStart <= 0 || sizeEnd <= sizeStart
                || !long.TryParse(entry.AsSpan(sizeStart + 3, sizeEnd - sizeStart - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            var name = WebUtility.HtmlDecode(entry[..sizeStart]);
            files.Add(new ReleaseFile(files.Count, folder.Length == 0 ? name : $"{folder}/{name}", size));
        }

        return files;
    }

    private async Task<JsonDocument> GetJsonAsync(long rowId, GazelleSettings settings, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(rowId, settings, new Uri(settings.Url + path), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new IndexerException($"The tracker answered {(int)response.StatusCode} without JSON.", exception);
        }

        if (!string.Equals(Text(document.RootElement, "status"), "success", StringComparison.OrdinalIgnoreCase))
        {
            var error = Text(document.RootElement, "error") ?? "the request failed";
            document.Dispose();
            throw new IndexerException("The tracker said: " + error);
        }

        return document;
    }

    /// <summary>Sends one request with the API key, after waiting for the row's rate limit.</summary>
    private async Task<HttpResponseMessage> SendAsync(long rowId, GazelleSettings settings, Uri url, CancellationToken cancellationToken)
    {
        _secrets.Register(settings.ApiKey);
        await WaitForTurnAsync(rowId, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", settings.ApiKey);

        try
        {
            return await _clients.CreateClient(IndexerHttp.ClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new IndexerException("The tracker could not be reached.", exception);
        }
    }

    /// <summary>
    /// At most <see cref="RequestsPerWindow"/> requests per row in any <see cref="Window"/>: a request
    /// that would be one too many waits until the oldest one leaves the window. Each row waits on its
    /// own turn, so one tracker's wait never holds up another's.
    /// </summary>
    private async Task WaitForTurnAsync(long rowId, CancellationToken cancellationToken)
    {
        RowLimit row;

        lock (_rowsGate)
        {
            if (!_rows.TryGetValue(rowId, out var found))
            {
                found = new RowLimit();
                _rows[rowId] = found;
            }

            row = found;
        }

        await row.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sent = row.Sent;

            while (true)
            {
                var now = _time.GetUtcNow();

                while (sent.Count > 0 && now - sent.Peek() >= Window)
                {
                    sent.Dequeue();
                }

                if (sent.Count < RequestsPerWindow)
                {
                    sent.Enqueue(now);
                    return;
                }

                await Task.Delay(sent.Peek() + Window - now, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            row.Turn.Release();
        }
    }

    /// <summary>One row's recent requests, and the turn its next request waits for.</summary>
    private sealed class RowLimit
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);

        public Queue<DateTimeOffset> Sent { get; } = new();
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Integer(JsonElement element, string name) =>
        Text(element, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? LongInteger(JsonElement element, string name) =>
        Text(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gazelle returned {Count} releases for {Indexer}")]
    private static partial void LogFound(ILogger logger, int count, string indexer);
}
