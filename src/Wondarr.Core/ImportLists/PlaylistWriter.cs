using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;

namespace Wondarr.Core.ImportLists;

/// <summary>Writes a list's playlists: the Plex playlist and the <c>.m3u8</c>, as the list asks.</summary>
public interface IPlaylistWriter
{
    /// <summary>
    /// Brings the list's playlists in line with its items: the songs still in the source that have a
    /// file, in the source's order. A failure is reported in the returned line, never thrown — a
    /// playlist that could not be written must not fail the sync that added the songs.
    /// </summary>
    /// <param name="listId">The list.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The one-line result, or <see langword="null"/> when the list writes no playlist.</returns>
    Task<string?> WriteAsync(long listId, CancellationToken cancellationToken);
}

/// <summary>
/// The playlist output of an import list (DECISIONS build session 7 #10). The Plex playlist is made
/// once and then reconciled in place — missing tracks added, extra ones removed, the rest moved into
/// the list's order — so its id, its poster and what the user did with it in Plex survive a sync.
/// Tracks are named by rating key, found per file in the song's own library section and cached on the
/// list item for as long as the file stays where it was. The <c>.m3u8</c> goes to
/// <c>&lt;library root&gt;/Playlists/</c> with paths relative to it.
/// </summary>
public sealed partial class PlaylistWriter : IPlaylistWriter
{
    /// <summary>The folder under a library root the <c>.m3u8</c> files are written to.</summary>
    public const string PlaylistsFolder = "Playlists";

    /// <summary>How many rating keys one create or add call names (the URI is a query string).</summary>
    private const int KeysPerCall = 100;

    private readonly WondarrDbContext _database;
    private readonly IPlexConnectionService _connection;
    private readonly IPlexServerClient _plex;
    private readonly ILogger<PlaylistWriter> _logger;

    /// <summary>Initialises a new instance of the <see cref="PlaylistWriter"/> class.</summary>
    /// <param name="database">The Wondarr database.</param>
    /// <param name="connection">The Plex connection: the server, the token and the machine id.</param>
    /// <param name="plex">The Plex server client.</param>
    /// <param name="logger">The logger.</param>
    public PlaylistWriter(
        WondarrDbContext database,
        IPlexConnectionService connection,
        IPlexServerClient plex,
        ILogger<PlaylistWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plex);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _connection = connection;
        _plex = plex;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> WriteAsync(long listId, CancellationToken cancellationToken)
    {
        var list = await _database.ImportLists
            .FirstOrDefaultAsync(candidate => candidate.Id == listId, cancellationToken)
            .ConfigureAwait(false);

        if (list is null || (!list.PlexPlaylist && !list.M3uExport))
        {
            return null;
        }

        // The items still in the source, in its order, with the song each became and its file.
        var items = await _database.ImportListItems
            .Where(item => item.ImportListId == listId && item.RemovedAt == null && item.SongId != null)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songIds = items.Select(item => item.SongId!.Value).Distinct().ToList();
        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.File)
            .Where(song => songIds.Contains(song.Id))
            .ToDictionaryAsync(song => song.Id, cancellationToken)
            .ConfigureAwait(false);

        var tracks = new List<(ImportListItem Item, Song Song, SongFile File)>();
        var seen = new HashSet<long>();

        foreach (var item in items)
        {
            if (songs.TryGetValue(item.SongId!.Value, out var song) && song.File is { } file && seen.Add(song.Id))
            {
                tracks.Add((item, song, file));
            }
        }

        var parts = new List<string>(2);

        if (list.M3uExport)
        {
            parts.Add(await WriteM3uAsync(list, tracks, cancellationToken).ConfigureAwait(false));
        }

        if (list.PlexPlaylist)
        {
            parts.Add(await WritePlexAsync(list, tracks, cancellationToken).ConfigureAwait(false));
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return string.Join("; ", parts);
    }

    /// <summary>A file name safe on every file system Wondarr runs on.</summary>
    internal static string SafeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name.Trim())
        {
            builder.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        }

        var safe = builder.ToString().Trim().TrimEnd('.');

        return safe.Length == 0 ? "Playlist" : safe;
    }

    /// <summary>
    /// The steps that turn a playlist's current items into the wanted order: the items to remove, the
    /// keys to append, then — over the result — the moves (item, after which item; null = to the top).
    /// </summary>
    internal static (IReadOnlyList<string> Remove, IReadOnlyList<string> Add) Diff(
        IReadOnlyList<PlexPlaylistItem> current,
        IReadOnlyList<string> wanted)
    {
        var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var remove = new List<string>();

        foreach (var item in current)
        {
            // Not wanted, or a second copy of a wanted track: out.
            if (!wantedSet.Contains(item.RatingKey) || !kept.Add(item.RatingKey))
            {
                remove.Add(item.PlaylistItemId);
            }
        }

        var add = wanted.Where(key => !kept.Contains(key)).Distinct(StringComparer.Ordinal).ToList();

        return (remove, add);
    }

    /// <summary>The moves that put a playlist's items into the wanted order (each item after the previous one).</summary>
    internal static IReadOnlyList<(string ItemId, string? After)> Moves(
        IReadOnlyList<PlexPlaylistItem> current,
        IReadOnlyList<string> wanted)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in current)
        {
            byKey.TryAdd(item.RatingKey, item.PlaylistItemId);
        }

        var order = current.Select(item => item.PlaylistItemId).ToList();
        var moves = new List<(string ItemId, string? After)>();
        string? previous = null;
        var index = 0;

        foreach (var key in wanted)
        {
            if (!byKey.TryGetValue(key, out var itemId))
            {
                continue;
            }

            if (index >= order.Count || order[index] != itemId)
            {
                moves.Add((itemId, previous));
                order.Remove(itemId);
                order.Insert(index, itemId);
            }

            previous = itemId;
            index++;
        }

        return moves;
    }

    private async Task<string> WriteM3uAsync(
        ImportList list,
        List<(ImportListItem Item, Song Song, SongFile File)> tracks,
        CancellationToken cancellationToken)
    {
        var library = await _database.Libraries
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == list.LibraryId, cancellationToken)
            .ConfigureAwait(false);

        var folder = Path.Combine(library.RootPath, PlaylistsFolder);
        var path = Path.Combine(folder, SafeFileName(list.Name) + ".m3u8");
        var builder = new StringBuilder("#EXTM3U\n");

        foreach (var (_, song, file) in tracks)
        {
            var seconds = (file.DurationMs ?? song.DurationMs ?? 0) / 1000;

            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds},{song.ArtistCredit} - {song.Title}\n");

            // Relative to the playlist's own folder, with forward slashes, which every player reads;
            // a file on another drive (another library) keeps its full path.
            builder.Append(PlaylistPath(folder, file.Path)).Append('\n');
        }

        try
        {
            Directory.CreateDirectory(folder);

            // Written beside, then moved over: a player never reads half a playlist.
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, builder.ToString(), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogM3uFailed(_logger, list.Id, exception);

            return $"the .m3u8 could not be written: {exception.Message}";
        }

        return string.Create(CultureInfo.InvariantCulture, $".m3u8 written with {tracks.Count} tracks");
    }

    private async Task<string> WritePlexAsync(
        ImportList list,
        List<(ImportListItem Item, Song Song, SongFile File)> tracks,
        CancellationToken cancellationToken)
    {
        var state = await _connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var context = await _connection.GetServerContextAsync(cancellationToken).ConfigureAwait(false);

        if (context is not { } server || string.IsNullOrEmpty(state.MachineIdentifier))
        {
            return "no Plex server is connected";
        }

        var (uri, token) = server;
        var libraries = await _database.Libraries
            .AsNoTracking()
            .ToDictionaryAsync(library => library.Id, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var wanted = new List<string>(tracks.Count);
            var missing = 0;

            foreach (var (item, song, file) in tracks)
            {
                var key = await RatingKeyAsync(uri, token, libraries.GetValueOrDefault(song.LibraryId), item, song, file, cancellationToken)
                    .ConfigureAwait(false);

                if (key is null)
                {
                    missing++;
                }
                else if (!wanted.Contains(key, StringComparer.Ordinal))
                {
                    wanted.Add(key);
                }
            }

            var current = list.PlexPlaylistKey is { } existing
                ? await _plex.GetPlaylistItemsAsync(uri, token, existing, cancellationToken).ConfigureAwait(false)
                : null;

            if (current is null)
            {
                if (wanted.Count == 0)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"Plex playlist not made yet: no track is in Plex ({missing} waiting for a scan)");
                }

                // Plex makes a playlist only with at least one item: the first call names a batch,
                // the rest are appended in order.
                list.PlexPlaylistKey = await _plex
                    .CreatePlaylistAsync(uri, token, state.MachineIdentifier!, list.Name, [.. wanted.Take(KeysPerCall)], cancellationToken)
                    .ConfigureAwait(false);

                foreach (var chunk in wanted.Skip(KeysPerCall).Chunk(KeysPerCall))
                {
                    await _plex.AddPlaylistItemsAsync(uri, token, state.MachineIdentifier!, list.PlexPlaylistKey, chunk, cancellationToken)
                        .ConfigureAwait(false);
                }

                LogCreated(_logger, list.Id, list.PlexPlaylistKey, wanted.Count);

                return Summary("created", wanted.Count, missing);
            }

            var (remove, add) = Diff(current, wanted);

            foreach (var itemId in remove)
            {
                await _plex.RemovePlaylistItemAsync(uri, token, list.PlexPlaylistKey!, itemId, cancellationToken).ConfigureAwait(false);
            }

            foreach (var chunk in add.Chunk(KeysPerCall))
            {
                await _plex.AddPlaylistItemsAsync(uri, token, state.MachineIdentifier!, list.PlexPlaylistKey!, chunk, cancellationToken)
                    .ConfigureAwait(false);
            }

            var moved = 0;

            if (remove.Count > 0 || add.Count > 0 || !current.Select(item => item.RatingKey).SequenceEqual(wanted, StringComparer.Ordinal))
            {
                var now = await _plex.GetPlaylistItemsAsync(uri, token, list.PlexPlaylistKey!, cancellationToken).ConfigureAwait(false) ?? [];

                foreach (var (itemId, after) in Moves(now, wanted))
                {
                    await _plex.MovePlaylistItemAsync(uri, token, list.PlexPlaylistKey!, itemId, after, cancellationToken).ConfigureAwait(false);
                    moved++;
                }
            }

            return Summary(
                string.Create(CultureInfo.InvariantCulture, $"updated (+{add.Count} -{remove.Count}, {moved} moved)"),
                wanted.Count,
                missing);
        }
        catch (PlexException exception)
        {
            LogPlexFailed(_logger, list.Id, exception);

            return "the Plex playlist could not be written: " + exception.Message;
        }
    }

    /// <summary>
    /// The rating key of the track a file is: the cached one while the file is where it was, else a
    /// title search in the song's own section, matched on the file's path as the server sees it.
    /// </summary>
    private async Task<string?> RatingKeyAsync(
        Uri server,
        string token,
        Library? library,
        ImportListItem item,
        Song song,
        SongFile file,
        CancellationToken cancellationToken)
    {
        if (item.PlexRatingKey is { } cached && string.Equals(item.PlexRatingKeyPath, file.Path, StringComparison.Ordinal))
        {
            return cached;
        }

        if (library?.PlexSectionId is not { Length: > 0 } section)
        {
            return null;
        }

        var serverPath = PlexPathMapper.ToServerPath(library, file.Path);
        var found = await _plex.FindTracksAsync(server, token, section, song.Title, cancellationToken).ConfigureAwait(false);
        var match = found.FirstOrDefault(track => track.Files.Any(path => SamePath(path, serverPath)));

        item.PlexRatingKey = match?.RatingKey;
        item.PlexRatingKeyPath = match is null ? null : file.Path;

        return match?.RatingKey;
    }

    private static string PlaylistPath(string folder, string file)
    {
        try
        {
            return Path.GetRelativePath(folder, file).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return file.Replace('\\', '/');
        }
    }

    private static bool SamePath(string plexPath, string serverPath) =>
        string.Equals(plexPath.Replace('\\', '/'), serverPath.Replace('\\', '/'), StringComparison.Ordinal);

    private static string Summary(string what, int count, int missing) =>
        missing == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Plex playlist {what} with {count} tracks")
            : string.Create(CultureInfo.InvariantCulture, $"Plex playlist {what} with {count} tracks ({missing} not in Plex yet)");

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Plex playlist {PlaylistKey} for import list {ImportListId} with {Count} tracks")]
    private static partial void LogCreated(ILogger logger, long importListId, string playlistKey, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the Plex playlist of import list {ImportListId} failed")]
    private static partial void LogPlexFailed(ILogger logger, long importListId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the .m3u8 of import list {ImportListId} failed")]
    private static partial void LogM3uFailed(ILogger logger, long importListId, Exception exception);
}
