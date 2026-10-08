using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>One request the fake qBittorrent saw, with its body already read and parsed.</summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    string Path,
    string Query,
    string ContentType,
    byte[] Body,
    string? Cookie,
    string? Referer)
{
    /// <summary>The body as text.</summary>
    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>The fields of an <c>application/x-www-form-urlencoded</c> body.</summary>
    public IReadOnlyDictionary<string, string> Form
    {
        get
        {
            var fields = new Dictionary<string, string>();

            if (!ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return fields;
            }

            foreach (var pair in BodyText.Split('&'))
            {
                var separator = pair.IndexOf('=');

                if (separator > 0)
                {
                    fields[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
                }
            }

            return fields;
        }
    }

    /// <summary>The parts of a <c>multipart/form-data</c> body: name, file name when it is a file, bytes.</summary>
    public IReadOnlyList<(string Name, string? FileName, byte[] Body)> Parts
    {
        get
        {
            var parts = new List<(string, string?, byte[])>();

            if (!ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return parts;
            }

            var marker = "boundary=";
            var boundaryIndex = ContentType.IndexOf(marker, StringComparison.Ordinal);

            if (boundaryIndex < 0)
            {
                return parts;
            }

            var boundary = ContentType[(boundaryIndex + marker.Length)..].Trim('"');

            foreach (var raw in BodyText.Split("--" + boundary, StringSplitOptions.RemoveEmptyEntries))
            {
                var section = raw.Trim('\r', '\n');

                if (section == "--")
                {
                    continue;
                }

                var headerEnd = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headerEnd < 0)
                {
                    continue;
                }

                var headers = section[..headerEnd];
                var content = section[(headerEnd + 4)..].TrimEnd('\r', '\n');
                var name = Token(headers, "name");

                if (name is null)
                {
                    continue;
                }

                parts.Add((name, Token(headers, "filename"), Encoding.UTF8.GetBytes(content)));
            }

            return parts;
        }
    }

    /// <summary>The fields of a multipart body that are not files.</summary>
    public IReadOnlyDictionary<string, string> MultipartFields
    {
        get
        {
            var fields = new Dictionary<string, string>();

            foreach (var (name, fileName, body) in Parts)
            {
                if (fileName is null)
                {
                    fields[name] = Encoding.UTF8.GetString(body);
                }
            }

            return fields;
        }
    }

    /// <summary>The file parts of a multipart body, by field name.</summary>
    public IReadOnlyDictionary<string, byte[]> MultipartFiles
    {
        get
        {
            var files = new Dictionary<string, byte[]>();

            foreach (var (name, fileName, body) in Parts)
            {
                if (fileName is not null)
                {
                    files[name] = body;
                }
            }

            return files;
        }
    }

    /// <summary>One query parameter's value, or <see langword="null"/>.</summary>
    public string? QueryValue(string name)
    {
        foreach (var pair in Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');

            if (separator > 0 && pair[..separator] == name)
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    /// <summary>
    /// One parameter of a part's <c>Content-Disposition</c> header, quoted or bare: .NET writes
    /// <c>name="a"</c> for some content types and <c>name=a</c> for others.
    /// </summary>
    private static string? Token(string headers, string name)
    {
        var index = headers.IndexOf(" " + name + "=", StringComparison.Ordinal);

        if (index < 0)
        {
            return null;
        }

        var start = index + name.Length + 2;

        if (start < headers.Length && headers[start] == '"')
        {
            var end = headers.IndexOf('"', start + 1);

            return end < 0 ? null : headers[(start + 1)..end];
        }

        var separator = headers.IndexOf(';', start);
        var lineEnd = headers.IndexOf("\r\n", start, StringComparison.Ordinal);
        var valueEnd = separator >= 0 ? separator : (lineEnd >= 0 ? lineEnd : headers.Length);

        return headers[start..valueEnd];
    }
}

/// <summary>One torrent the fake qBittorrent holds.</summary>
public sealed class FakeTorrent
{
    /// <summary>Gets or sets the infohash, lower-case hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets the torrent's name.</summary>
    public string Name { get; set; } = "Some Release";

    /// <summary>Gets or sets the raw state name.</summary>
    public string State { get; set; } = "downloading";

    /// <summary>Gets or sets the progress, 0 to 1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the save path as the client sees it.</summary>
    public string SavePath { get; set; } = "/downloads";

    /// <summary>Gets or sets the content path as the client sees it.</summary>
    public string ContentPath { get; set; } = "/downloads/Some Release";

    /// <summary>Gets or sets whether the files are known; null when this client does not report it.</summary>
    public bool? HasMetadata { get; set; }

    /// <summary>Gets the torrent's files.</summary>
    public List<FakeTorrentFile> Files { get; } = [];
}

/// <summary>One file of a fake torrent.</summary>
public sealed class FakeTorrentFile
{
    /// <summary>Initialises a new instance of the <see cref="FakeTorrentFile"/> class.</summary>
    /// <param name="index">The file's index.</param>
    /// <param name="name">The file's path inside the torrent.</param>
    /// <param name="size">The file's size in bytes.</param>
    public FakeTorrentFile(int index, string name, long size)
    {
        Index = index;
        Name = name;
        Size = size;
    }

    /// <summary>Gets the file's index.</summary>
    public int Index { get; }

    /// <summary>Gets the file's path inside the torrent.</summary>
    public string Name { get; }

    /// <summary>Gets the file's size in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets or sets the file's priority.</summary>
    public int Priority { get; set; } = 1;
}

/// <summary>
/// A fake qBittorrent Web API: answers per path, keeps torrents, categories and a save path, and
/// records every request. Authentication is the real flow: a 403 until the login with the right
/// credentials has issued the <c>SID</c> cookie.
/// </summary>
public sealed class QbittorrentFake : HttpMessageHandler
{
    /// <summary>The username the fake accepts.</summary>
    public const string Username = "admin";

    /// <summary>A fixture password, never a real credential.</summary>
    public const string Password = "fixture-password-1234";

    private const string Sid = "sid-0123456789abcdef";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string _version = "2.11.0";
    private bool _requireAuthentication = true;
    private bool _banned;
    private bool _duplicateAddsFail;
    private bool _loggedIn;

    /// <summary>Gets every request the fake saw, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Gets the torrents the fake holds, by infohash.</summary>
    public Dictionary<string, FakeTorrent> Torrents { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the categories the fake holds, by name; the value is the save path.</summary>
    public Dictionary<string, string> Categories { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the save path the preferences report.</summary>
    public string SavePath { get; set; } = "/downloads";

    /// <summary>Stops requiring a login, like qBittorrent's local-network bypass.</summary>
    public QbittorrentFake WithoutAuthentication()
    {
        _requireAuthentication = false;
        return this;
    }

    /// <summary>Sets the Web API version the fake reports.</summary>
    public QbittorrentFake WithVersion(string version)
    {
        _version = version;
        return this;
    }

    /// <summary>Makes the login answer 403, as qBittorrent does for a banned address.</summary>
    public QbittorrentFake WithBannedAddress()
    {
        _banned = true;
        return this;
    }

    /// <summary>Makes every add answer <c>Fails.</c>, so the caller must check the torrent list.</summary>
    public QbittorrentFake WhereDuplicateAddsFail()
    {
        _duplicateAddsFail = true;
        return this;
    }

    /// <summary>Adds a torrent the fake holds.</summary>
    public FakeTorrent AddTorrent(string hash, string state)
    {
        var torrent = new FakeTorrent { Hash = hash, State = state };
        Torrents[hash] = torrent;

        return torrent;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.Query,
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            body,
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
            request.Headers.Referrer?.ToString());
        Requests.Add(recorded);

        return Respond(recorded);
    }

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        if (_requireAuthentication && !_loggedIn && request.Path != "/api/v2/auth/login")
        {
            return Text(HttpStatusCode.Forbidden, "Forbidden");
        }

        if (request.Method != HttpMethod.Post && request.Method != HttpMethod.Get)
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        switch (request.Path)
        {
            case "/api/v2/app/webapiVersion":
                return request.Method == HttpMethod.Get
                    ? Text(HttpStatusCode.OK, _version)
                    : Text(HttpStatusCode.NotFound, string.Empty);

            case "/api/v2/auth/login":
                return Login(request);

            case "/api/v2/torrents/info":
                return TorrentInfo(request);

            case "/api/v2/torrents/files":
                return TorrentFiles(request);

            case "/api/v2/torrents/add":
                return Add(request);

            case "/api/v2/torrents/filePrio":
                return FilePriority(request);

            case "/api/v2/torrents/start":
            case "/api/v2/torrents/resume":
                return SetState(request, "downloading");

            case "/api/v2/torrents/stop":
            case "/api/v2/torrents/pause":
                return SetState(request, "stoppedDL");

            case "/api/v2/torrents/delete":
                return Delete(request);

            case "/api/v2/torrents/categories":
                return Json(HttpStatusCode.OK, Categories.ToDictionary(
                    pair => pair.Key,
                    pair => (object)new Dictionary<string, string> { ["name"] = pair.Key, ["save_path"] = pair.Value }));

            case "/api/v2/torrents/createCategory":
                Categories[request.Form["category"]] = string.Empty;
                return Text(HttpStatusCode.OK, string.Empty);

            case "/api/v2/app/preferences":
                return Json(HttpStatusCode.OK, new Dictionary<string, string> { ["save_path"] = SavePath });

            default:
                return Text(HttpStatusCode.NotFound, string.Empty);
        }
    }

    private HttpResponseMessage Login(RecordedRequest request)
    {
        if (_banned)
        {
            return Text(HttpStatusCode.Forbidden, "Forbidden");
        }

        var form = request.Form;

        if (form.GetValueOrDefault("username") != Username || form.GetValueOrDefault("password") != Password)
        {
            return Text(HttpStatusCode.OK, "Fails.");
        }

        _loggedIn = true;

        var response = Text(HttpStatusCode.OK, "Ok.");
        response.Headers.Add("Set-Cookie", $"SID={Sid}; path=/");

        return response;
    }

    private HttpResponseMessage TorrentInfo(RecordedRequest request)
    {
        var hash = request.QueryValue("hashes");

        if (hash is null || !Torrents.TryGetValue(hash, out var torrent))
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        var entry = new Dictionary<string, object?>
        {
            ["hash"] = torrent.Hash,
            ["name"] = torrent.Name,
            ["state"] = torrent.State,
            ["progress"] = torrent.Progress,
            ["save_path"] = torrent.SavePath,
            ["content_path"] = torrent.ContentPath,
        };

        if (torrent.HasMetadata is { } hasMetadata)
        {
            entry["has_metadata"] = hasMetadata;
        }

        return Json(HttpStatusCode.OK, new[] { entry });
    }

    private HttpResponseMessage TorrentFiles(RecordedRequest request)
    {
        var hash = request.QueryValue("hash");

        if (hash is null || !Torrents.TryGetValue(hash, out var torrent))
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        return Json(HttpStatusCode.OK, torrent.Files.Select(file => new Dictionary<string, object?>
        {
            ["index"] = file.Index,
            ["name"] = file.Name,
            ["size"] = file.Size,
            ["progress"] = 0.0,
            ["priority"] = file.Priority,
        }));
    }

    private HttpResponseMessage Add(RecordedRequest request)
    {
        if (_duplicateAddsFail)
        {
            return Text(HttpStatusCode.OK, "Fails.");
        }

        var fields = request.MultipartFields;

        if (fields.TryGetValue("urls", out var magnet))
        {
            if (Wondarr.Sources.Torznab.Clients.MagnetLink.TryGetInfoHash(magnet, out var hash))
            {
                Torrents[hash] = new FakeTorrent { Hash = hash, State = "metaDL", HasMetadata = false };
            }

            return Text(HttpStatusCode.OK, "Ok.");
        }

        return Text(HttpStatusCode.OK, "Ok.");
    }

    private HttpResponseMessage FilePriority(RecordedRequest request)
    {
        var form = request.Form;

        if (!Torrents.TryGetValue(form.GetValueOrDefault("hash", string.Empty), out var torrent))
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        var priority = int.Parse(form["priority"], CultureInfo.InvariantCulture);

        foreach (var index in form["id"].Split('|'))
        {
            var file = torrent.Files.FirstOrDefault(candidate => candidate.Index == int.Parse(index, CultureInfo.InvariantCulture));

            if (file is not null)
            {
                file.Priority = priority;
            }
        }

        return Text(HttpStatusCode.OK, string.Empty);
    }

    private HttpResponseMessage SetState(RecordedRequest request, string state)
    {
        var form = request.Form;

        if (!Torrents.TryGetValue(form.GetValueOrDefault("hashes", string.Empty), out var torrent))
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        torrent.State = state;

        return Text(HttpStatusCode.OK, string.Empty);
    }

    private HttpResponseMessage Delete(RecordedRequest request)
    {
        var form = request.Form;

        if (!Torrents.Remove(form.GetValueOrDefault("hashes", string.Empty)))
        {
            return Text(HttpStatusCode.NotFound, string.Empty);
        }

        return Text(HttpStatusCode.OK, string.Empty);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, JsonOptions)) };
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over one fixed handler.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>A logger that keeps every formatted message, so a test can read what was written.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>Gets the formatted messages, in order.</summary>
    public List<string> Messages { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));

        if (exception is not null)
        {
            Messages.Add(exception.Message);
        }
    }
}
