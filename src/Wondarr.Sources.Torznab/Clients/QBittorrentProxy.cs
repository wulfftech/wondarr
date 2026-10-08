// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Download/Clients/QBittorrent/QBittorrentProxyV2.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Kept from the original: the login-on-403 flow with one retry, the SID cookie kept per client, the
// version-gated `stopped`/`paused` add parameter, the `Fails.` reading of the add response and the
// form shapes of info/files/add/delete/categories/createCategory. Dropped from the original: the
// v1 API, the API-key and basic-auth paths, seeding configuration, labels, queue priorities and
// Lidarr's own HTTP abstraction; added: filePrio, start/stop (and resume/pause before 2.11.0),
// stopCondition, the 2.8.15 version floor and Wondarr's typed errors.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Wondarr.Sources.Torznab.Clients;

/// <summary>One torrent as qBittorrent's <c>torrents/info</c> reports it.</summary>
/// <param name="Hash">The infohash, lower-case hex.</param>
/// <param name="Name">The torrent's name.</param>
/// <param name="State">The raw state name, 4.x and 5.x generations.</param>
/// <param name="Progress">How much is downloaded, 0 to 1.</param>
/// <param name="SavePath">The save path as qBittorrent sees it.</param>
/// <param name="ContentPath">The content path as qBittorrent sees it.</param>
/// <param name="HasMetadata">Whether the files are known; <see langword="null"/> when this qBittorrent does not report it.</param>
[method: JsonConstructor]
public sealed record QBittorrentTorrent(
    string Hash,
    string Name,
    string State,
    double Progress,
    string SavePath,
    string ContentPath,
    bool? HasMetadata);

/// <summary>One file as qBittorrent's <c>torrents/files</c> reports it.</summary>
/// <param name="Index">The file's index; what the priority call names it by.</param>
/// <param name="Name">The file's path inside the torrent.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Progress">How much of the file is downloaded, 0 to 1.</param>
/// <param name="Priority">The file's priority: 0 skipped, 1 wanted.</param>
[method: JsonConstructor]
public sealed record QBittorrentTorrentFile(int Index, string Name, long Size, double Progress, int Priority);

/// <summary>
/// One client row's state in the proxy: the <c>SID</c> cookie qBittorrent issued and the Web API
/// version read once for it. Keyed by row id and settings hash, so a settings change starts a fresh
/// session (DECISIONS build session 8 #9).
/// </summary>
public sealed class QBittorrentSession
{
    internal CookieContainer Cookies { get; } = new();

    internal Version? ApiVersion { get; set; }
}

/// <summary>
/// The qBittorrent Web API v2 HTTP layer: login on a 403 with one retry, the version read once per
/// client row, and the torrent calls <see cref="ITorrentClient"/> and the connection test need.
/// </summary>
public sealed partial class QBittorrentProxy
{
    /// <summary>The name the HTTP client is registered under.</summary>
    public const string HttpClientName = "wondarr-qbittorrent";

    /// <summary>The oldest Web API Wondarr drives: 2.8.15 shipped with qBittorrent 4.5.</summary>
    public static readonly Version MinimumApiVersion = new(2, 8, 15);

    /// <summary>From this version (qBittorrent 5.0) the API speaks <c>stopped</c>, <c>start</c> and <c>stop</c>.</summary>
    public static readonly Version StopStartVersion = new(2, 11, 0);

    private const string VersionPath = "/api/v2/app/webapiVersion";
    private const string LoginPath = "/api/v2/auth/login";
    private const string AddPath = "/api/v2/torrents/add";
    private const string InfoPath = "/api/v2/torrents/info";
    private const string FilesPath = "/api/v2/torrents/files";
    private const string FilePrioPath = "/api/v2/torrents/filePrio";
    private const string DeletePath = "/api/v2/torrents/delete";
    private const string CategoriesPath = "/api/v2/torrents/categories";
    private const string CreateCategoryPath = "/api/v2/torrents/createCategory";
    private const string PreferencesPath = "/api/v2/app/preferences";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QBittorrentProxy> _logger;
    private readonly ConcurrentDictionary<string, QBittorrentSession> _sessions = [];

    /// <summary>Initialises a new instance of the <see cref="QBittorrentProxy"/> class.</summary>
    /// <param name="httpClientFactory">Creates the HTTP client the calls go through.</param>
    /// <param name="logger">The log sink; never sees a password.</param>
    public QBittorrentProxy(IHttpClientFactory httpClientFactory, ILogger<QBittorrentProxy> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The session of one client row: its cookie jar and its cached Web API version. The same key
    /// returns the same session, so the login survives between calls; a settings change must be
    /// answered with a different key.
    /// </summary>
    /// <param name="key">The row id and the settings hash, as <see cref="SessionKey"/> builds it.</param>
    public QBittorrentSession Session(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _sessions.GetOrAdd(key, _ => new QBittorrentSession());
    }

    /// <summary>Builds the session key of a client row: its id and the hash of its settings JSON.</summary>
    /// <param name="rowId">The row's id, or 0 for a draft being tested.</param>
    /// <param name="settingsJson">The row's settings, exactly as stored.</param>
    public static string SessionKey(long rowId, string settingsJson)
    {
        ArgumentNullException.ThrowIfNull(settingsJson);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson));

        return string.Concat(rowId.ToString(CultureInfo.InvariantCulture), ":", Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Reads the Web API version once per session and caches it.</summary>
    public async Task<Version> GetApiVersionAsync(QBittorrentSession session, QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.ApiVersion is { } cached)
        {
            return cached;
        }

        using var response = await SendAsync(session, settings, () => Get(settings, VersionPath), cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DownloadClientException("The qBittorrent Web API is not at this address; check the host, the port and the URL base.");
        }

        var body = await ReadSuccessAsync(response, "the version request", cancellationToken).ConfigureAwait(false);

        if (!Version.TryParse(body.Trim(), out var version))
        {
            throw new DownloadClientException($"qBittorrent answered '{body.Trim()}' where its Web API version was expected.");
        }

        session.ApiVersion = version;

        return version;
    }

    /// <summary>Reads one torrent; an empty list means the client does not have it.</summary>
    public async Task<IReadOnlyList<QBittorrentTorrent>> GetTorrentsAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () => Get(settings, InfoPath + "?hashes=" + Uri.EscapeDataString(infoHash)),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TorrentNotFoundException();
        }

        var body = await ReadSuccessAsync(response, "the torrent list", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<List<QBittorrentTorrent>>(body, Json) ?? [];
    }

    /// <summary>Lists one torrent's files; empty while a magnet's metadata is still fetching.</summary>
    public async Task<IReadOnlyList<QBittorrentTorrentFile>> GetTorrentFilesAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () => Get(settings, FilesPath + "?hash=" + Uri.EscapeDataString(infoHash)),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TorrentNotFoundException();
        }

        var body = await ReadSuccessAsync(response, "the file list", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<List<QBittorrentTorrentFile>>(body, Json) ?? [];
    }

    /// <summary>
    /// Adds a torrent. A <c>.torrent</c> is added stopped (DECISIONS build session 8 #9) so nothing
    /// downloads before the file selection; a magnet is added running with
    /// <c>stopCondition=MetadataReceived</c> so it stops the moment the files are known. A
    /// <c>Fails.</c> answer counts as added when the torrent is in the client (a duplicate).
    /// </summary>
    public async Task AddTorrentAsync(QBittorrentSession session, QBittorrentSettings settings, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var version = await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () =>
            {
                // A fresh content per attempt: the request owns and disposes it.
                var content = new MultipartFormDataContent();

                if (request.TorrentFile is { Length: > 0 } torrentFile)
                {
                    var file = new ByteArrayContent(torrentFile);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                    content.Add(file, "torrents", request.InfoHash + ".torrent");

                    // Nothing downloads until Wondarr has chosen the files.
                    content.Add(new StringContent("true"), version >= StopStartVersion ? "stopped" : "paused");
                }
                else
                {
                    content.Add(new StringContent(request.MagnetUrl ?? string.Empty), "urls");

                    // Runs until the metadata arrives, then stops for the file selection.
                    content.Add(new StringContent("MetadataReceived"), "stopCondition");
                }

                if (settings.Category.Length > 0)
                {
                    content.Add(new StringContent(settings.Category), "category");
                }

                return Post(settings, AddPath, content);
            },
            cancellationToken).ConfigureAwait(false);

        var body = await ReadSuccessAsync(response, "the add", cancellationToken).ConfigureAwait(false);

        if (body.Contains("Fails.", StringComparison.Ordinal))
        {
            // A duplicate or an invalid torrent: one the client already has counts as added. A
            // missing torrent answers the follow-up with a 404, which is the same refusal.
            IReadOnlyList<QBittorrentTorrent> torrents;

            try
            {
                torrents = await GetTorrentsAsync(session, settings, request.InfoHash, cancellationToken).ConfigureAwait(false);
            }
            catch (TorrentNotFoundException)
            {
                torrents = [];
            }

            if (torrents.Count == 0)
            {
                throw new DownloadClientException("qBittorrent refused to add the torrent.");
            }
        }
    }

    /// <summary>Gives files a priority: 0 skips them, 1 downloads them.</summary>
    public async Task SetFilePriorityAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, IReadOnlyCollection<int> fileIndexes, int priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIndexes);

        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () => PostForm(
                settings,
                FilePrioPath,
                new Dictionary<string, string>
                {
                    ["hash"] = infoHash,
                    ["id"] = string.Join("|", fileIndexes),
                    ["priority"] = priority.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TorrentNotFoundException();
        }

        await ReadSuccessAsync(response, "the file priority change", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a torrent: <c>start</c> from 2.11.0, <c>resume</c> before.</summary>
    public async Task StartTorrentAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, CancellationToken cancellationToken)
    {
        var version = await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);
        await PostHashesAsync(session, settings, version >= StopStartVersion ? "/api/v2/torrents/start" : "/api/v2/torrents/resume", infoHash, "the start", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops a torrent: <c>stop</c> from 2.11.0, <c>pause</c> before.</summary>
    public async Task StopTorrentAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, CancellationToken cancellationToken)
    {
        var version = await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);
        await PostHashesAsync(session, settings, version >= StopStartVersion ? "/api/v2/torrents/stop" : "/api/v2/torrents/pause", infoHash, "the stop", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a torrent; only <see cref="ITorrentClient.RemoveAsync"/> ever asks for the files with it.</summary>
    public async Task RemoveTorrentAsync(QBittorrentSession session, QBittorrentSettings settings, string infoHash, bool deleteFiles, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () => PostForm(
                settings,
                DeletePath,
                new Dictionary<string, string>
                {
                    ["hashes"] = infoHash,
                    ["deleteFiles"] = deleteFiles ? "true" : "false",
                }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TorrentNotFoundException();
        }

        await ReadSuccessAsync(response, "the remove", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the categories, as a name → save path map; a save path may be empty.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetCategoriesAsync(QBittorrentSession session, QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(session, settings, () => Get(settings, CategoriesPath), cancellationToken).ConfigureAwait(false);

        var body = await ReadSuccessAsync(response, "the category list", cancellationToken).ConfigureAwait(false);

        var categories = new Dictionary<string, string>();

        using var document = JsonDocument.Parse(body);

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var savePath = entry.Value.TryGetProperty("save_path", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            categories[entry.Name] = savePath;
        }

        return categories;
    }

    /// <summary>Creates a category; qBittorrent keeps it from then on.</summary>
    public async Task CreateCategoryAsync(QBittorrentSession session, QBittorrentSettings settings, string name, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            session,
            settings,
            () => PostForm(settings, CreateCategoryPath, new Dictionary<string, string> { ["category"] = name }),
            cancellationToken).ConfigureAwait(false);

        await ReadSuccessAsync(response, "the category creation", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads qBittorrent's own save path from its preferences.</summary>
    public async Task<string> GetSavePathAsync(QBittorrentSession session, QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        await RequireApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(session, settings, () => Get(settings, PreferencesPath), cancellationToken).ConfigureAwait(false);

        var body = await ReadSuccessAsync(response, "the preferences", cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("save_path", out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>Reads the version once and refuses anything older than qBittorrent 4.5.</summary>
    private async Task<Version> RequireApiVersionAsync(QBittorrentSession session, QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        var version = await GetApiVersionAsync(session, settings, cancellationToken).ConfigureAwait(false);

        if (version < MinimumApiVersion)
        {
            throw new DownloadClientException($"qBittorrent 4.5 or newer is needed; the Web API reports version {version}.");
        }

        return version;
    }

    /// <summary>Sends the request, and on a 403 logs in once and sends it again.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        QBittorrentSession session,
        QBittorrentSettings settings,
        Func<HttpRequestMessage> create,
        CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(session, settings, create(), cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();

        await LoginAsync(session, settings, cancellationToken).ConfigureAwait(false);

        return await SendOnceAsync(session, settings, create(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one request with the session's cookie and the base URL as its referer.</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(QBittorrentSession session, QBittorrentSettings settings, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AddHeaders(session, settings, request);

        try
        {
            return await _httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            request.Dispose();
            throw new DownloadClientException("Failed to connect to qBittorrent, check your settings.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            request.Dispose();
            throw new DownloadClientException("qBittorrent did not answer in time.", exception);
        }
    }

    /// <summary>Logs in and keeps the SID cookie in the session.</summary>
    private async Task LoginAsync(QBittorrentSession session, QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Username.Length == 0 && settings.Password.Length == 0)
        {
            throw new DownloadClientException("qBittorrent refused the request, and no username or password is configured to log in with.");
        }

        using var response = await SendOnceAsync(
            session,
            settings,
            PostForm(
                settings,
                LoginPath,
                new Dictionary<string, string>
                {
                    ["username"] = settings.Username,
                    ["password"] = settings.Password,
                }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DownloadClientException("qBittorrent refused the login and has banned this IP address; wait for the ban to lift or connect from another address.");
        }

        // qBittorrent 5.2 (Web API 2.15) answers a wrong password with 401 rather than 200 "Fails.".
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            LogLoginRefused(_logger, settings.BaseUrl);

            throw new DownloadClientException("Wrong qBittorrent username or password.");
        }

        var body = (await ReadSuccessAsync(response, "the login", cancellationToken).ConfigureAwait(false)).Trim();

        if (body == "Fails.")
        {
            LogLoginRefused(_logger, settings.BaseUrl);

            throw new DownloadClientException("Wrong qBittorrent username or password.");
        }

        // "Ok." before 5.2; an empty 204 from 5.2 on. Either way the SID cookie below decides.
        if (body.Length > 0 && body != "Ok.")
        {
            LogLoginRefused(_logger, settings.BaseUrl);

            throw new DownloadClientException("qBittorrent did not accept the login.");
        }

        var baseUri = new Uri(settings.BaseUrl);

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                try
                {
                    session.Cookies.SetCookies(baseUri, cookie);
                }
                catch (CookieException)
                {
                    // A cookie qBittorrent's own header confused us with; the SID check below decides.
                }
            }
        }

        if (session.Cookies.GetCookies(baseUri).Count == 0)
        {
            throw new DownloadClientException("qBittorrent accepted the login but sent no session cookie.");
        }

        LogLoggedIn(_logger, settings.BaseUrl);
    }

    /// <summary>Reads a successful body, turning the failure statuses into typed errors.</summary>
    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DownloadClientException("Failed to authenticate with qBittorrent; check the username and password.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadClientException($"qBittorrent answered HTTP {(int)response.StatusCode} to {what}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts a one-hash form call and turns a 404 into a missing torrent.</summary>
    private async Task PostHashesAsync(QBittorrentSession session, QBittorrentSettings settings, string path, string infoHash, string what, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            session,
            settings,
            () => PostForm(settings, path, new Dictionary<string, string> { ["hashes"] = infoHash }),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new TorrentNotFoundException();
        }

        await ReadSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
    }

    private static void AddHeaders(QBittorrentSession session, QBittorrentSettings settings, HttpRequestMessage request)
    {
        var baseUri = new Uri(settings.BaseUrl);

        // qBittorrent's CSRF check wants the Web UI's own address as the referer.
        request.Headers.Referrer = baseUri;

        var cookies = session.Cookies.GetCookies(baseUri);

        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value)));
        }
    }

    private static HttpRequestMessage Get(QBittorrentSettings settings, string path) =>
        new(HttpMethod.Get, settings.BaseUrl + path);

    private static HttpRequestMessage Post(QBittorrentSettings settings, string path, HttpContent content) =>
        new(HttpMethod.Post, settings.BaseUrl + path) { Content = content };

    private static HttpRequestMessage PostForm(QBittorrentSettings settings, string path, IReadOnlyDictionary<string, string> fields) =>
        Post(settings, path, new FormUrlEncodedContent(fields));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Logged in to qBittorrent at {BaseUrl}")]
    private static partial void LogLoggedIn(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "qBittorrent refused the login at {BaseUrl}")]
    private static partial void LogLoginRefused(ILogger logger, string baseUrl);
}
