using System.Globalization;
using System.Text;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Compaction;

/// <summary>Works out what the Compact library task would do, without doing any of it (LIBRARY_OUTPUT.md §7.3).</summary>
public interface ICompactPlanner
{
    /// <summary>Plans the compaction of one library: which songs would change album, and where they would go.</summary>
    /// <param name="libraryId">The library to plan.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The plan. Nothing is moved, tagged or written.</returns>
    /// <exception cref="KeyNotFoundException">The library does not exist.</exception>
    Task<CompactPlan> PlanAsync(long libraryId, CancellationToken cancellationToken);
}

/// <summary>
/// The dry run behind the Compact library task: it re-plans every song of a library under its album
/// policy as if none were placed yet, and reports the songs whose album would change. It reads the
/// database and renders paths; it writes nothing at all, so the same plan can be shown, discarded or
/// executed later.
/// </summary>
public sealed class CompactPlanner : ICompactPlanner
{
    private readonly WondarrDbContext _database;
    private readonly ISongService _songs;

    /// <summary>Initialises a new instance of the <see cref="CompactPlanner"/> class.</summary>
    /// <param name="database">The Wondarr database, read for the library and the songs' files.</param>
    /// <param name="songs">The song service, which owns the re-planning itself.</param>
    public CompactPlanner(WondarrDbContext database, ISongService songs)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(songs);

        _database = database;
        _songs = songs;
    }

    /// <inheritdoc />
    public async Task<CompactPlan> PlanAsync(long libraryId, CancellationToken cancellationToken)
    {
        var replanned = await _songs.ReplanLibraryAsync(libraryId, cancellationToken).ConfigureAwait(false);

        var library = await _database.Libraries
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == libraryId, cancellationToken)
            .ConfigureAwait(false);

        // Every song that has an album context counts towards the album totals, pinned ones included:
        // a pinned song stays exactly where it is, so its album is one of the ones that remain.
        var songs = await _database.Songs
            .AsNoTracking()
            .Include(song => song.PrimaryArtist)
            .Include(song => song.AlbumContext)
            .Include(song => song.File).ThenInclude(file => file!.Quality)
            .Where(song => song.LibraryId == libraryId && song.AlbumContext != null)
            .OrderBy(song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var proposed = new Dictionary<long, AlbumContext>(replanned.Count);
        foreach (var song in replanned)
        {
            proposed[song.SongId] = song.Proposed;
        }

        var before = new HashSet<string>(StringComparer.Ordinal);
        var after = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<long, Song>(songs.Count);

        foreach (var song in songs)
        {
            var current = song.AlbumContext!;

            byId[song.Id] = song;
            before.Add(current.AlbumKey);
            after.Add(proposed.TryGetValue(song.Id, out var to) ? to.AlbumKey : current.AlbumKey);
        }

        var moves = new List<CompactMove>();

        foreach (var song in replanned)
        {
            var stored = byId[song.SongId];
            var current = stored.AlbumContext!;

            // A song the engine left on its own album is not an album move, even when it was given
            // another track number: it stays exactly as it is, tags and album alike. (Its file may
            // still be in the wrong folder; the template pass below looks after that.)
            if (string.Equals(current.AlbumKey, song.Proposed.AlbumKey, StringComparison.Ordinal))
            {
                continue;
            }

            var (fromPath, toPath) = PathsFor(stored, song.Proposed, library);

            moves.Add(new CompactMove(
                song.SongId,
                stored.Title,
                stored.ArtistCredit,
                AlbumOf(current),
                AlbumOf(song.Proposed),
                fromPath,
                toPath,
                song.Proposed));
        }

        // The template pass: a song that stays on its album but whose file is not where the library's
        // naming template puts it now (the template changed after the file was filed) moves to the
        // rendered path, album unchanged. Pinned songs are included: pinning fixes the album, not the folder.
        var albumMoved = moves.Select(move => move.SongId).ToHashSet();
        var suppressed = new List<long>();
        var templateMoves = PlanTemplateMoves(songs, albumMoved, library, await LatestRowsAsync(libraryId, cancellationToken).ConfigureAwait(false), suppressed);
        var templateTotal = templateMoves.Count;

        moves.AddRange(CapByAlbum(templateMoves, TemplateMoveCap));

        moves.Sort(CompareMoves);

        return new CompactPlan(libraryId, before.Count, after.Count, replanned.Count, moves, templateTotal, suppressed);
    }

    /// <summary>
    /// The two paths a move would touch, or two <see langword="null"/>s when it would touch none. A
    /// reference file belongs to the user's own folder, not to the library, so it is never moved: the
    /// move only changes the song's album context.
    /// </summary>
    private static (string? FromPath, string? ToPath) PathsFor(Song song, AlbumContext proposed, Library library)
    {
        var file = song.File;

        if (file is null || string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var extension = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();

        // The probe's own measurements, rebuilt from the row: they are what the naming tokens read.
        var media = new MediaInfo(
            file.Codec,
            file.Container,
            file.BitrateKbps,
            file.SampleRate,
            file.BitDepth,
            file.Channels,
            file.DurationMs ?? 0,
            file.Quality.Lossless,
            file.Size);

        var relative = LibraryOrganizer.RelativePathFor(
            song,
            proposed,
            song.PrimaryArtist,
            media,
            file.Quality,
            file.SourceType,
            library,
            extension);

        // Joined exactly as the placer joins it, so the plan shows the path the file will really get.
        var target = Path.GetFullPath(Path.Combine(
            library.RootPath,
            relative.Replace('/', Path.DirectorySeparatorChar) + "." + extension));

        return (file.Path, target);
    }

    /// <summary>
    /// How many template-only moves one Compact run may make. A run re-tags and re-files every one of
    /// them and Plex scans the result, so a library whose template changed is brought over in steps;
    /// whole albums only, so an album's files are never split across two runs.
    /// </summary>
    public const int MaxTemplateMovesPerRun = 250;

    /// <summary>Gets how many template moves one run may plan; the constant, except where a test needs a small one.</summary>
    internal int TemplateMoveCap { get; init; } = MaxTemplateMovesPerRun;

    /// <summary>The latest compact_move row of each song, to tell a move that keeps failing from a new one.</summary>
    private async Task<Dictionary<long, CompactMoveRecord>> LatestRowsAsync(long libraryId, CancellationToken cancellationToken)
    {
        var rows = await _database.CompactMoves
            .AsNoTracking()
            .Where(row => row.LibraryId == libraryId)
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latest = new Dictionary<long, CompactMoveRecord>();

        foreach (var row in rows)
        {
            latest[row.SongId] = row;
        }

        return latest;
    }

    /// <summary>
    /// The template-only moves: every song not already moving to another album whose managed file is
    /// not where the library's template renders it. The path is rendered from the album context the
    /// executor will actually place (aligned with the album folder's first file, the way the organizer
    /// aligns it), and the songs are taken in id order with each aligned song's context carried forward,
    /// as the executor saves it, so a later sibling aligns against what the earlier one became.
    /// </summary>
    private static List<CompactMove> PlanTemplateMoves(
        List<Song> songs,
        HashSet<long> albumMoved,
        Library library,
        Dictionary<long, CompactMoveRecord> latestRows,
        List<long> suppressed)
    {
        var result = new List<CompactMove>();
        var contexts = songs.ToDictionary(song => song.Id, song => song.AlbumContext!);

        foreach (var stored in songs)
        {
            if (albumMoved.Contains(stored.Id))
            {
                continue;
            }

            var file = stored.File;

            // Only a managed file inside the library root is the template's business; a reference file
            // is the user's own, and a path outside the root is not Wondarr's to move.
            if (file is null
                || string.Equals(file.SourceType, SourceTypes.Reference, StringComparison.Ordinal)
                || !PathRules.IsStrictlyInside(library.RootPath, file.Path))
            {
                continue;
            }

            var aligned = Clone(contexts[stored.Id]);

            var folderFirst = songs
                .Where(other => other.Id != stored.Id
                    && other.File is { } otherFile
                    && !string.Equals(otherFile.SourceType, SourceTypes.Reference, StringComparison.Ordinal)
                    && string.Equals(contexts[other.Id].AlbumKey, aligned.AlbumKey, StringComparison.Ordinal))
                .OrderBy(other => contexts[other.Id].Id)
                .FirstOrDefault();

            if (folderFirst is not null)
            {
                AlbumFolderConsistency.Align(aligned, contexts[folderFirst.Id]);
            }

            var (from, to) = PathsFor(stored, aligned, library);

            if (from is null || to is null || IsInPlace(from, to))
            {
                continue;
            }

            // The target the planner offers must be one the placer will accept.
            if (!PathRules.IsStrictlyInside(library.RootPath, to))
            {
                continue;
            }

            // A move that failed for good, or keeps failing, to this same path is not tried again by
            // every run: the file would be staged and Plex scanned each time for nothing. It is tried
            // again once the template (and so the target) changes.
            if (latestRows.TryGetValue(stored.Id, out var row)
                && row.State is CompactMoveState.Failed or CompactMoveState.GivenUp
                && row.ToPath is not null
                && string.Equals(NormalizeForCompare(row.ToPath), NormalizeForCompare(to), StringComparison.OrdinalIgnoreCase))
            {
                suppressed.Add(row.Id);
                continue;
            }

            contexts[stored.Id] = aligned;

            result.Add(new CompactMove(
                stored.Id,
                stored.Title,
                stored.ArtistCredit,
                AlbumOf(stored.AlbumContext!),
                AlbumOf(aligned),
                from,
                to,
                aligned));
        }

        return result;
    }

    /// <summary>The moves of the first albums that fit the cap; an album is taken whole or not at all.</summary>
    private static IEnumerable<CompactMove> CapByAlbum(List<CompactMove> templateMoves, int cap)
    {
        var taken = 0;

        foreach (var album in templateMoves.GroupBy(move => move.From.AlbumKey, StringComparer.Ordinal))
        {
            var count = album.Count();

            // The first album always goes, even if it alone is over the cap: it cannot be split.
            if (taken > 0 && taken + count > cap)
            {
                continue;
            }

            taken += count;

            foreach (var move in album)
            {
                yield return move;
            }
        }
    }

    /// <summary>A copy of an album context with every field the naming template and the executor read.</summary>
    private static AlbumContext Clone(AlbumContext source) => new()
    {
        Id = source.Id,
        SongId = source.SongId,
        Kind = source.Kind,
        AlbumTitle = source.AlbumTitle,
        AlbumArtist = source.AlbumArtist,
        AlbumKey = source.AlbumKey,
        MbReleaseId = source.MbReleaseId,
        MbReleaseGroupId = source.MbReleaseGroupId,
        TrackNo = source.TrackNo,
        DiscNo = source.DiscNo,
        TotalTracks = source.TotalTracks,
        Date = source.Date,
        OriginalDate = source.OriginalDate,
        Label = source.Label,
        CoverUrl = source.CoverUrl,
        IsVariousArtists = source.IsVariousArtists,
        Sticky = source.Sticky,
        Pinned = source.Pinned,
    };

    /// <summary>
    /// The form two spellings of one path compare equal in: no trailing separator, combining marks
    /// dropped and accents folded. The app runs with <c>InvariantGlobalization</c>, where
    /// <see cref="string.Normalize(NormalizationForm)"/> does nothing for non-ASCII text, so NFC and NFD
    /// spellings are made equal by folding both (<see cref="TextFolding"/>) rather than normalising. A
    /// template change never alters an accent, so folding can only make this more lenient, never wrong.
    /// </summary>
    private static string NormalizeForCompare(string path)
    {
        var builder = new StringBuilder(path.Length);

        foreach (var character in path)
        {
            // U+0300 to U+036F: the combining diacritical marks an NFD name carries.
            if (character is < (char)0x0300 or > (char)0x036f)
            {
                builder.Append(character);
            }
        }

        return Path.TrimEndingDirectorySeparator(TextFolding.RemoveDiacritics(builder.ToString()));
    }

    /// <summary>
    /// Whether a file already is where the template puts it: at the rendered path, or at one of its
    /// <c>(2)</c>…<c>(99)</c> collision variants (the placer's names for a taken file name). Folder and
    /// file name are compared the way the file system compares them, so a second Compact right after
    /// the first plans nothing.
    /// </summary>
    internal static bool IsInPlace(string currentPath, string renderedPath)
    {
        // Case and Unicode form (and, with no ICU, accents) are never a move: a CIFS share or a Mac-written folder can hold the
        // same name in another case or in NFD, and renaming those would only churn Plex.
        var current = NormalizeForCompare(Path.GetFullPath(currentPath));
        var rendered = NormalizeForCompare(Path.GetFullPath(renderedPath));

        var currentFolder = Path.GetDirectoryName(current) ?? string.Empty;
        var renderedFolder = Path.GetDirectoryName(rendered) ?? string.Empty;

        if (!string.Equals(currentFolder, renderedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentName = Path.GetFileName(current);
        var extension = Path.GetExtension(rendered);
        var name = Path.GetFileNameWithoutExtension(rendered);

        if (string.Equals(currentName, name + extension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var suffix = 2; suffix <= FilePlacer.MaxCollisionSuffix; suffix++)
        {
            var variant = string.Concat(name, " (", suffix.ToString(CultureInfo.InvariantCulture), ")", extension);

            if (string.Equals(currentName, variant, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The plan's own ordering: from-album title, then to-album title, then song id.</summary>
    private static int CompareMoves(CompactMove left, CompactMove right)
    {
        var order = string.CompareOrdinal(left.From.AlbumTitle, right.From.AlbumTitle);

        if (order != 0)
        {
            return order;
        }

        order = string.CompareOrdinal(left.To.AlbumTitle, right.To.AlbumTitle);

        return order != 0 ? order : left.SongId.CompareTo(right.SongId);
    }

    private static CompactAlbum AlbumOf(AlbumContext context) =>
        new(context.AlbumKey, context.Kind, context.AlbumTitle, context.AlbumArtist);
}
