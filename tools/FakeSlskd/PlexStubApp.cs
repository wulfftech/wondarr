using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FakeSlskd;

/// <summary>
/// A Plex Media Server — and the one plex.tv call the sign-in makes — for the Phase 6 gate, on its own
/// loopback port inside the app container. It answers what Wondarr asks of Plex: the identity, the
/// music sections, partial scans and empty-trash (recorded, nothing else), a title search over the
/// files under a section's folder, and audio playlists kept in memory exactly as python-plexapi
/// describes them (create with a <c>server://…/library/metadata/{keys}</c> URI, append, remove by
/// playlist item id, move after another item). <c>GET /fake/plex</c> reports the state for the gate.
/// </summary>
public static class PlexStubApp
{
    /// <summary>The machine identifier the fake server reports.</summary>
    public const string MachineIdentifier = "fake-plex-machine";

    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".m4a", ".opus", ".ogg"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the stub. The caller starts it.</summary>
    /// <param name="options">Configuration: the port and the sections.</param>
    /// <param name="state">The fake's shared state (the request log).</param>
    public static WebApplication Build(FakeSlskdOptions options, FakeSlskdState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var builder = FakeSlskdHost.CreateBuilder($"http://127.0.0.1:{options.PlexPort}");
        var app = builder.Build();
        var plex = new FakePlexState(Sections(Environment.GetEnvironmentVariable("FAKE_PLEX_SECTIONS")));
        var self = $"http://127.0.0.1:{options.PlexPort.ToString(CultureInfo.InvariantCulture)}";

        app.Use(FakeSlskdHost.LogRequestsAsync);

        // plex.tv: the account's servers (the sign-in checks the token with this call).
        app.MapGet("/api/v2/resources", () => Results.Json(
            new[]
            {
                new
                {
                    name = "Fake Plex",
                    product = "Plex Media Server",
                    productVersion = "1.43.4.10903",
                    provides = "server",
                    clientIdentifier = MachineIdentifier,
                    owned = true,
                    accessToken = "fake-server-token",
                    connections = new[]
                    {
                        new { protocol = "http", address = "127.0.0.1", port = options.PlexPort, uri = self, local = true, relay = false },
                    },
                },
            }));

        app.MapGet("/identity", () => Container(new { machineIdentifier = MachineIdentifier, version = "1.43.4.10903", claimed = true }));

        app.MapGet("/library/sections", () => Container(new
        {
            size = plex.Sections.Count,
            Directory = plex.Sections.Select(section => new
            {
                key = section.Key,
                type = "artist",
                title = "Music " + section.Key,
                refreshing = false,
                Location = new[] { new { id = 1, path = section.Value } },
            }),
        }));

        app.MapGet("/library/sections/{key}/refresh", (string key, string? path) =>
        {
            plex.Refreshes.Enqueue(new Refresh(key, path ?? string.Empty));

            return Results.Ok();
        });

        app.MapPut("/library/sections/{key}/emptyTrash", () => Results.Ok());

        app.MapGet("/library/sections/{key}/all", (string key, string? title) =>
        {
            if (!plex.Sections.TryGetValue(key, out var root) || string.IsNullOrWhiteSpace(title) || !Directory.Exists(root))
            {
                return Container(new { size = 0, Metadata = Array.Empty<object>() });
            }

            var wanted = Fold(title);
            var tracks = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => !path.Contains("/.wondarr-", StringComparison.Ordinal))
                .Where(path => Fold(Path.GetFileNameWithoutExtension(path)).Contains(wanted, StringComparison.Ordinal))
                .Select(path => new
                {
                    ratingKey = plex.KeyOf(path),
                    type = "track",
                    title,
                    Media = new[] { new { Part = new[] { new { file = path } } } },
                })
                .ToList();

            return Container(new { size = tracks.Count, Metadata = tracks });
        });

        app.MapPost("/playlists", (HttpContext context) =>
        {
            var title = context.Request.Query["title"].ToString();
            var keys = KeysOf(context.Request.Query["uri"].ToString());

            if (string.IsNullOrWhiteSpace(title) || keys.Count == 0)
            {
                return Results.BadRequest();
            }

            var playlist = plex.Create(title);
            plex.Append(playlist, keys);

            return Container(new { size = 1, Metadata = new[] { new { ratingKey = playlist.Key, type = "playlist", title } } });
        });

        app.MapGet("/playlists/{key}/items", (string key) =>
            plex.Playlists.TryGetValue(key, out var playlist)
                ? Container(new
                {
                    size = playlist.Items.Count,
                    Metadata = playlist.Snapshot().Select(item => new { ratingKey = item.RatingKey, playlistItemID = long.Parse(item.ItemId, CultureInfo.InvariantCulture) }),
                })
                : Results.NotFound());

        app.MapPut("/playlists/{key}/items", (HttpContext context, string key) =>
        {
            if (!plex.Playlists.TryGetValue(key, out var playlist))
            {
                return Results.NotFound();
            }

            plex.Append(playlist, KeysOf(context.Request.Query["uri"].ToString()));

            return Container(new { size = playlist.Items.Count });
        });

        app.MapDelete("/playlists/{key}/items/{itemId}", (string key, string itemId) =>
        {
            if (!plex.Playlists.TryGetValue(key, out var playlist))
            {
                return Results.NotFound();
            }

            lock (playlist)
            {
                playlist.Items.RemoveAll(item => item.ItemId == itemId);
            }

            return Results.Ok();
        });

        app.MapPut("/playlists/{key}/items/{itemId}/move", (string key, string itemId, string? after) =>
        {
            if (!plex.Playlists.TryGetValue(key, out var playlist))
            {
                return Results.NotFound();
            }

            lock (playlist)
            {
                var item = playlist.Items.FirstOrDefault(candidate => candidate.ItemId == itemId);

                if (item is null)
                {
                    return Results.NotFound();
                }

                playlist.Items.Remove(item);
                var at = after is null ? 0 : playlist.Items.FindIndex(candidate => candidate.ItemId == after) + 1;
                playlist.Items.Insert(at, item);
            }

            return Results.Ok();
        });

        // For the gate: every playlist with its tracks' files in order, and the scans asked for.
        app.MapGet("/fake/plex", () => Results.Json(
            new
            {
                playlists = plex.Playlists.Values.OrderBy(playlist => playlist.Key, StringComparer.Ordinal).Select(playlist => new
                {
                    key = playlist.Key,
                    title = playlist.Title,
                    items = playlist.Snapshot().Select(item => new { ratingKey = item.RatingKey, file = plex.PathOf(item.RatingKey) }),
                }),
                refreshes = plex.Refreshes.ToArray(),
            },
            Json));

        return app;
    }

    private static IResult Container(object body) => Results.Json(new { MediaContainer = body }, Json);

    /// <summary>The rating keys at the end of a <c>server://{machine}/…/library/metadata/{keys}</c> URI.</summary>
    private static List<string> KeysOf(string uri)
    {
        var at = uri.LastIndexOf("/library/metadata/", StringComparison.Ordinal);

        return at < 0
            ? []
            : [.. uri[(at + "/library/metadata/".Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Letters and digits only, lower-case: a title and a file name named after it compare equal.</summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> Sections(string? configured)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in (configured ?? "1=/data/music;2=/data/music2").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);

            if (at > 0)
            {
                sections[pair[..at].Trim()] = pair[(at + 1)..].Trim();
            }
        }

        return sections;
    }

    private sealed record Refresh(string Section, string Path);

    private sealed record PlaylistItem(string RatingKey, string ItemId);

    private sealed class FakePlaylist(string key, string title)
    {
        public string Key { get; } = key;

        public string Title { get; } = title;

        public List<PlaylistItem> Items { get; } = [];

        public PlaylistItem[] Snapshot()
        {
            lock (this)
            {
                return [.. Items];
            }
        }
    }

    private sealed class FakePlexState(Dictionary<string, string> sections)
    {
        private readonly ConcurrentDictionary<string, string> _keyByPath = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _pathByKey = new(StringComparer.Ordinal);
        private int _nextKey = 1000;
        private int _nextItem = 5000;
        private int _nextPlaylist = 9000;

        public Dictionary<string, string> Sections { get; } = sections;

        public ConcurrentDictionary<string, FakePlaylist> Playlists { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<Refresh> Refreshes { get; } = new();

        public string KeyOf(string path) => _keyByPath.GetOrAdd(path, file =>
        {
            var key = Interlocked.Increment(ref _nextKey).ToString(CultureInfo.InvariantCulture);
            _pathByKey[key] = file;

            return key;
        });

        public string? PathOf(string key) => _pathByKey.TryGetValue(key, out var path) ? path : null;

        public FakePlaylist Create(string title)
        {
            var playlist = new FakePlaylist(Interlocked.Increment(ref _nextPlaylist).ToString(CultureInfo.InvariantCulture), title);
            Playlists[playlist.Key] = playlist;

            return playlist;
        }

        public void Append(FakePlaylist playlist, IEnumerable<string> keys)
        {
            lock (playlist)
            {
                foreach (var key in keys)
                {
                    playlist.Items.Add(new PlaylistItem(key, Interlocked.Increment(ref _nextItem).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }
    }
}
