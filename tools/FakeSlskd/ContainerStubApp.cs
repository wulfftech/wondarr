using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FakeSlskd;

/// <summary>
/// The Phase 7 gate's torrents and usenet, on one loopback port inside the app container: a Torznab
/// and a Newznab indexer (<c>/torznab/api</c>, <c>/newznab/api</c>) offering the releases the gate
/// registers, a qBittorrent Web API (<c>/api/v2/…</c>) that "downloads" — renders real audio for —
/// only the files whose priority is above 0, and a SABnzbd API (<c>/sabnzbd/api</c>) that trims,
/// "unpacks" a post into its complete folder and keeps a history. The gate registers releases with
/// <c>POST /fake/containers</c> and reads what happened with <c>GET /fake/containers</c>.
/// </summary>
public static class ContainerStubApp
{
    /// <summary>Where the fake clients' folders live unless <c>FAKE_CONTAINERS_ROOT</c> says otherwise.</summary>
    public const string DefaultRoot = "/data/downloads";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions SnakeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Builds the stub. The caller starts it.</summary>
    /// <param name="options">Configuration: the port and the audio generator.</param>
    /// <param name="state">The fake's shared state, which the AcoustID stub answers from.</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var self = $"http://127.0.0.1:{options.ContainersPort.ToString(CultureInfo.InvariantCulture)}";
        var builder = FakeSlskdHost.CreateBuilder(self);
        var app = builder.Build();
        var containers = new ContainerWorld(options, state, self, options.ContainersRoot);

        app.Use(FakeSlskdHost.LogRequestsAsync);

        // ---- the gate ------------------------------------------------------------------------------
        app.MapPost("/fake/containers", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var release = await JsonSerializer.DeserializeAsync<ReleaseSpec>(context.Request.Body, Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("an empty release");

            return Results.Json(containers.Register(release), Json);
        });

        app.MapGet("/fake/containers", () => Results.Json(containers.Report(), Json));

        // ---- the indexers --------------------------------------------------------------------------
        app.MapGet("/torznab/api", (HttpContext context) => containers.Indexer(context, torrent: true));
        app.MapGet("/newznab/api", (HttpContext context) => containers.Indexer(context, torrent: false));
        app.MapGet("/torznab/download/{id:int}", (int id) => containers.Download(id, torrent: true));
        app.MapGet("/newznab/download/{id:int}", (int id) => containers.Download(id, torrent: false));

        // ---- qBittorrent ---------------------------------------------------------------------------
        app.MapGet("/api/v2/app/webapiVersion", () => Results.Text("2.11.4"));
        app.MapPost("/api/v2/auth/login", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "SID=fake-sid; path=/");
            return Results.Text("Ok.");
        });
        app.MapGet("/api/v2/app/preferences", () => Results.Json(new Dictionary<string, object> { ["save_path"] = containers.TorrentSavePath }));
        app.MapGet("/api/v2/torrents/categories", () => Results.Json(new Dictionary<string, object>
        {
            ["wondarr"] = new Dictionary<string, string> { ["name"] = "wondarr", ["savePath"] = string.Empty },
        }));
        app.MapPost("/api/v2/torrents/createCategory", () => Results.Text(string.Empty));
        app.MapPost("/api/v2/torrents/add", async (HttpContext context, CancellationToken cancellationToken) => await containers.AddTorrentAsync(context).ConfigureAwait(false));
        app.MapGet("/api/v2/torrents/info", (string? hashes) => containers.TorrentInfo(hashes));
        app.MapGet("/api/v2/torrents/files", (string? hash) => containers.TorrentFiles(hash));
        app.MapPost("/api/v2/torrents/filePrio", async (HttpContext context, CancellationToken cancellationToken) => await containers.FilePriorityAsync(context).ConfigureAwait(false));
        app.MapPost("/api/v2/torrents/start", async (HttpContext context, CancellationToken cancellationToken) => await containers.SetTorrentStateAsync(context, running: true).ConfigureAwait(false));
        app.MapPost("/api/v2/torrents/resume", async (HttpContext context, CancellationToken cancellationToken) => await containers.SetTorrentStateAsync(context, running: true).ConfigureAwait(false));
        app.MapPost("/api/v2/torrents/stop", async (HttpContext context, CancellationToken cancellationToken) => await containers.SetTorrentStateAsync(context, running: false).ConfigureAwait(false));
        app.MapPost("/api/v2/torrents/pause", async (HttpContext context, CancellationToken cancellationToken) => await containers.SetTorrentStateAsync(context, running: false).ConfigureAwait(false));
        app.MapPost("/api/v2/torrents/delete", async (HttpContext context, CancellationToken cancellationToken) => await containers.DeleteTorrentAsync(context).ConfigureAwait(false));

        // ---- SABnzbd -------------------------------------------------------------------------------
        app.MapMethods("/sabnzbd/api", ["GET", "POST"], async (HttpContext context, CancellationToken cancellationToken) => await containers.SabnzbdAsync(context).ConfigureAwait(false));

        return app;
    }

    /// <summary>One release the gate registers.</summary>
    public sealed record ReleaseSpec
    {
        /// <summary><c>torrent</c> or <c>usenet</c>.</summary>
        public string Protocol { get; init; } = "torrent";

        /// <summary>The release name the indexer lists.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>Usenet: the post is a RAR set with obfuscated names; its tracks appear only once unpacked.</summary>
        public bool Obfuscated { get; init; }

        /// <summary>The tracks, as scenario files: <c>path</c> is the file name, <c>audio</c> and <c>identity</c> what it is.</summary>
        public IReadOnlyList<ScenarioFile> Files { get; init; } = [];
    }

    /// <summary>Everything the stub holds, behind one lock.</summary>
    private sealed class ContainerWorld
    {
        private readonly Lock _gate = new();
        private readonly FakeSlskdOptions _options;
        private readonly FakeSlskdState _state;
        private readonly string _self;
        private readonly List<Release> _releases = [];
        private readonly Dictionary<string, Torrent> _torrents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);

        public ContainerWorld(FakeSlskdOptions options, FakeSlskdState state, string self, string root)
        {
            _options = options;
            _state = state;
            _self = self;
            TorrentSavePath = Path.Combine(root, "torrents");
            UsenetCompleteDir = Path.Combine(root, "usenet", "complete");

            Directory.CreateDirectory(TorrentSavePath);
            Directory.CreateDirectory(Path.Combine(UsenetCompleteDir, "music"));
        }

        /// <summary>Where the fake qBittorrent saves.</summary>
        public string TorrentSavePath { get; }

        /// <summary>Where the fake SABnzbd finishes jobs; the <c>wondarr</c> category's folder is <c>music</c> below it.</summary>
        public string UsenetCompleteDir { get; }

        // ---- the gate ------------------------------------------------------------------------------
        public object Register(ReleaseSpec spec)
        {
            lock (_gate)
            {
                var release = new Release(_releases.Count + 1, spec);
                _releases.Add(release);
                FakeSlskdLog.Info($"Container release {release.Id} registered: {spec.Protocol} '{spec.Title}' ({spec.Files.Count} files)");

                return new { id = release.Id, infoHash = release.InfoHash };
            }
        }

        public object Report()
        {
            lock (_gate)
            {
                return new
                {
                    torrents = _torrents.Values.Select(torrent => new
                    {
                        hash = torrent.Hash,
                        title = torrent.Release.Spec.Title,
                        state = torrent.State,
                        removed = torrent.Removed,
                        files = torrent.Files.Select(file => new { name = file.Name, priority = file.Priority, downloaded = file.Progress >= 1 }),
                    }),
                    jobs = _jobs.Values.Select(job => new
                    {
                        id = job.Id,
                        title = job.Release.Spec.Title,
                        status = job.Status,
                        removed = job.Removed,
                        removedWithFiles = job.RemovedWithFiles,
                        files = job.Files.Where(file => !file.Deleted).Select(file => file.Name),
                        deleted = job.Files.Where(file => file.Deleted).Select(file => file.Name),
                        unpacked = job.Unpacked,
                    }),
                };
            }
        }

        // ---- the indexers --------------------------------------------------------------------------
        public IResult Indexer(HttpContext context, bool torrent)
        {
            var query = context.Request.Query;
            var function = query["t"].ToString();

            if (function == "caps")
            {
                return Results.Content(
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <caps>
                      <server version="1.0" title="Fake indexer"/>
                      <limits max="100" default="100"/>
                      <searching>
                        <search available="yes" supportedParams="q"/>
                        <music-search available="yes" supportedParams="q,artist,album"/>
                      </searching>
                      <categories>
                        <category id="3000" name="Audio"><subcat id="3040" name="Audio/Lossless"/></category>
                      </categories>
                    </caps>
                    """,
                    "application/xml");
            }

            var text = string.Join(' ', new[] { query["q"].ToString(), query["artist"].ToString(), query["album"].ToString() });
            var wanted = SearchText.Tokenize(text);
            List<Release> found;

            lock (_gate)
            {
                found = [.. _releases.Where(release => release.IsTorrent == torrent && wanted.Length > 0 && wanted.All(SearchText.Tokenize(release.Spec.Title).Contains))];
            }

            XNamespace attr = torrent ? "http://torznab.com/schemas/2015/feed" : "http://www.newznab.com/DTD/2010/feeds/attributes/";
            var prefix = torrent ? "torznab" : "newznab";
            var channel = new XElement("channel", new XElement("title", "Fake indexer"));

            foreach (var release in found)
            {
                var link = $"{_self}/{prefix}/download/{release.Id.ToString(CultureInfo.InvariantCulture)}";
                var item = new XElement(
                    "item",
                    new XElement("title", release.Spec.Title),
                    new XElement("guid", new XAttribute("isPermaLink", "false"), $"fake-{prefix}-{release.Id.ToString(CultureInfo.InvariantCulture)}"),
                    new XElement("link", link),
                    new XElement("pubDate", DateTimeOffset.UtcNow.AddDays(-30).ToString("r", CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", link), new XAttribute("length", release.TotalSize), new XAttribute("type", torrent ? "application/x-bittorrent" : "application/x-nzb")),
                    new XElement(attr + "attr", new XAttribute("name", "size"), new XAttribute("value", release.TotalSize)),
                    new XElement(attr + "attr", new XAttribute("name", "category"), new XAttribute("value", "3040")));

                if (torrent)
                {
                    item.Add(
                        new XElement(attr + "attr", new XAttribute("name", "infohash"), new XAttribute("value", release.InfoHash)),
                        new XElement(attr + "attr", new XAttribute("name", "seeders"), new XAttribute("value", "25")),
                        new XElement(attr + "attr", new XAttribute("name", "peers"), new XAttribute("value", "30")));
                }
                else
                {
                    item.Add(new XElement(attr + "attr", new XAttribute("name", "grabs"), new XAttribute("value", "12")));
                }

                channel.Add(item);
            }

            var rss = new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + prefix, attr.NamespaceName), channel);

            return Results.Content(new XDocument(new XDeclaration("1.0", "utf-8", null), rss).ToString(), "application/rss+xml");
        }

        public IResult Download(int id, bool torrent)
        {
            lock (_gate)
            {
                var release = _releases.FirstOrDefault(candidate => candidate.Id == id && candidate.IsTorrent == torrent);

                return release is null
                    ? Results.NotFound()
                    : torrent
                        ? Results.Bytes(release.TorrentBytes, "application/x-bittorrent")
                        : Results.Bytes(release.NzbBytes, "application/x-nzb");
            }
        }

        // ---- qBittorrent ---------------------------------------------------------------------------
        public async Task<IResult> AddTorrentAsync(HttpContext context)
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            var file = form.Files["torrents"];

            if (file is null)
            {
                return Results.Text("Fails.");
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var stopped = form["stopped"] == "true" || form["paused"] == "true";

            lock (_gate)
            {
                var release = _releases.FirstOrDefault(candidate => candidate.IsTorrent && candidate.TorrentBytes.AsSpan().SequenceEqual(bytes));

                if (release is null)
                {
                    return Results.Text("Fails.");
                }

                if (!_torrents.ContainsKey(release.InfoHash))
                {
                    var torrent = new Torrent(release) { State = stopped ? "stoppedDL" : "downloading" };
                    _torrents[release.InfoHash] = torrent;
                }

                return Results.Text("Ok.");
            }
        }

        public IResult TorrentInfo(string? hashes)
        {
            lock (_gate)
            {
                var selected = _torrents.Values.Where(torrent => hashes is null || hashes.Split('|').Contains(torrent.Hash));

                return Results.Json(
                    selected.Select(torrent => new Dictionary<string, object>
                    {
                        ["hash"] = torrent.Hash,
                        ["name"] = torrent.Release.Spec.Title,
                        ["state"] = torrent.State,
                        ["progress"] = torrent.Progress,
                        ["save_path"] = TorrentSavePath,
                        ["content_path"] = Path.Combine(TorrentSavePath, torrent.Release.Spec.Title),
                        ["has_metadata"] = true,
                        ["category"] = "wondarr",
                    }),
                    SnakeJson);
            }
        }

        public IResult TorrentFiles(string? hash)
        {
            lock (_gate)
            {
                if (hash is null || !_torrents.TryGetValue(hash, out var torrent))
                {
                    return Results.NotFound();
                }

                return Results.Json(
                    torrent.Files.Select(file => new Dictionary<string, object>
                    {
                        ["index"] = file.Index,
                        ["name"] = file.Name,
                        ["size"] = file.Size,
                        ["progress"] = file.Progress,
                        ["priority"] = file.Priority,
                    }),
                    SnakeJson);
            }
        }

        public async Task<IResult> FilePriorityAsync(HttpContext context)
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);

            lock (_gate)
            {
                if (!_torrents.TryGetValue(form["hash"].ToString(), out var torrent))
                {
                    return Results.NotFound();
                }

                var priority = int.Parse(form["priority"].ToString(), CultureInfo.InvariantCulture);

                foreach (var index in form["id"].ToString().Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var file = torrent.Files.FirstOrDefault(candidate => candidate.Index == int.Parse(index, CultureInfo.InvariantCulture));

                    if (file is not null)
                    {
                        file.Priority = priority;
                    }
                }
            }

            Pump();
            return Results.Text(string.Empty);
        }

        public async Task<IResult> SetTorrentStateAsync(HttpContext context, bool running)
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);

            lock (_gate)
            {
                if (!_torrents.TryGetValue(form["hashes"].ToString(), out var torrent))
                {
                    return Results.NotFound();
                }

                torrent.State = running ? "downloading" : "stoppedDL";
            }

            Pump();
            return Results.Text(string.Empty);
        }

        public async Task<IResult> DeleteTorrentAsync(HttpContext context)
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);

            lock (_gate)
            {
                if (!_torrents.TryGetValue(form["hashes"].ToString(), out var torrent))
                {
                    return Results.NotFound();
                }

                torrent.Removed = true;
                _torrents.Remove(torrent.Hash);
                FakeSlskdLog.Info($"Torrent {torrent.Hash} removed (deleteFiles={form["deleteFiles"]})");
            }

            return Results.Text(string.Empty);
        }

        /// <summary>
        /// Starts rendering every wanted file of every running torrent that is not done yet, one task
        /// per file. A file is "downloaded" once its audio is on disk.
        /// </summary>
        private void Pump()
        {
            List<(Torrent Torrent, TorrentFile File)> work;

            lock (_gate)
            {
                work = [.. _torrents.Values
                    .Where(torrent => torrent.State is "downloading" or "uploading")
                    .SelectMany(torrent => torrent.Files
                        .Where(file => file.Priority > 0 && file.Progress < 1 && !file.Rendering)
                        .Select(file => (torrent, file)))];

                foreach (var (torrent, file) in work)
                {
                    file.Rendering = true;
                    torrent.State = "downloading";
                }
            }

            foreach (var (torrent, file) in work)
            {
                _ = Task.Run(async () =>
                {
                    var path = Path.Combine(TorrentSavePath, file.Name.Replace('/', Path.DirectorySeparatorChar));
                    await RenderAsync(file.Spec, path).ConfigureAwait(false);

                    lock (_gate)
                    {
                        file.Progress = 1;
                        file.Rendering = false;

                        if (torrent.Files.Where(other => other.Priority > 0).All(other => other.Progress >= 1))
                        {
                            torrent.State = "uploading";
                        }
                    }
                });
            }
        }

        // ---- SABnzbd -------------------------------------------------------------------------------
        public async Task<IResult> SabnzbdAsync(HttpContext context)
        {
            var query = context.Request.Query;
            var mode = query["mode"].ToString();

            switch (mode)
            {
                case "version":
                    return Results.Json(new { version = "4.5.3" });

                case "get_config":
                    return query["section"] == "categories"
                        ? Results.Json(new { config = new { categories = new object[] { new { name = "*", dir = string.Empty }, new { name = "wondarr", dir = "music" } } } })
                        : Results.Json(new { config = new { misc = new { complete_dir = UsenetCompleteDir } } });

                case "addfile":
                    return await AddJobAsync(context).ConfigureAwait(false);

                case "get_files":
                    lock (_gate)
                    {
                        return _jobs.TryGetValue(query["value"].ToString(), out var job)
                            ? Results.Json(new
                            {
                                files = job.Files.Where(file => !file.Deleted).Select(file => new
                                {
                                    filename = file.Name,
                                    nzf_id = file.Id,
                                    bytes = file.Size.ToString(CultureInfo.InvariantCulture),
                                }),
                            })
                            : Results.Json(new { files = Array.Empty<object>() });
                    }

                case "queue":
                    return Queue(query);

                case "history":
                    return History(query);

                default:
                    return Results.Json(new { status = false, error = $"unknown mode {mode}" });
            }
        }

        private async Task<IResult> AddJobAsync(HttpContext context)
        {
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            var file = form.Files["name"];

            if (file is null)
            {
                return Results.Json(new { status = false, error = "No NZB found in the upload" });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var query = context.Request.Query;

            lock (_gate)
            {
                var release = _releases.FirstOrDefault(candidate => !candidate.IsTorrent && candidate.NzbBytes.AsSpan().SequenceEqual(bytes));

                if (release is null)
                {
                    return Results.Json(new { status = false, error = "Unknown NZB" });
                }

                var job = new Job($"SABnzbd_nzo_{(_jobs.Count + 1).ToString(CultureInfo.InvariantCulture)}", release)
                {
                    Name = query["nzbname"].ToString() is { Length: > 0 } name ? name : release.Spec.Title,
                    Status = query["priority"] == "-2" ? "Paused" : "Queued",
                };
                _jobs[job.Id] = job;

                if (job.Status != "Paused")
                {
                    Process(job);
                }

                return Results.Json(new { status = true, nzo_ids = new[] { job.Id } });
            }
        }

        private IResult Queue(IQueryCollection query)
        {
            var name = query["name"].ToString();
            var value = query["value"].ToString();

            lock (_gate)
            {
                switch (name)
                {
                    case "delete_nzf":
                        if (_jobs.TryGetValue(value, out var trimmed))
                        {
                            foreach (var file in trimmed.Files.Where(file => file.Id == query["value2"].ToString()))
                            {
                                file.Deleted = true;
                            }
                        }

                        return Results.Json(new { status = true, nzf_ids = new[] { query["value2"].ToString() } });

                    case "resume":
                        if (_jobs.TryGetValue(value, out var resumed) && resumed.Status == "Paused")
                        {
                            resumed.Status = "Queued";
                            Process(resumed);
                        }

                        return Results.Json(new { status = true, nzo_ids = new[] { value } });

                    case "delete":
                        if (_jobs.TryGetValue(value, out var deleted) && !deleted.Finished)
                        {
                            Remove(deleted, query["del_files"] == "1");
                        }

                        return Results.Json(new { status = true, nzo_ids = new[] { value } });
                }

                var wanted = query["nzo_ids"].ToString();

                return Results.Json(new
                {
                    queue = new
                    {
                        slots = _jobs.Values
                            .Where(job => !job.Removed && !job.Finished && (wanted.Length == 0 || job.Id == wanted))
                            .Select(job => new
                            {
                                nzo_id = job.Id,
                                filename = job.Name,
                                status = job.Status,
                                mb = (job.Release.TotalSize / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture),
                                mbleft = (job.Status is "Paused" or "Queued" ? job.Release.TotalSize / 1048576.0 : 0).ToString("0.00", CultureInfo.InvariantCulture),
                                percentage = job.Status is "Paused" or "Queued" ? "0" : "50",
                            }),
                    },
                });
            }
        }

        private IResult History(IQueryCollection query)
        {
            var name = query["name"].ToString();
            var value = query["value"].ToString();

            lock (_gate)
            {
                if (name == "delete")
                {
                    if (_jobs.TryGetValue(value, out var deleted) && deleted.Finished)
                    {
                        Remove(deleted, query["del_files"] == "1");
                    }

                    return Results.Json(new { status = true });
                }

                var wanted = query["nzo_ids"].ToString();

                return Results.Json(new
                {
                    history = new
                    {
                        slots = _jobs.Values
                            .Where(job => !job.Removed && job.Finished && (wanted.Length == 0 || job.Id == wanted))
                            .Select(job => new
                            {
                                nzo_id = job.Id,
                                name = job.Name,
                                status = job.Status,
                                fail_message = string.Empty,
                                storage = job.Storage,
                                bytes = job.Release.TotalSize,
                            }),
                    },
                });
            }
        }

        /// <summary>
        /// Downloads and unpacks a job in the background: the audio files nobody deleted (a clean post)
        /// or every track of the release (an obfuscated RAR set, whose tracks only exist unpacked).
        /// </summary>
        private void Process(Job job)
        {
            job.Status = "Downloading";

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);

                List<ScenarioFile> tracks;
                string folder;

                lock (_gate)
                {
                    job.Status = "Extracting";
                    folder = Path.Combine(UsenetCompleteDir, "music", job.Name);
                    tracks = job.Release.Spec.Obfuscated
                        ? [.. job.Release.Spec.Files]
                        : [.. job.Release.Spec.Files.Where(track => job.Files.Any(file => file.Name == track.BareName && !file.Deleted))];
                }

                foreach (var track in tracks)
                {
                    await RenderAsync(track, Path.Combine(folder, track.BareName)).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    job.Unpacked = [.. tracks.Select(track => track.BareName)];
                    job.Storage = folder;
                    job.Status = "Completed";
                    job.Finished = true;
                }
            });
        }

        private static void Remove(Job job, bool deleteFiles)
        {
            job.Removed = true;
            job.RemovedWithFiles = deleteFiles;

            if (deleteFiles && job.Storage is { } folder && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            FakeSlskdLog.Info($"Usenet job {job.Id} removed (del_files={deleteFiles})");
        }

        /// <summary>Renders a track's audio and teaches the AcoustID stub its identity.</summary>
        private async Task RenderAsync(ScenarioFile track, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fingerprint = await _options.AudioGenerator.GenerateAsync(track, path, CancellationToken.None).ConfigureAwait(false);

            if (fingerprint.Length > 0 && track.Identity is { } identity)
            {
                _state.RememberIdentity(fingerprint, identity);
            }

            FakeSlskdLog.Info($"Rendered {path}");
        }
    }

    /// <summary>A registered release with its container bytes.</summary>
    private sealed class Release
    {
        public Release(int id, ReleaseSpec spec)
        {
            Id = id;
            Spec = spec;
            IsTorrent = string.Equals(spec.Protocol, "torrent", StringComparison.OrdinalIgnoreCase);
            Sizes = [.. spec.Files.Select(DeclaredSize)];
            TotalSize = Sizes.Sum();

            if (IsTorrent)
            {
                (TorrentBytes, InfoHash) = BuildTorrent(spec, Sizes);
                NzbBytes = [];
            }
            else
            {
                NzbBytes = BuildNzb(spec, Sizes);
                TorrentBytes = [];
                InfoHash = string.Empty;
            }
        }

        public int Id { get; }

        public ReleaseSpec Spec { get; }

        public bool IsTorrent { get; }

        public long[] Sizes { get; }

        public long TotalSize { get; }

        public byte[] TorrentBytes { get; }

        public string InfoHash { get; }

        public byte[] NzbBytes { get; }

        /// <summary>The size a file is listed with: what the indexer would say, from its length and codec.</summary>
        private static long DeclaredSize(ScenarioFile file)
        {
            var seconds = file.Audio?.DurationSeconds ?? file.Length;
            var kbps = file.Audio?.Codec == "mp3" ? file.Audio.BitrateKbps : 1000;

            return file.Size ?? (long)(seconds * kbps * 1000 / 8);
        }

        private static (byte[] Bytes, string InfoHash) BuildTorrent(ReleaseSpec spec, long[] sizes)
        {
            var info = new Bencode().Dict();
            info.Str("files").List();

            for (var index = 0; index < spec.Files.Count; index++)
            {
                info.Dict().Str("length").Int(sizes[index]).Str("path").List().Str(spec.Files[index].BareName).End().End();
            }

            info.End().Str("name").Str(spec.Title).Str("piece length").Int(262_144).Str("pieces").Bytes(new byte[20]).End();
            var infoBytes = info.ToArray();

            var torrent = new Bencode().Dict().Str("announce").Str("http://127.0.0.1/announce").Str("info").Raw(infoBytes).End().ToArray();

#pragma warning disable CA5350 // The BitTorrent v1 info-hash is SHA-1 by specification.
            var hash = Convert.ToHexStringLower(SHA1.HashData(infoBytes));
#pragma warning restore CA5350

            return (torrent, hash);
        }

        private static byte[] BuildNzb(ReleaseSpec spec, long[] sizes)
        {
            XNamespace nzb = "http://www.newzbin.com/DTD/2003/nzb";
            var root = new XElement(nzb + "nzb");
            var names = spec.Obfuscated
                ? Enumerable.Range(1, 3).Select(part => $"{ObfuscatedStem(spec.Title)}.part{part.ToString("00", CultureInfo.InvariantCulture)}.rar").ToList()
                : [.. spec.Files.Select(file => file.BareName)];
            var total = sizes.Sum();

            for (var index = 0; index < names.Count; index++)
            {
                var size = spec.Obfuscated ? total / names.Count : sizes[index];
                root.Add(new XElement(
                    nzb + "file",
                    new XAttribute("poster", "fake"),
                    new XAttribute("date", "0"),
                    new XAttribute("subject", $"[{(index + 1).ToString(CultureInfo.InvariantCulture)}/{names.Count.ToString(CultureInfo.InvariantCulture)}] - \"{names[index]}\" yEnc (1/1)"),
                    new XElement(nzb + "groups", new XElement(nzb + "group", "alt.binaries.sounds.flac")),
                    new XElement(nzb + "segments", new XElement(nzb + "segment", new XAttribute("bytes", size), new XAttribute("number", "1"), $"part{index.ToString(CultureInfo.InvariantCulture)}@fake"))));
            }

            return Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString());
        }

        private static string ObfuscatedStem(string title) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)))[..24];
    }

    private sealed class Torrent(Release release)
    {
        public Release Release { get; } = release;

        public string Hash { get; } = release.InfoHash;

        public string State { get; set; } = "stoppedDL";

        public bool Removed { get; set; }

        public List<TorrentFile> Files { get; } =
        [
            .. release.Spec.Files.Select((file, index) => new TorrentFile(index, $"{release.Spec.Title}/{file.BareName}", release.Sizes[index], file)),
        ];

        public double Progress
        {
            get
            {
                var total = Files.Sum(file => file.Size);
                return total == 0 ? 0 : Files.Sum(file => file.Size * file.Progress) / total;
            }
        }
    }

    private sealed class TorrentFile(int index, string name, long size, ScenarioFile spec)
    {
        public int Index { get; } = index;

        public string Name { get; } = name;

        public long Size { get; } = size;

        public ScenarioFile Spec { get; } = spec;

        public int Priority { get; set; } = 1;

        public double Progress { get; set; }

        public bool Rendering { get; set; }
    }

    private sealed class Job
    {
        public Job(string id, Release release)
        {
            Id = id;
            Release = release;
            Files = release.Spec.Obfuscated
                ? [.. Enumerable.Range(1, 3).Select(part => new JobFile($"nzf_{part.ToString(CultureInfo.InvariantCulture)}", $"part{part.ToString("00", CultureInfo.InvariantCulture)}.rar", release.TotalSize / 3))]
                : [.. release.Spec.Files.Select((file, index) => new JobFile($"nzf_{(index + 1).ToString(CultureInfo.InvariantCulture)}", file.BareName, release.Sizes[index]))];
        }

        public string Id { get; }

        public Release Release { get; }

        public string Name { get; set; } = string.Empty;

        public string Status { get; set; } = "Queued";

        public bool Finished { get; set; }

        public bool Removed { get; set; }

        public bool RemovedWithFiles { get; set; }

        public string? Storage { get; set; }

        public List<string> Unpacked { get; set; } = [];

        public List<JobFile> Files { get; }
    }

    private sealed class JobFile(string id, string name, long size)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public long Size { get; } = size;

        public bool Deleted { get; set; }
    }

    /// <summary>A tiny bencode writer for the fake's .torrent files.</summary>
    private sealed class Bencode
    {
        private readonly List<byte> _bytes = [];

        public byte[] ToArray() => [.. _bytes];

        public Bencode Raw(byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Bencode Dict() => Raw("d"u8.ToArray());

        public Bencode List() => Raw("l"u8.ToArray());

        public Bencode End() => Raw("e"u8.ToArray());

        public Bencode Int(long value) => Raw(Encoding.ASCII.GetBytes($"i{value.ToString(CultureInfo.InvariantCulture)}e"));

        public Bencode Str(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        public Bencode Bytes(byte[] value)
        {
            Raw(Encoding.ASCII.GetBytes($"{value.Length.ToString(CultureInfo.InvariantCulture)}:"));
            return Raw(value);
        }
    }
}
