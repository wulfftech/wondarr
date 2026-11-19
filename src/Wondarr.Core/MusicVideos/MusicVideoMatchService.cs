using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;

namespace Wondarr.Core.MusicVideos;

/// <summary>A song as the Match page shows it.</summary>
/// <param name="Id">The song id.</param>
/// <param name="Title">The song's title.</param>
/// <param name="ArtistCredit">The artist credit as it is written.</param>
/// <param name="Album">The album title, or <see langword="null"/>.</param>
/// <param name="DurationMs">The song's length, or <see langword="null"/>.</param>
/// <param name="HasVideo">Whether the song already has a video, or one on its way.</param>
public sealed record MusicVideoMatchSong(long Id, string Title, string ArtistCredit, string? Album, int? DurationMs, bool HasVideo);

/// <summary>A song a file might be, with its score.</summary>
/// <param name="Song">The song, or <see langword="null"/> when it was deleted since.</param>
/// <param name="SongId">The song id.</param>
/// <param name="Score">The score the scan gave it.</param>
/// <param name="Reason">Where the name came from, or why the song cannot take the file.</param>
public sealed record MusicVideoMatchCandidate(long SongId, MusicVideoMatchSong? Song, double Score, string Reason);

/// <summary>One video file on the Match page.</summary>
/// <param name="Id">The row id.</param>
/// <param name="State">Where the file stands.</param>
/// <param name="RelativePath">The path under the video root, <c>/</c>-separated.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Container">The container, or <see langword="null"/> when it was never read.</param>
/// <param name="DurationMs">The length, or <see langword="null"/>.</param>
/// <param name="Width">The picture width, or <see langword="null"/>.</param>
/// <param name="Height">The picture height, or <see langword="null"/>.</param>
/// <param name="ParsedArtist">The artist the file name says.</param>
/// <param name="ParsedTitle">The title the file name says.</param>
/// <param name="NfoArtist">The artist the <c>.nfo</c> says.</param>
/// <param name="NfoTitle">The title the <c>.nfo</c> says.</param>
/// <param name="TagArtist">The artist tag.</param>
/// <param name="TagTitle">The title tag.</param>
/// <param name="Candidates">The songs it might be, best first.</param>
/// <param name="HasThumbnail">Whether an image lies beside the file.</param>
public sealed record MusicVideoMatchRow(
    long Id,
    MusicVideoFileState State,
    string RelativePath,
    long Size,
    string? Container,
    int? DurationMs,
    int? Width,
    int? Height,
    string? ParsedArtist,
    string? ParsedTitle,
    string? NfoArtist,
    string? NfoTitle,
    string? TagArtist,
    string? TagTitle,
    IReadOnlyList<MusicVideoMatchCandidate> Candidates,
    bool HasThumbnail);

/// <summary>How many files are in each state, for the badge of the Match page tab.</summary>
/// <param name="Pending">Waiting to be identified.</param>
/// <param name="Adopted">Linked to their song.</param>
/// <param name="Ambiguous">Waiting for a person to pick.</param>
/// <param name="Unmatched">No song fits.</param>
/// <param name="Ignored">Marked as not a video of the library.</param>
/// <param name="Missing">Gone from the folder.</param>
public sealed record MusicVideoMatchSummary(int Pending, int Adopted, int Ambiguous, int Unmatched, int Ignored, int Missing);

/// <summary>What a Match page action did to one file.</summary>
public enum MusicVideoMatchOutcome
{
    /// <summary>The file is now the song's video.</summary>
    Adopted,

    /// <summary>The file is ignored.</summary>
    Ignored,

    /// <summary>The file is back in the queue.</summary>
    Unignored,

    /// <summary>There is no such file.</summary>
    FileNotFound,

    /// <summary>There is no such song.</summary>
    SongNotFound,

    /// <summary>The file is not on disk any more.</summary>
    FileMissing,

    /// <summary>The song already has a video and the caller did not say to replace it.</summary>
    SongHasVideo,

    /// <summary>A download for the song is queued or running.</summary>
    DownloadInProgress,

    /// <summary>The song's old video could not be recycled; nothing was changed.</summary>
    RecycleFailed,

    /// <summary>The file is not ignored, so there is nothing to undo.</summary>
    NotIgnored,
}

/// <summary>The answer to a Match page action.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A sentence for the person when something went wrong, or <see langword="null"/>.</param>
public sealed record MusicVideoMatchResult(MusicVideoMatchOutcome Outcome, string? Message = null);

/// <summary>What a bulk action did.</summary>
/// <param name="Requested">How many files were named.</param>
/// <param name="Adopted">How many were linked to their top candidate.</param>
/// <param name="Ignored">How many were ignored.</param>
/// <param name="Skipped">How many were left as they were: no candidate, its song has a video, or the file is gone.</param>
public sealed record MusicVideoBulkResult(int Requested, int Adopted, int Ignored, int Skipped);

/// <summary>What a bulk action may do.</summary>
public enum MusicVideoBulkAction
{
    /// <summary>Link each file to its top candidate when that song has no video.</summary>
    AcceptTop,

    /// <summary>Ignore each file.</summary>
    Ignore,
}

/// <summary>The work behind the Match page's Music videos tab (ADR-0014, the 2026-11-19 amendment).</summary>
public interface IMusicVideoMatchService
{
    /// <summary>The most files one bulk action may name.</summary>
    public const int MaxBulkFiles = 500;

    /// <summary>The most songs a search returns.</summary>
    public const int MaxSongSearchResults = 25;

    /// <summary>The count of files per state.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<MusicVideoMatchSummary> GetSummaryAsync(CancellationToken cancellationToken);

    /// <summary>One page of the files in a state, by path.</summary>
    /// <param name="state">The state to list.</param>
    /// <param name="paging">The page.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<PagedResult<MusicVideoMatchRow>> ListAsync(MusicVideoFileState state, PagingSpec paging, CancellationToken cancellationToken);

    /// <summary>
    /// The image that lies beside a file's own video: <c>name-thumb.jpg</c>, <c>name.jpg</c> or <c>name.png</c>, inside
    /// the video root. The path is built from the row, never taken from a caller, and a link is not followed.
    /// </summary>
    /// <param name="id">The file's row id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The image's path, or <see langword="null"/> when there is no such file or no image.</returns>
    Task<string?> FindThumbnailAsync(long id, CancellationToken cancellationToken);

    /// <summary>Links a file to a song where it lies.</summary>
    /// <param name="id">The file's row id.</param>
    /// <param name="songId">The song.</param>
    /// <param name="replace">Whether a video the song already has may be replaced.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<MusicVideoMatchResult> ResolveAsync(long id, long songId, bool replace, CancellationToken cancellationToken);

    /// <summary>Marks a file as not a video of the library, or undoes that.</summary>
    /// <param name="id">The file's row id.</param>
    /// <param name="ignore"><see langword="true"/> to ignore it, <see langword="false"/> to put it back in the queue.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<MusicVideoMatchResult> IgnoreAsync(long id, bool ignore, CancellationToken cancellationToken);

    /// <summary>Accepts the top candidate of each file, or ignores each.</summary>
    /// <param name="ids">The files' row ids.</param>
    /// <param name="action">What to do.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<MusicVideoBulkResult> BulkAsync(IReadOnlyCollection<long> ids, MusicVideoBulkAction action, CancellationToken cancellationToken);

    /// <summary>Searches the library's songs by artist and title for the manual pick.</summary>
    /// <param name="term">The words; each has to be in the title or the artist credit.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<MusicVideoMatchSong>> SearchSongsAsync(string term, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MusicVideoMatchService : IMusicVideoMatchService
{
    /// <summary>The images that may sit beside a video, in the order they are looked for.</summary>
    private static readonly string[] ThumbnailSuffixes = ["-thumb.jpg", ".jpg", ".png"];

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly IMusicVideoService _videos;
    private readonly ILogger<MusicVideoMatchService> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoMatchService"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the root.</param>
    /// <param name="videos">Recycles the video a replacement takes the place of.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoMatchService(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        IMusicVideoService videos,
        ILogger<MusicVideoMatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _videos = videos;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MusicVideoMatchSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var counts = await _database.MusicVideoFiles
            .AsNoTracking()
            .GroupBy(file => file.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int Of(MusicVideoFileState state) => counts.Find(row => row.State == state)?.Count ?? 0;

        return new MusicVideoMatchSummary(
            Of(MusicVideoFileState.Pending),
            Of(MusicVideoFileState.Adopted),
            Of(MusicVideoFileState.Ambiguous),
            Of(MusicVideoFileState.Unmatched),
            Of(MusicVideoFileState.Ignored),
            Of(MusicVideoFileState.Missing));
    }

    /// <inheritdoc />
    public async Task<PagedResult<MusicVideoMatchRow>> ListAsync(MusicVideoFileState state, PagingSpec paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = _database.MusicVideoFiles.AsNoTracking().Where(file => file.State == state);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var files = await (paging.Descending ? query.OrderByDescending(file => file.Path) : query.OrderBy(file => file.Path))
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = files.ToDictionary(file => file.Id, file => MusicVideoFileRules.Read(file.Candidates));
        var songIds = candidates.Values.SelectMany(list => list).Select(candidate => candidate.SongId).Distinct().ToList();
        var songs = await LoadSongsAsync(songIds, cancellationToken).ConfigureAwait(false);
        var thumbnails = await HaveThumbnailsAsync(files, cancellationToken).ConfigureAwait(false);
        var root = _options.CurrentValue.RootPath;

        var rows = files
            .Select(file => new MusicVideoMatchRow(
                file.Id,
                file.State,
                Relative(root, file.Path),
                file.Size,
                file.Container,
                file.DurationMs,
                file.Width,
                file.Height,
                file.ParsedArtist,
                file.ParsedTitle,
                file.NfoArtist,
                file.NfoTitle,
                file.TagArtist,
                file.TagTitle,
                [.. candidates[file.Id].Select(candidate => new MusicVideoMatchCandidate(
                    candidate.SongId,
                    songs.GetValueOrDefault(candidate.SongId),
                    candidate.Score,
                    candidate.Reason))],
                thumbnails.Contains(file.Id)))
            .ToList();

        return new PagedResult<MusicVideoMatchRow>(rows, total);
    }

    /// <inheritdoc />
    public async Task<string?> FindThumbnailAsync(long id, CancellationToken cancellationToken)
    {
        var file = await _database.MusicVideoFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        var root = _options.CurrentValue.RootPath;

        if (file is null || string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return await MusicVideoDisk.RunAsync(() => ThumbnailOf(root, file.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<MusicVideoMatchResult> ResolveAsync(long id, long songId, bool replace, CancellationToken cancellationToken)
    {
        var file = await _database.MusicVideoFiles.FirstOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);

        if (file is null)
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.FileNotFound);
        }

        if (file.State == MusicVideoFileState.Missing)
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.FileMissing, "The file is not in the folder any more.");
        }

        if (!await _database.Songs.AsNoTracking().AnyAsync(song => song.Id == songId, cancellationToken).ConfigureAwait(false))
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.SongNotFound);
        }

        if (await ActiveDownloads.ExistsAsync(_database, songId, cancellationToken).ConfigureAwait(false))
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.DownloadInProgress, "A download for the song is in progress.");
        }

        var row = await _database.MusicVideos.FirstOrDefaultAsync(video => video.SongId == songId, cancellationToken).ConfigureAwait(false);

        // The file already is this song's video: nothing to do.
        if (row is { Status: MusicVideoStatus.Present } && PathsEqual(row.Path, file.Path))
        {
            if (file.State != MusicVideoFileState.Adopted || file.SongId != songId)
            {
                file.State = MusicVideoFileState.Adopted;
                file.SongId = songId;
                file.MissingSince = null;
                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new MusicVideoMatchResult(MusicVideoMatchOutcome.Adopted);
        }

        if (row is { Status: MusicVideoStatus.Present } && !replace)
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.SongHasVideo, "The song already has a video.");
        }

        // Replacing: an adopted old video stays where it lies and goes back to the queue; a downloaded one is recycled, but
        // only after the database says the song has its new video, so a failed recycle can never leave the row half done.
        MusicVideo? toRecycle = null;

        if (row is { Status: MusicVideoStatus.Present })
        {
            if (row.FoundBy != MusicVideoFoundBy.Existing)
            {
                toRecycle = new MusicVideo { SongId = row.SongId, Path = row.Path, FoundBy = row.FoundBy };
            }
            else if (row.Path is { } oldPath)
            {
                var oldFile = await _database.MusicVideoFiles
                    .FirstOrDefaultAsync(other => other.Path == oldPath, cancellationToken)
                    .ConfigureAwait(false);

                if (oldFile is not null)
                {
                    oldFile.State = MusicVideoFileState.Ambiguous;
                    oldFile.SongId = null;
                    oldFile.Candidates = MusicVideoFileRules.Write([new MusicVideoFileCandidate(songId, 1, MusicVideoFileRules.SongHasVideo)]);
                }
            }
        }

        // One transaction: the unlink, the removal of the old row and the link are saved together or not at all.
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The file leaves the song it was adopted for.
            await UnlinkAsync(file, cancellationToken).ConfigureAwait(false);

            // A row that is not Present (a failed or stranded download) gives way too.
            if (row is not null)
            {
                _database.MusicVideos.Remove(row);
            }

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (!await MusicVideoFileRules.LinkAsync(_database, file, songId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _database.ChangeTracker.Clear();

                return new MusicVideoMatchResult(MusicVideoMatchOutcome.SongHasVideo, "The song already has a video.");
            }

            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (MusicVideoFileRules.IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _database.ChangeTracker.Clear();

            return new MusicVideoMatchResult(MusicVideoMatchOutcome.SongHasVideo, "The song already has a video.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _database.ChangeTracker.Clear();

            throw;
        }

        if (toRecycle is not null)
        {
            try
            {
                await _videos.RecycleFilesAsync(toRecycle, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MusicVideoInReferenceLibraryException
                or MusicVideoPathException
                or MusicVideoFileException
                or MusicVideoTimeoutException)
            {
                // The song has its new video; the old files stay where they are, and the log says so.
                LogOldVideoLeft(_logger, toRecycle.Path ?? string.Empty, exception);
            }
        }

        LogResolved(_logger, file.Path, songId);

        return new MusicVideoMatchResult(MusicVideoMatchOutcome.Adopted);
    }

    /// <inheritdoc />
    public async Task<MusicVideoMatchResult> IgnoreAsync(long id, bool ignore, CancellationToken cancellationToken)
    {
        var file = await _database.MusicVideoFiles.FirstOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);

        if (file is null)
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.FileNotFound);
        }

        if (!ignore)
        {
            if (file.State != MusicVideoFileState.Ignored)
            {
                return new MusicVideoMatchResult(MusicVideoMatchOutcome.NotIgnored, "The file is not ignored.");
            }

            // What the scan concluded is still in the row: back to the queue it came from.
            file.State = MusicVideoFileRules.Read(file.Candidates).Count > 0 ? MusicVideoFileState.Ambiguous : MusicVideoFileState.Unmatched;
            file.MissingSince = null;
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new MusicVideoMatchResult(MusicVideoMatchOutcome.Unignored);
        }

        if (file.State == MusicVideoFileState.Missing)
        {
            return new MusicVideoMatchResult(MusicVideoMatchOutcome.FileMissing, "The file is not in the folder any more.");
        }

        // An adopted file that is ignored stops being its song's video; the file itself is left where it lies.
        await UnlinkAsync(file, cancellationToken).ConfigureAwait(false);
        file.State = MusicVideoFileState.Ignored;
        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MusicVideoMatchResult(MusicVideoMatchOutcome.Ignored);
    }

    /// <inheritdoc />
    public async Task<MusicVideoBulkResult> BulkAsync(IReadOnlyCollection<long> ids, MusicVideoBulkAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var adopted = 0;
        var ignored = 0;
        var skipped = 0;

        foreach (var id in ids.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (action == MusicVideoBulkAction.Ignore)
            {
                var result = await IgnoreAsync(id, ignore: true, cancellationToken).ConfigureAwait(false);

                if (result.Outcome == MusicVideoMatchOutcome.Ignored)
                {
                    ignored++;
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            var file = await _database.MusicVideoFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
                .ConfigureAwait(false);

            // Only what is waiting for a person is accepted: an adopted file already has its song.
            if (file is not { State: MusicVideoFileState.Ambiguous or MusicVideoFileState.Unmatched })
            {
                skipped++;

                continue;
            }

            // Only a top candidate the scan itself would have adopted: at the adopt line, clear of the second.
            var stored = MusicVideoFileRules.Read(file.Candidates);

            if (stored.Count == 0
                || stored[0].Score < MusicVideoFileRules.AdoptScore
                || (stored.Count > 1 && stored[0].Score - stored[1].Score <= MusicVideoFileRules.AdoptMargin + 1e-9)
                || stored[0].Reason == MusicVideoFileRules.LyricVideo)
            {
                skipped++;

                continue;
            }

            var top = stored[0];

            var accepted = await ResolveAsync(id, top.SongId, replace: false, cancellationToken).ConfigureAwait(false);

            if (accepted.Outcome == MusicVideoMatchOutcome.Adopted)
            {
                adopted++;
            }
            else
            {
                skipped++;
            }
        }

        return new MusicVideoBulkResult(ids.Count, adopted, ignored, skipped);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MusicVideoMatchSong>> SearchSongsAsync(string term, CancellationToken cancellationToken)
    {
        var words = (term ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(6)
            .ToList();

        if (words.Count == 0)
        {
            return [];
        }

        var query = _database.Songs.AsNoTracking();

        foreach (var word in words)
        {
            var pattern = "%" + EscapeLike(word) + "%";
            query = query.Where(song =>
                EF.Functions.Like(song.Title, pattern, "\\")
                || EF.Functions.Like(song.ArtistCredit, pattern, "\\"));
        }

        var ids = await query
            .OrderBy(song => song.ArtistCredit)
            .ThenBy(song => song.Title)
            .ThenBy(song => song.Id)
            .Select(song => song.Id)
            .Take(IMusicVideoMatchService.MaxSongSearchResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songs = await LoadSongsAsync(ids, cancellationToken).ConfigureAwait(false);

        return [.. ids.Select(id => songs.GetValueOrDefault(id)).OfType<MusicVideoMatchSong>()];
    }

    /// <summary>Takes the <see cref="MusicVideoFoundBy.Existing"/> row of this file away from its song; the file is not touched.</summary>
    private async Task UnlinkAsync(MusicVideoFile file, CancellationToken cancellationToken)
    {
        var path = file.Path;
        var video = await _database.MusicVideos
            .FirstOrDefaultAsync(candidate => candidate.FoundBy == MusicVideoFoundBy.Existing && candidate.Path == path, cancellationToken)
            .ConfigureAwait(false);

        if (video is not null && _database.Entry(video).State != EntityState.Deleted)
        {
            _database.MusicVideos.Remove(video);
        }

        file.SongId = null;
    }

    private async Task<Dictionary<long, MusicVideoMatchSong>> LoadSongsAsync(List<long> songIds, CancellationToken cancellationToken)
    {
        if (songIds.Count == 0)
        {
            return [];
        }

        var songs = await _database.Songs
            .AsNoTracking()
            .Where(song => songIds.Contains(song.Id))
            .Select(song => new
            {
                song.Id,
                song.Title,
                song.ArtistCredit,
                Album = song.AlbumContext == null ? null : song.AlbumContext.AlbumTitle,
                song.DurationMs,
                FileDurationMs = song.File == null ? null : song.File.DurationMs,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var withVideo = (await _database.MusicVideos
                .AsNoTracking()
                .Where(video => songIds.Contains(video.SongId) && video.Status != MusicVideoStatus.Failed)
                .Select(video => video.SongId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();

        return songs.ToDictionary(
            song => song.Id,
            song => new MusicVideoMatchSong(
                song.Id,
                song.Title,
                song.ArtistCredit,
                song.Album,
                song.DurationMs ?? song.FileDurationMs,
                withVideo.Contains(song.Id)));
    }

    /// <summary>The ids of the files that have an image beside them, asked of the disk once for the whole page.</summary>
    private async Task<HashSet<long>> HaveThumbnailsAsync(List<MusicVideoFile> files, CancellationToken cancellationToken)
    {
        var root = _options.CurrentValue.RootPath;

        if (files.Count == 0 || string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        try
        {
            return await MusicVideoDisk
                .RunAsync(() => files.Where(file => ThumbnailOf(root, file.Path) is not null).Select(file => file.Id).ToHashSet(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MusicVideoTimeoutException)
        {
            // A share that does not answer shows the list without pictures.
            return [];
        }
    }

    /// <summary>
    /// The image beside a video: the video's own path without its extension plus a known suffix, strictly inside the root,
    /// and a real file, not a link to somewhere else.
    /// </summary>
    private static string? ThumbnailOf(string root, string videoPath)
    {
        try
        {
            if (!PathRules.IsStrictlyInside(root, videoPath))
            {
                return null;
            }

            var stem = Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath));

            foreach (var suffix in ThumbnailSuffixes)
            {
                var candidate = stem + suffix;
                var info = new FileInfo(candidate);

                if (info.Exists && info.LinkTarget is null && PathRules.IsStrictlyInside(root, candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool PathsEqual(string? first, string second)
    {
        try
        {
            return first is not null && PathRules.AreEqual(first, second);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Relative(string? root, string path)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return path;
        }

        try
        {
            var relative = Path.GetRelativePath(root, path);

            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? path
                : Path.DirectorySeparatorChar == '\\' ? relative.Replace('\\', '/') : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The old music video {Path} could not be recycled after its song was given another video; it stays where it is")]
    private static partial void LogOldVideoLeft(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The video {Path} was matched to song {SongId} on the Match page")]
    private static partial void LogResolved(ILogger logger, string path, long songId);
}
