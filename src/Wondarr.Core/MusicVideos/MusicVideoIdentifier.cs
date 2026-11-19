using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Persistence;
using Wondarr.Core.Verification;

namespace Wondarr.Core.MusicVideos;

/// <summary>One song a video file might be, as it is stored in <c>music_video_file.candidates</c>.</summary>
/// <param name="SongId">The song.</param>
/// <param name="Score">The title similarity from 0 to 1, plus 0.05 when the length fits.</param>
/// <param name="Reason">Where the name came from, or why the song cannot take the file.</param>
public sealed record MusicVideoFileCandidate(long SongId, double Score, string Reason);

/// <summary>What one identification pass did.</summary>
/// <param name="Adopted">How many files were linked to their song where they lie.</param>
/// <param name="Ambiguous">How many files wait for the user to pick.</param>
/// <param name="Unmatched">How many files no song fits.</param>
public sealed record MusicVideoIdentifyResult(int Adopted, int Ambiguous, int Unmatched);

/// <summary>Identifies the video files the scan found against the library's songs (ADR-0014, 2026-11-19).</summary>
public interface IMusicVideoIdentifier
{
    /// <summary>
    /// Identifies every <see cref="MusicVideoFileState.Pending"/> file: a confident match is adopted where the file lies,
    /// everything else is left for the Match page. Never touches a file on disk.
    /// </summary>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the pass between files.</param>
    Task<MusicVideoIdentifyResult> IdentifyPendingAsync(Func<string, Task>? progress, CancellationToken cancellationToken);
}

/// <summary>The rules for turning a file into a song, and the stored form of the candidates.</summary>
public static class MusicVideoFileRules
{
    /// <summary>The score at which a lone candidate is adopted without asking.</summary>
    public const double AdoptScore = 0.95;

    /// <summary>How far ahead of every other candidate the best one has to be to be adopted.</summary>
    public const double AdoptMargin = 0.05;

    /// <summary>The most candidates a row keeps.</summary>
    public const int MaxCandidates = 5;

    /// <summary>What a video's length adds to a candidate that fits: a tiebreaker, never enough to make a match.</summary>
    public const double LengthBonus = 0.05;

    /// <summary>The reason a song that already holds a video gives.</summary>
    public const string SongHasVideo = "the song already has a video";

    /// <summary>The reason a file named as a lyric video or a visualiser gives.</summary>
    public const string LyricVideo = "lyric video or visualiser";

    /// <summary>The reason a song whose video is being downloaded gives.</summary>
    public const string SongDownloading = "a download for the song is in progress";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes a row's candidates.</summary>
    /// <param name="candidates">The candidates, best first.</param>
    public static string Write(IEnumerable<MusicVideoFileCandidate> candidates) =>
        JsonSerializer.Serialize(candidates.Take(MaxCandidates).ToList(), Json);

    /// <summary>Reads a row's candidates; a value that cannot be read is no candidates.</summary>
    /// <param name="json">The stored JSON.</param>
    public static IReadOnlyList<MusicVideoFileCandidate> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<MusicVideoFileCandidate>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether a video length fits a song: from 5 s shorter to 90 s longer than the song, which is what a video with an
    /// intro or an outro looks like next to its single.
    /// </summary>
    /// <param name="videoMs">The video's length.</param>
    /// <param name="songMs">The song's length.</param>
    public static bool LengthFits(int? videoMs, int? songMs) =>
        videoMs is > 0 && songMs is > 0 && videoMs >= songMs - 5_000 && videoMs <= songMs + 90_000;

    /// <summary>
    /// Links a file to a song where it lies: the song's <c>music_video</c> row is made (or a failed one reused)
    /// <see cref="MusicVideoStatus.Present"/> with <see cref="MusicVideoFoundBy.Existing"/> at the file's path. Nothing is
    /// saved; nothing on disk is touched.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="file">The file's row, which becomes <see cref="MusicVideoFileState.Adopted"/>.</param>
    /// <param name="songId">The song.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// <see langword="false"/> when the song already has a video (or one on its way), which is never replaced here.
    /// </returns>
    public static async Task<bool> LinkAsync(WondarrDbContext database, MusicVideoFile file, long songId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(file);

        var row = await database.MusicVideos
            .FirstOrDefaultAsync(video => video.SongId == songId, cancellationToken)
            .ConfigureAwait(false);

        if (row is { Status: not MusicVideoStatus.Failed })
        {
            return false;
        }

        if (row is null)
        {
            row = new MusicVideo { SongId = songId };
            database.MusicVideos.Add(row);
        }

        row.Status = MusicVideoStatus.Present;
        row.YouTubeVideoId = string.Empty;
        row.FoundBy = MusicVideoFoundBy.Existing;
        row.Path = file.Path;
        row.Width = file.Width;
        row.Height = file.Height;
        row.VideoCodec = null;
        row.AudioCodec = null;
        row.SizeBytes = file.Size > 0 ? file.Size : null;
        row.DurationMs = file.DurationMs;
        row.FailureReason = null;

        file.State = MusicVideoFileState.Adopted;
        file.SongId = songId;
        file.MissingSince = null;

        database.History.Add(new HistoryItem
        {
            SongId = songId,
            EventType = HistoryEventType.MusicVideoImported,
            Data = JsonSerializer.Serialize(
                new { youTubeVideoId = (string?)null, foundBy = nameof(MusicVideoFoundBy.Existing), path = file.Path, recycledPaths = Array.Empty<string>() },
                Json),
        });

        return true;
    }

    /// <summary>Whether a save failed because a unique index refused a second row.</summary>
    /// <param name="exception">The failure.</param>
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite
            && sqlite.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
    }
}

/// <inheritdoc />
public sealed partial class MusicVideoIdentifier : IMusicVideoIdentifier
{
    /// <summary>How many tokens of a credit are tried as one artist name when looking songs up.</summary>
    private const int MaxArtistWords = 8;

    private readonly WondarrDbContext _database;
    private readonly IOptionsMonitor<MusicVideoOptions> _options;
    private readonly ILogger<MusicVideoIdentifier> _logger;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoIdentifier"/> class.</summary>
    /// <param name="database">The database.</param>
    /// <param name="options">The <c>music_videos</c> settings, for the root and the naming template.</param>
    /// <param name="logger">The logger.</param>
    public MusicVideoIdentifier(
        WondarrDbContext database,
        IOptionsMonitor<MusicVideoOptions> options,
        ILogger<MusicVideoIdentifier> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MusicVideoIdentifyResult> IdentifyPendingAsync(Func<string, Task>? progress, CancellationToken cancellationToken)
    {
        var files = await _database.MusicVideoFiles
            // Waiting files are judged again every time, against the songs as they are now: a song added since may be this
            // file's. Adopted and Ignored files, and what the user settled, are never touched.
            .Where(file => file.State == MusicVideoFileState.Pending
                || file.State == MusicVideoFileState.Ambiguous
                || file.State == MusicVideoFileState.Unmatched)
            .OrderBy(file => file.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (files.Count == 0)
        {
            return new MusicVideoIdentifyResult(0, 0, 0);
        }

        var options = _options.CurrentValue;
        var index = await SongIndex.BuildAsync(_database, cancellationToken).ConfigureAwait(false);
        var blocked = await BlockedSongsAsync(cancellationToken).ConfigureAwait(false);

        var adopted = 0;
        var ambiguous = 0;
        var unmatched = 0;
        var done = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = Candidates(file, index, options);
            var outcome = Decide(file, candidates, blocked);

            file.Candidates = MusicVideoFileRules.Write(outcome.Candidates);
            file.SongId = null;
            file.State = outcome.Unmatched ? MusicVideoFileState.Unmatched : MusicVideoFileState.Ambiguous;

            if (outcome.AdoptSongId is { } songId)
            {
                try
                {
                    if (await MusicVideoFileRules.LinkAsync(_database, file, songId, cancellationToken).ConfigureAwait(false))
                    {
                        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        blocked[songId] = MusicVideoFileRules.SongHasVideo;
                        adopted++;
                        LogAdopted(_logger, file.Path, songId);
                        done++;

                        continue;
                    }
                }
                catch (DbUpdateException exception) when (MusicVideoFileRules.IsUniqueViolation(exception))
                {
                    // A download or a request that overlapped this pass gave the song a video first.
                    ResetAfterFailedLink(file);
                }

                // The song took a video between the check and the write: the file waits for a person.
                file.State = MusicVideoFileState.Ambiguous;
                file.SongId = null;
                file.Candidates = MusicVideoFileRules.Write(
                    outcome.Candidates.Select(candidate => candidate.SongId == songId
                        ? candidate with { Reason = MusicVideoFileRules.SongHasVideo }
                        : candidate));
            }

            if (file.State == MusicVideoFileState.Unmatched)
            {
                unmatched++;
            }
            else
            {
                ambiguous++;
            }

            done++;

            if (done % 100 == 0)
            {
                await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                if (progress is not null)
                {
                    await progress(string.Create(CultureInfo.InvariantCulture, $"Identified {done} of {files.Count} videos")).ConfigureAwait(false);
                }
            }
        }

        await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MusicVideoIdentifyResult(adopted, ambiguous, unmatched);
    }

    private void ResetAfterFailedLink(MusicVideoFile file)
    {
        // The failed save left the new rows in the tracker; they are not wanted.
        foreach (var entry in _database.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added && entry.Entity is MusicVideo or HistoryItem)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        file.State = MusicVideoFileState.Ambiguous;
        file.SongId = null;
    }

    /// <summary>The songs that cannot take another video, with the reason: they hold one, or one is on its way.</summary>
    private async Task<Dictionary<long, string>> BlockedSongsAsync(CancellationToken cancellationToken)
    {
        var rows = await _database.MusicVideos
            .AsNoTracking()
            .Select(video => new { video.SongId, video.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var blocked = new Dictionary<long, string>();

        foreach (var row in rows)
        {
            if (row.Status == MusicVideoStatus.Present)
            {
                blocked[row.SongId] = MusicVideoFileRules.SongHasVideo;
            }
            else if (row.Status is MusicVideoStatus.Queued or MusicVideoStatus.Downloading)
            {
                blocked[row.SongId] = MusicVideoFileRules.SongDownloading;
            }
        }

        return blocked;
    }

    /// <summary>The songs a file might be, best first: the union of what each name source says.</summary>
    private static List<MusicVideoFileCandidate> Candidates(MusicVideoFile file, SongIndex index, MusicVideoOptions options)
    {
        var sources = new List<(string Reason, string? Artist, string? Title)>
        {
            ("the .nfo", file.NfoArtist, file.NfoTitle),
            ("the file's tags", file.TagArtist, file.TagTitle),
        };

        foreach (var name in MusicVideoNameParser.Parse(RelativePath(file.Path, options.RootPath), options.NamingTemplate))
        {
            sources.Add((name.FromTemplate ? "the file name (naming template)" : "the file name", name.Artist, name.Title));
        }

        var best = new Dictionary<long, MusicVideoFileCandidate>();
        var lengthFits = new Dictionary<long, bool>();

        foreach (var (reason, artist, title) in sources)
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var stripped = MusicVideoTitleMatcher.Strip(title).Title;

            if (stripped.Length == 0)
            {
                continue;
            }

            foreach (var song in index.ForCredit(artist))
            {
                if (!ArtistEvidence.NamesAnyOf(artist, song.Artists))
                {
                    continue;
                }

                var similarity = MusicVideoTitleMatcher.Similarity(song.Title, stripped);

                if (similarity < DecisionEngine.TitleFloor)
                {
                    continue;
                }

                // The score is the title similarity alone. Length only orders songs that score alike (below): it is not
                // added to the score, so it can neither make a song a candidate nor lift one over the adopt line.
                var score = Math.Round(similarity, 4);
                var isLyric = MusicVideoTitleMatcher.IsNotAMusicVideo(song.Title, title);

                if (!best.TryGetValue(song.Id, out var known) || score > known.Score)
                {
                    best[song.Id] = new MusicVideoFileCandidate(
                        song.Id,
                        score,
                        isLyric
                            ? MusicVideoFileRules.LyricVideo
                            : string.Create(CultureInfo.InvariantCulture, $"{reason}; title {similarity:0.00}"));
                    lengthFits[song.Id] = MusicVideoFileRules.LengthFits(file.DurationMs, song.DurationMs);
                }
            }
        }

        return
        [
            .. best.Values
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => lengthFits.GetValueOrDefault(candidate.SongId))
                .ThenBy(candidate => candidate.SongId),
        ];
    }

    /// <summary>What to do with a file, given its candidates.</summary>
    private static Outcome Decide(MusicVideoFile file, List<MusicVideoFileCandidate> candidates, Dictionary<long, string> blocked)
    {
        if (candidates.Count == 0)
        {
            return new Outcome([], Unmatched: true, AdoptSongId: null);
        }

        var marked = candidates
            .Select(candidate => blocked.TryGetValue(candidate.SongId, out var why) ? candidate with { Reason = why } : candidate)
            .ToList();

        var top = marked[0];
        var next = marked.Count > 1 ? marked[1] : null;

        // The title alone has to reach the adopt line (length never lifts it there), and "within 0.05" includes 0.05.
        var confident = top.Score >= MusicVideoFileRules.AdoptScore
            && (next is null || top.Score - next.Score > MusicVideoFileRules.AdoptMargin + 1e-9);

        // A file that was never read (no container) is not trusted to be a video: it waits for a person. Neither is a
        // lyric video or a visualiser, which is somebody's picture over the audio.
        var adoptable = confident
            && !blocked.ContainsKey(top.SongId)
            && file.Container is not null
            && top.Reason != MusicVideoFileRules.LyricVideo;

        return new Outcome(marked, Unmatched: false, adoptable ? top.SongId : null);
    }

    /// <summary>The path under the video root, <c>/</c>-separated; the file's name alone when it is not under the root.</summary>
    private static string RelativePath(string path, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);

                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    return Path.DirectorySeparatorChar == '\\' ? relative.Replace('\\', '/') : relative;
                }
            }
            catch (ArgumentException)
            {
                // Fall through to the file name.
            }
        }

        return Path.GetFileName(path);
    }

    private sealed record Outcome(List<MusicVideoFileCandidate> Candidates, bool Unmatched, long? AdoptSongId);

    /// <summary>One song, reduced to what matching needs.</summary>
    private sealed record SongEntry(long Id, string Title, int? DurationMs, IReadOnlyList<string> Artists);

    /// <summary>
    /// The library's songs by artist name, so a file is compared with the few songs of its artist rather than with every
    /// song of the library. The key is a superset filter: <see cref="ArtistEvidence.NamesAnyOf"/> still decides.
    /// </summary>
    private sealed class SongIndex
    {
        private readonly Dictionary<string, List<SongEntry>> _byArtist;

        private SongIndex(Dictionary<string, List<SongEntry>> byArtist) => _byArtist = byArtist;

        public static async Task<SongIndex> BuildAsync(WondarrDbContext database, CancellationToken cancellationToken)
        {
            var songs = await database.Songs
                .AsNoTracking()
                .Select(song => new
                {
                    song.Id,
                    song.Title,
                    song.DurationMs,
                    FileDurationMs = song.File == null ? null : song.File.DurationMs,
                    PrimaryArtist = song.PrimaryArtist.Name,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var credits = (await database.SongArtists
                    .AsNoTracking()
                    .Select(credit => new { credit.SongId, credit.Artist.Name })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(credit => credit.SongId)
                .ToDictionary(group => group.Key, group => group.Select(credit => credit.Name).ToList());

            var byArtist = new Dictionary<string, List<SongEntry>>(StringComparer.Ordinal);

            foreach (var song in songs)
            {
                var names = credits.TryGetValue(song.Id, out var listed) ? [.. listed] : new List<string>();

                if (!string.IsNullOrWhiteSpace(song.PrimaryArtist) && !names.Contains(song.PrimaryArtist, StringComparer.Ordinal))
                {
                    names.Add(song.PrimaryArtist);
                }

                var entry = new SongEntry(song.Id, song.Title, song.DurationMs ?? song.FileDurationMs, names);

                foreach (var key in names.Select(Key).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    if (!byArtist.TryGetValue(key, out var list))
                    {
                        byArtist[key] = list = [];
                    }

                    list.Add(entry);
                }
            }

            return new SongIndex(byArtist);
        }

        /// <summary>The songs whose artist is named somewhere in the credit: every run of words of it is looked up.</summary>
        public List<SongEntry> ForCredit(string credit)
        {
            var words = TextMatching.Normalize(credit).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var found = new Dictionary<long, SongEntry>();

            for (var start = 0; start < words.Length; start++)
            {
                for (var length = 1; length <= MaxArtistWords && start + length <= words.Length; length++)
                {
                    var key = Key(string.Join(' ', words.Skip(start).Take(length)));

                    if (key.Length > 0 && _byArtist.TryGetValue(key, out var songs))
                    {
                        foreach (var song in songs)
                        {
                            found[song.Id] = song;
                        }
                    }
                }
            }

            return [.. found.Values];
        }

        /// <summary>An artist name for looking up, made the way <c>ArtistEvidence</c> makes its own key.</summary>
        private static string Key(string name)
        {
            var words = TextMatching.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (words.Count > 1 && words[0] == "the")
            {
                words.RemoveAt(0);
            }

            if (words.Count > 1 && words[^1] == "the")
            {
                words.RemoveAt(words.Count - 1);
            }

            return string.Join(' ', words.Where(word => word != "and"));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Adopted the video {Path} as the music video of song {SongId}")]
    private static partial void LogAdopted(ILogger logger, string path, long songId);
}
