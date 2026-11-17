using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>What searching for a song's music video found.</summary>
/// <param name="Candidates">The candidates, best first; none is blocklisted.</param>
/// <param name="Confident">The candidate to download without asking, or <see langword="null"/> when none is confident.</param>
/// <param name="Partial">Whether MusicBrainz failed to answer, so "no confident match" is not an answer.</param>
public sealed record MusicVideoSearchResult(IReadOnlyList<MusicVideoCandidate> Candidates, MusicVideoCandidate? Confident, bool Partial = false)
{
    /// <summary>
    /// The candidate an automatic path may download: the confident one, but from a partial result only when it is a
    /// YouTube search result that is confident on its own.
    /// </summary>
    public MusicVideoCandidate? AutoDownload =>
        Confident is { } confident && (!Partial || confident.FoundBy == MusicVideoFoundBy.YouTubeSearch) ? confident : null;
}

/// <summary>What queueing a download did.</summary>
public enum MusicVideoQueueOutcome
{
    /// <summary>The download command is queued.</summary>
    Queued,

    /// <summary>There is no such song.</summary>
    SongNotFound,

    /// <summary>The video is on the song's blocklist.</summary>
    Blocklisted,

    /// <summary>A download for the song is already queued or running.</summary>
    InProgress,

    /// <summary>The song already holds a video and the caller may not replace it (or it is this very video).</summary>
    AlreadyFiled,
}

/// <summary>The outcome of queueing a download, and the command that will run it.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="CommandId">The queued command, when <paramref name="Outcome"/> is <see cref="MusicVideoQueueOutcome.Queued"/>.</param>
public sealed record MusicVideoQueueResult(MusicVideoQueueOutcome Outcome, long? CommandId = null);

/// <summary>Finds a song's music video and queues its download (ADR-0014).</summary>
public interface IMusicVideoSearchService
{
    /// <summary>The longest a fruitless song is left alone by the scheduled task.</summary>
    public static readonly TimeSpan SearchMemory = TimeSpan.FromDays(30);

    /// <summary>Searches MusicBrainz and YouTube Music for the song's video. Nothing is queued or written.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The candidates, or <see langword="null"/> when there is no such song.</returns>
    Task<MusicVideoSearchResult?> SearchAsync(long songId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the download of one video for a song. The song's row is created (or a failed one reset) as
    /// <see cref="MusicVideoStatus.Queued"/>; a song that already holds a different video keeps it until the
    /// download has been checked and filed, which then replaces it.
    /// </summary>
    /// <param name="songId">The song.</param>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="foundBy">How the video was found.</param>
    /// <param name="trigger">What asked for it.</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <param name="replace">
    /// Whether a filed video may be replaced. Only a person's pick (a grab) says yes: a search, a bulk search and the
    /// scheduled task never replace one.
    /// </param>
    Task<MusicVideoQueueResult> QueueDownloadAsync(
        long songId,
        string videoId,
        MusicVideoFoundBy foundBy,
        CommandTrigger trigger,
        CancellationToken cancellationToken,
        bool replace = false);

    /// <summary>
    /// Queues a confident-only search (<c>MusicVideoSearch</c> with <c>autoDownload</c>) for each of the songs that
    /// exist. A song that is not there is skipped.
    /// </summary>
    /// <param name="songIds">The songs.</param>
    /// <param name="trigger">What asked for the searches.</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns>The ids of the queued commands, in song order.</returns>
    Task<IReadOnlyList<long>> QueueSearchesAsync(
        IReadOnlyCollection<long> songIds,
        CommandTrigger trigger,
        CancellationToken cancellationToken);

    /// <summary>
    /// The monitored songs that have a file and no video row, oldest added first, leaving out those the scheduled
    /// task found nothing for within <see cref="SearchMemory"/>.
    /// </summary>
    /// <param name="limit">How many songs at most.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<long>> NextMissingAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Remembers that a search for the song found nothing to download, so the scheduled task leaves it alone for a while.</summary>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task RememberFruitlessAsync(long songId, CancellationToken cancellationToken);
}

/// <summary>The JSON bodies of the music video commands, camelCase like every other body.</summary>
public static class MusicVideoCommandBodies
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The body of a <c>MusicVideoSearch</c> command.</summary>
    /// <param name="SongId">The song.</param>
    /// <param name="AutoDownload">Whether a confident candidate is downloaded.</param>
    public sealed record Search(long SongId, bool AutoDownload);

    /// <summary>The body of a <c>MusicVideoDownload</c> command.</summary>
    /// <param name="SongId">The song.</param>
    /// <param name="YouTubeVideoId">The YouTube video id.</param>
    /// <param name="FoundBy">How the video was found.</param>
    public sealed record Download(long SongId, string YouTubeVideoId, MusicVideoFoundBy FoundBy);

    /// <summary>Writes a command body.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="body">The body.</param>
    public static string Write<T>(T body) => JsonSerializer.Serialize(body, Json);

    /// <summary>Reads a command body.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="json">The raw body.</param>
    /// <param name="name">The command name, for the message of a body that cannot be read.</param>
    /// <exception cref="ArgumentException">The body is missing or not what the command takes.</exception>
    public static T Read<T>(string? json, string name)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException($"The {name} command needs a body.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json)
                ?? throw new ArgumentException($"The {name} command body is empty.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"The {name} command body is not valid: {exception.Message}", exception);
        }
    }
}

/// <inheritdoc />
public sealed class MusicVideoSearchService : IMusicVideoSearchService
{
    private readonly WondarrDbContext _database;
    private readonly IMusicVideoFinder _finder;
    private readonly ICommandQueue _commands;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoSearchService"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="finder">Finds the candidates.</param>
    /// <param name="commands">Queues the download.</param>
    /// <param name="time">The clock the search memory runs on.</param>
    public MusicVideoSearchService(
        WondarrDbContext database,
        IMusicVideoFinder finder,
        ICommandQueue commands,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(time);

        _database = database;
        _finder = finder;
        _commands = commands;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<MusicVideoSearchResult?> SearchAsync(long songId, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .AsNoTracking()
            .Include(candidate => candidate.PrimaryArtist)
            .Include(candidate => candidate.Artists)
                .ThenInclude(credit => credit.Artist)
            .Include(candidate => candidate.File)
            .FirstOrDefaultAsync(candidate => candidate.Id == songId, cancellationToken)
            .ConfigureAwait(false);

        if (song is null)
        {
            return null;
        }

        var blocklisted = (await _database.MusicVideoBlocklist
                .AsNoTracking()
                .Where(item => item.SongId == songId)
                .Select(item => item.YouTubeVideoId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var artists = song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => credit.Artist.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(song.PrimaryArtist?.Name))
        {
            artists.Add(song.PrimaryArtist!.Name);
        }

        var query = new MusicVideoQuery(
            song.Title,
            artists,
            song.MbRecordingId,
            song.DurationMs ?? song.File?.DurationMs,
            blocklisted);

        var found = await _finder.FindAsync(query, cancellationToken).ConfigureAwait(false);

        // The finder drops these already; a second filter keeps a wrong finder from handing one to a download.
        var candidates = found.Where(candidate => !blocklisted.Contains(candidate.VideoId)).ToList();

        // A confident match that a failed MusicBrainz lookup leaves standing is the search's own: it is YouTube's.
        return new MusicVideoSearchResult(candidates, candidates.Find(candidate => candidate.Confident), found.Partial);
    }

    /// <inheritdoc />
    public async Task<MusicVideoQueueResult> QueueDownloadAsync(
        long songId,
        string videoId,
        MusicVideoFoundBy foundBy,
        CommandTrigger trigger,
        CancellationToken cancellationToken,
        bool replace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        if (!await _database.Songs.AsNoTracking().AnyAsync(song => song.Id == songId, cancellationToken).ConfigureAwait(false))
        {
            return new MusicVideoQueueResult(MusicVideoQueueOutcome.SongNotFound);
        }

        if (await _database.MusicVideoBlocklist
                .AsNoTracking()
                .AnyAsync(item => item.SongId == songId && item.YouTubeVideoId == videoId, cancellationToken)
                .ConfigureAwait(false))
        {
            return new MusicVideoQueueResult(MusicVideoQueueOutcome.Blocklisted);
        }

        // One download at a time per song, a replacement included: a second pick while one is on its way is refused.
        var active = await ActiveDownloads.ExistsAsync(_database, songId, cancellationToken).ConfigureAwait(false);

        var row = await _database.MusicVideos
            .FirstOrDefaultAsync(video => video.SongId == songId, cancellationToken)
            .ConfigureAwait(false);
        var created = false;

        if (active && row is not null)
        {
            return new MusicVideoQueueResult(MusicVideoQueueOutcome.InProgress);
        }

        if (row is null)
        {
            _database.MusicVideos.Add(new MusicVideo
            {
                SongId = songId,
                Status = MusicVideoStatus.Queued,
                YouTubeVideoId = videoId,
                FoundBy = foundBy,
            });
            created = true;
        }
        else if (row.Status == MusicVideoStatus.Present)
        {
            // A different video replaces the filed one only when a person picked it, and only once it has been
            // downloaded and checked.
            if (!replace || string.Equals(row.YouTubeVideoId, videoId, StringComparison.Ordinal))
            {
                return new MusicVideoQueueResult(MusicVideoQueueOutcome.AlreadyFiled);
            }
        }
        else
        {
            // A queued or downloading row with no command behind it was left by a crash: it is reclaimed.
            row.Status = MusicVideoStatus.Queued;
            row.YouTubeVideoId = videoId;
            row.FoundBy = foundBy;
            row.FailureReason = null;
        }

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another request created the song's row first.
            return new MusicVideoQueueResult(MusicVideoQueueOutcome.InProgress);
        }

        try
        {
            var command = await _commands
                .EnqueueAsync(
                    MusicVideoDownloadCommandHandler.CommandName,
                    MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, videoId, foundBy)),
                    trigger,
                    cancellationToken)
                .ConfigureAwait(false);

            return new MusicVideoQueueResult(MusicVideoQueueOutcome.Queued, command.Id);
        }
        catch
        {
            // Nothing will run for a row that was only just made: it must not wait for a command that never comes.
            if (created)
            {
                await _database.MusicVideos
                    .Where(video => video.SongId == songId && video.Status == MusicVideoStatus.Queued)
                    .ExecuteDeleteAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> QueueSearchesAsync(
        IReadOnlyCollection<long> songIds,
        CommandTrigger trigger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        var existing = await _database.Songs
            .AsNoTracking()
            .Where(song => songIds.Contains(song.Id))
            .Select(song => song.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var commandIds = new List<long>(existing.Count);

        foreach (var songId in existing)
        {
            var command = await _commands
                .EnqueueAsync(
                    MusicVideoSearchCommandHandler.CommandName,
                    MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Search(songId, AutoDownload: true)),
                    trigger,
                    cancellationToken)
                .ConfigureAwait(false);

            commandIds.Add(command.Id);
        }

        return commandIds;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> NextMissingAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit < 1)
        {
            return [];
        }

        var cutoff = _time.GetUtcNow().UtcDateTime - IMusicVideoSearchService.SearchMemory;

        return await _database.Songs
            .AsNoTracking()
            .Where(song => song.Monitored
                && song.File != null
                && !_database.MusicVideos.Any(video => video.SongId == song.Id)
                && !_database.MusicVideoLastSearches.Any(search => search.SongId == song.Id && search.SearchedAt > cutoff))
            .OrderBy(song => song.CreatedAt)
            .ThenBy(song => song.Id)
            .Select(song => song.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RememberFruitlessAsync(long songId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var existing = await _database.MusicVideoLastSearches
            .FirstOrDefaultAsync(search => search.SongId == songId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _database.MusicVideoLastSearches.Add(new MusicVideoLastSearch { SongId = songId, SearchedAt = now });
        }
        else
        {
            existing.SearchedAt = now;
        }

        try
        {
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The song was remembered by a run that overlapped this one; that is all this wanted.
            _database.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite
        && sqlite.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Whether a <c>MusicVideoDownload</c> command for a song is queued or running.</summary>
internal static class ActiveDownloads
{
    /// <summary>Asks the command table.</summary>
    /// <param name="database">The database.</param>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public static async Task<bool> ExistsAsync(WondarrDbContext database, long songId, CancellationToken cancellationToken)
    {
        var bodies = await database.Commands
            .AsNoTracking()
            .Where(command => command.Name == MusicVideoDownloadCommandHandler.CommandName
                && (command.Status == CommandStatus.Queued || command.Status == CommandStatus.Started))
            .Select(command => command.Body)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return bodies.Any(body =>
        {
            try
            {
                return body is not null
                    && MusicVideoCommandBodies.Read<MusicVideoCommandBodies.Download>(body, MusicVideoDownloadCommandHandler.CommandName).SongId == songId;
            }
            catch (ArgumentException)
            {
                return false;
            }
        });
    }
}
