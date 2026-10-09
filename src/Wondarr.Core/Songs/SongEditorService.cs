using System.Globalization;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Songs;

/// <summary>What the mass editor changes. A <see langword="null"/> field is left alone.</summary>
/// <param name="SongIds">The songs to change, 1 to 1,000, distinct.</param>
/// <param name="Monitored">The new monitored flag.</param>
/// <param name="QualityProfileId">The new quality profile.</param>
/// <param name="LibraryId">The library to move the songs to (queues <c>MoveSongs</c>).</param>
/// <param name="Tags">The tags to apply.</param>
/// <param name="ApplyTags">How <paramref name="Tags"/> apply: <c>add</c>, <c>remove</c> or <c>replace</c>.</param>
public sealed record SongEditorRequest(
    IReadOnlyList<long>? SongIds,
    bool? Monitored = null,
    long? QualityProfileId = null,
    long? LibraryId = null,
    IReadOnlyList<string>? Tags = null,
    string? ApplyTags = null);

/// <summary>The outcome of an edit.</summary>
/// <param name="Songs">The updated songs, in the request's order, loaded for a song resource.</param>
/// <param name="MoveCommandIds">The <c>MoveSongs</c> commands queued for the library change.</param>
public sealed record SongEditorResult(IReadOnlyList<Song> Songs, IReadOnlyList<long> MoveCommandIds);

/// <summary>A tag in use and how many songs carry it.</summary>
/// <param name="Label">The tag.</param>
/// <param name="SongCount">How many songs carry it.</param>
public sealed record SongTagCount(string Label, int SongCount);

/// <summary>Some of the named songs do not exist; nothing was changed.</summary>
public sealed class SongsNotFoundException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SongsNotFoundException"/> class.</summary>
    /// <param name="missingIds">The unknown ids.</param>
    public SongsNotFoundException(IReadOnlyList<long> missingIds)
        : base("Unknown song ids: " + string.Join(", ", missingIds.Take(10)) + (missingIds.Count > 10 ? ", ..." : string.Empty))
    {
        MissingIds = missingIds;
    }

    /// <summary>Gets every unknown id.</summary>
    public IReadOnlyList<long> MissingIds { get; }
}

/// <summary>Some of the songs a mass delete names are downloading or importing right now.</summary>
public sealed class SongsBusyException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="SongsBusyException"/> class.</summary>
    /// <param name="busyIds">The songs with a queue item still in flight.</param>
    public SongsBusyException(IReadOnlyList<long> busyIds)
        : base("These songs are downloading or importing; remove them from the queue first: "
            + string.Join(", ", busyIds.Take(10)) + (busyIds.Count > 10 ? ", ..." : string.Empty))
    {
        BusyIds = busyIds;
    }

    /// <summary>Gets every song with a queue item in flight.</summary>
    public IReadOnlyList<long> BusyIds { get; }
}

/// <summary>The rules of the Library page's mass editor and its tag list.</summary>
public interface ISongEditorService
{
    /// <summary>Applies the edit to every named song.</summary>
    /// <param name="request">The songs and the changes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="ArgumentException">The request is unusable (no ids, nothing to change, an unknown profile or library, a bad tag).</exception>
    /// <exception cref="SongsNotFoundException">A named song does not exist.</exception>
    Task<SongEditorResult> EditAsync(SongEditorRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes every named song (files stay on disk).</summary>
    /// <param name="songIds">The songs to delete, 1 to 1,000, distinct.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many songs were deleted.</returns>
    /// <exception cref="ArgumentException">The id list is unusable.</exception>
    /// <exception cref="SongsNotFoundException">A named song does not exist.</exception>
    Task<int> DeleteAsync(IReadOnlyList<long>? songIds, CancellationToken cancellationToken);

    /// <summary>Lists the tags in use with their song counts, ordered by label.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<SongTagCount>> GetTagsAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SongEditorService : ISongEditorService
{
    /// <summary>The most songs one edit names.</summary>
    public const int MaxSongIds = MoveSongsCommandHandler.MaxSongIds;

    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WondarrDbContext _database;
    private readonly ISongService _songs;
    private readonly ICommandQueue _commands;

    /// <summary>Initialises a new instance of the <see cref="SongEditorService"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="songs">The song service, which deletes.</param>
    /// <param name="commands">The command queue the library move runs on.</param>
    public SongEditorService(WondarrDbContext database, ISongService songs, ICommandQueue commands)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(commands);

        _database = database;
        _songs = songs;
        _commands = commands;
    }

    private enum TagMode
    {
        Add,
        Remove,
        Replace,
    }

    /// <inheritdoc />
    public async Task<SongEditorResult> EditAsync(SongEditorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ids = RequireIds(request.SongIds);
        var mode = ParseTagMode(request);

        if (request.Monitored is null && request.QualityProfileId is null && request.LibraryId is null && mode is null)
        {
            throw new ArgumentException("Nothing to change: give monitored, qualityProfileId, libraryId or tags.");
        }

        var tags = mode is null ? null : SongTags.Normalize(request.Tags!);

        var songs = await _database.Songs
            .Where(song => ids.Contains(song.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        RequireAll(ids, songs.Select(song => song.Id));

        if (request.QualityProfileId is { } profileId
            && !await _database.QualityProfiles.AnyAsync(profile => profile.Id == profileId, cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Quality profile {profileId.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        if (request.LibraryId is { } libraryId
            && !await _database.Libraries.AnyAsync(library => library.Id == libraryId, cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException($"Library {libraryId.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        // Work out every new tag list first, so a song over the limit changes nothing at all.
        var newTags = new Dictionary<long, List<string>>();
        if (mode is { } applyMode && tags is not null)
        {
            foreach (var song in songs)
            {
                newTags[song.Id] = applyMode switch
                {
                    TagMode.Add => SongTags.Normalize(song.Tags.Concat(tags)),
                    TagMode.Remove => [.. song.Tags.Where(existing => !tags.Contains(existing, StringComparer.OrdinalIgnoreCase))],
                    _ => [.. tags],
                };
            }
        }

        foreach (var song in songs)
        {
            if (request.Monitored is { } monitored)
            {
                song.Monitored = monitored;
            }

            if (request.QualityProfileId is { } profile)
            {
                song.QualityProfileId = profile;
            }

            if (newTags.TryGetValue(song.Id, out var list))
            {
                song.Tags = list;
            }
        }

        // One SaveChanges is one transaction: monitored, profile and tags land together or not at all.
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var commandIds = new List<long>();
        if (request.LibraryId is { } target)
        {
            var toMove = songs.Where(song => song.LibraryId != target).Select(song => song.Id).ToList();

            foreach (var chunk in toMove.Chunk(MoveSongsCommandHandler.MaxSongIds))
            {
                var command = await _commands
                    .EnqueueAsync(
                        MoveSongsCommandHandler.CommandName,
                        JsonSerializer.Serialize(
                            new MoveSongsCommandBody(MoveSongsCommandHandler.CommandName, chunk, target),
                            CommandJson),
                        CommandTrigger.Manual,
                        cancellationToken)
                    .ConfigureAwait(false);

                commandIds.Add(command.Id);
            }
        }

        _database.ChangeTracker.Clear();

        var loaded = await _database.Songs
            .AsNoTracking()
            .Include(song => song.PrimaryArtist)
            .Include(song => song.AlbumContext)
            .Include(song => song.File)
            .Where(song => ids.Contains(song.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = loaded.ToDictionary(song => song.Id);

        return new SongEditorResult([.. ids.Select(id => byId[id])], commandIds);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(IReadOnlyList<long>? songIds, CancellationToken cancellationToken)
    {
        var ids = RequireIds(songIds);

        var songs = await _database.Songs
            .Where(song => ids.Contains(song.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        RequireAll(ids, songs.Select(song => song.Id));

        // A song's queue items go with it (the foreign key cascades), so deleting one whose download
        // or import is still running would orphan the transfer and pull the row from under the
        // tracker. Those are refused, and nothing is deleted.
        var busy = await _database.QueueItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.SongId) && ActiveStates.Contains(item.State))
            .Select(item => item.SongId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (busy.Count > 0)
        {
            throw new SongsBusyException([.. busy.Order()]);
        }

        // One SaveChanges is one transaction: every song goes, or none does. As with a single
        // delete, the credits, album context and file row cascade; the file stays on disk.
        _database.Songs.RemoveRange(songs);
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return songs.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SongTagCount>> GetTagsAsync(CancellationToken cancellationToken)
    {
        // A few thousand songs: loading the tag lists is one cheap query.
        var lists = await _database.Songs
            .AsNoTracking()
            .Where(song => song.Tags.Count > 0)
            .Select(song => song.Tags)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. lists
                .SelectMany(list => list.Distinct(StringComparer.Ordinal))
                .GroupBy(tag => tag, StringComparer.Ordinal)
                .Select(group => new SongTagCount(group.Key, group.Count()))
                .OrderBy(tag => tag.Label, StringComparer.Ordinal),
        ];
    }

    /// <summary>Queue states in which a download or an import is still in flight.</summary>
    private static readonly QueueItemState[] ActiveStates =
    [
        QueueItemState.Queued,
        QueueItemState.RemotelyQueued,
        QueueItemState.Downloading,
        QueueItemState.Completed,
        QueueItemState.Importing,
    ];

    private static List<long> RequireIds(IReadOnlyList<long>? songIds)
    {
        if (songIds is not { Count: >= 1 })
        {
            throw new ArgumentException("songIds must hold at least one id.");
        }

        if (songIds.Count > MaxSongIds)
        {
            throw new ArgumentException(
                $"songIds holds more than {MaxSongIds.ToString(CultureInfo.InvariantCulture)} ids.");
        }

        if (songIds.Distinct().Count() != songIds.Count)
        {
            throw new ArgumentException("songIds must not repeat an id.");
        }

        return [.. songIds];
    }

    private static void RequireAll(List<long> wanted, IEnumerable<long> found)
    {
        var present = found.ToHashSet();
        var missing = wanted.Where(id => !present.Contains(id)).ToList();

        if (missing.Count > 0)
        {
            throw new SongsNotFoundException(missing);
        }
    }

    private static TagMode? ParseTagMode(SongEditorRequest request)
    {
        if (request.Tags is null)
        {
            return request.ApplyTags is null
                ? null
                : throw new ArgumentException("applyTags needs tags.");
        }

        return request.ApplyTags?.ToUpperInvariant() switch
        {
            "ADD" => TagMode.Add,
            "REMOVE" => TagMode.Remove,
            "REPLACE" => TagMode.Replace,
            null => throw new ArgumentException("tags need applyTags: add, remove or replace."),
            _ => throw new ArgumentException("applyTags must be add, remove or replace."),
        };
    }

    private sealed record MoveSongsCommandBody(string Name, IReadOnlyList<long> SongIds, long LibraryId);
}
