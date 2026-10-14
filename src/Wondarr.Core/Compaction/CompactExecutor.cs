using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Songs;

namespace Wondarr.Core.Compaction;

/// <summary>How one Compact library run ended.</summary>
/// <param name="Moved">Files that were moved into their new album folder.</param>
/// <param name="ContextOnly">Songs whose album changed without a file moving (a reference file, or none at all).</param>
/// <param name="Failed">Moves that could not be finished; their rows say why, and the next run retries them.</param>
/// <param name="Resumed">Moves an earlier run left unfinished and this run finished.</param>
/// <param name="GivenUp">Moves that kept failing or whose song is gone: this run stopped retrying them.</param>
/// <param name="Stranded">
/// The files a given-up move left where they are instead of putting them back, each as a path; the song
/// (if it still exists) points at them. <see langword="null"/> when there are none.
/// </param>
/// <param name="TrashNote">
/// Why Plex's trash was not emptied, or <see langword="null"/> when it was (or there was nothing to
/// forget).
/// </param>
public sealed record CompactResult(
    int Moved,
    int ContextOnly,
    int Failed,
    int Resumed,
    int GivenUp = 0,
    IReadOnlyList<string>? Stranded = null,
    string? TrashNote = null);

/// <summary>Applies a Compact library plan to the disk, the tags and Plex (LIBRARY_OUTPUT.md §7.3).</summary>
public interface ICompactExecutor
{
    /// <summary>Compacts one library: plans (or resumes) and applies every move of the plan.</summary>
    /// <param name="libraryId">The library to compact.</param>
    /// <param name="progress">Called with a one-line progress message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the run between steps.</param>
    /// <param name="resumeOnly">
    /// Only finish or give up the rows an earlier run left; never plan anew. The start-up path asks for
    /// this, so a restart can never apply a plan nobody confirmed.
    /// </param>
    /// <returns>How the run's moves ended.</returns>
    /// <exception cref="KeyNotFoundException">The library does not exist.</exception>
    /// <exception cref="InvalidOperationException">A compaction of this library is already running.</exception>
    Task<CompactResult> RunAsync(
        long libraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken,
        bool resumeOnly = false);
}

/// <summary>
/// The one implementation of <see cref="ICompactExecutor"/>.
/// </summary>
/// <remarks>
/// Plex never reconsiders a track's album membership on rescan (§7.2), so a file cannot simply be moved
/// to its new album folder: Plex has to forget it first. One run therefore does, per library: move every
/// affected file (with its lyrics sidecars) out to a hidden staging folder inside the root — a rename on
/// one file system — have Plex scan the old folders and empty its trash, re-tag and place each file
/// through the same organizer imports use, and let the debounced partial scan pick up the new folders.
/// Every step is written to the file's own <c>compact_move</c> row before the next one starts, so a
/// crash resumes instead of losing files, and nothing is ever deleted except a <c>cover.jpg</c> the
/// album folder was left holding (through the recycle bin) and the folders it left empty.
/// </remarks>
public sealed partial class CompactExecutor : ICompactExecutor
{
    /// <summary>The hidden folder under a library root where files wait while Plex forgets them.</summary>
    internal const string StagingFolderName = ".wondarr-compact";

    /// <summary>How many files there are between two progress messages.</summary>
    internal const int ProgressEvery = 25;

    /// <summary>The sidecar extensions that travel with their audio file.</summary>
    internal static readonly string[] SidecarExtensions = SongFileRules.SidecarExtensions;

    /// <summary>What counts as an audio file when deciding whether a folder is one we emptied.</summary>
    internal static readonly string[] AudioExtensions = SongFileRules.AudioExtensions;

    /// <summary>The album folder's art sidecar.</summary>
    internal const string CoverJpgName = SongFileRules.CoverJpgName;

    /// <summary>The Plex server preference that makes Plex empty its trash after every scan.</summary>
    internal const string AutoEmptyTrashSetting = "autoEmptyTrash";

    /// <summary>How often the section's <c>refreshing</c> flag is read while a scan runs.</summary>
    internal static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long a scan that has not started is waited for. Never seeing it start is not an error.</summary>
    internal static readonly TimeSpan ScanStartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a scan that has started is waited for before the run carries on regardless.</summary>
    internal static readonly TimeSpan ScanFinishTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// One gate per library, so two compactions of the same library can never interleave their moves.
    /// </summary>
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();

    /// <summary>How folder paths are compared, the same way <see cref="PathRules"/> compares them.</summary>
    private static readonly IEqualityComparer<string> PathComparer =
        PathRules.Comparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>The JSON shape of a proposed album context and of every payload this class writes.</summary>
    private static readonly JsonSerializerOptions ProposedJson = new(JsonSerializerDefaults.Web);

    /// <summary>The history payload's shape: camelCase, nulls left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WondarrDbContext _database;
    private readonly ICompactPlanner _planner;
    private readonly ILibraryOrganizer _organizer;
    private readonly IDiskOperations _disk;
    private readonly IRecycleBin _recycleBin;
    private readonly IPlexConnectionService _connection;
    private readonly IPlexServerClient _plex;
    private readonly IPlexLibraryUpdater _updater;
    private readonly TimeProvider _time;
    private readonly ISongFileLock _songFileLock;
    private readonly ILogger<CompactExecutor> _logger;

    /// <summary>Initialises a new instance of the <see cref="CompactExecutor"/> class.</summary>
    /// <param name="database">The Wondarr database, which holds the moves.</param>
    /// <param name="planner">The dry run, asked for a plan when there is nothing to resume.</param>
    /// <param name="organizer">Tags, names and places each file at its new path.</param>
    /// <param name="disk">Every file-system call, including the staging folder.</param>
    /// <param name="recycleBin">Where an album folder's leftover <c>cover.jpg</c> goes.</param>
    /// <param name="connection">The selected Plex server and its token; no token leaves it otherwise.</param>
    /// <param name="plex">The scans and the empty trash that make Plex forget a track.</param>
    /// <param name="updater">The debounced partial scan of the folders the files land in.</param>
    /// <param name="time">The clock every wait and poll is measured against.</param>
    /// <param name="songFileLock">The per-song lock the import's place-and-record step also takes.</param>
    /// <param name="logger">The log sink.</param>
    public CompactExecutor(
        WondarrDbContext database,
        ICompactPlanner planner,
        ILibraryOrganizer organizer,
        IDiskOperations disk,
        IRecycleBin recycleBin,
        IPlexConnectionService connection,
        IPlexServerClient plex,
        IPlexLibraryUpdater updater,
        TimeProvider time,
        ISongFileLock songFileLock,
        ILogger<CompactExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(recycleBin);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plex);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(songFileLock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _planner = planner;
        _organizer = organizer;
        _disk = disk;
        _recycleBin = recycleBin;
        _connection = connection;
        _plex = plex;
        _updater = updater;
        _time = time;
        _songFileLock = songFileLock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CompactResult> RunAsync(
        long libraryId,
        Func<string, Task>? progress,
        CancellationToken cancellationToken,
        bool resumeOnly = false)
    {
        var gate = Gates.GetOrAdd(libraryId, _ => new SemaphoreSlim(1, 1));

        // Waited for, not queued on: a second run of the same library is a mistake, not a request.
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A compaction of this library is already running");
        }

        try
        {
            return await RunLockedAsync(libraryId, progress, resumeOnly, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The run itself, with the library's gate held.</summary>
    private async Task<CompactResult> RunLockedAsync(
        long libraryId,
        Func<string, Task>? progress,
        bool resumeOnly,
        CancellationToken cancellationToken)
    {
        var library = await _database.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == libraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                string.Concat(
                    "Library ",
                    libraryId.ToString(CultureInfo.InvariantCulture),
                    " does not exist."));

        var carried = await _database.CompactMoves
            .AsNoTracking()
            .Where(row => row.LibraryId == libraryId && row.State != CompactMoveState.Placed)
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var moved = 0;
        var contextOnly = 0;
        var failed = 0;
        var resumed = 0;
        var givenUp = 0;
        var stranded = new List<string>();

        List<Move> work;

        // A row whose song was deleted under it cannot be placed, whatever is done to it.
        var carriedSongIds = carried.Select(row => row.SongId).Distinct().ToList();
        var existingSongIds = (await _database.Songs
                .AsNoTracking()
                .Where(song => carriedSongIds.Contains(song.Id))
                .Select(song => song.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();

        // Work left from an earlier run: a row not yet staged or placed, or a failed row whose file is
        // still in the staging folder — under the path the row names, or under the one it would have
        // named had the run not stopped between moving the file and recording that it had. A row that
        // cannot be finished is dealt with here, so it never holds the rest of the library up:
        //  - its song is gone: the staged file goes to the recycle bin;
        //  - it has failed MaxAttempts times: the file goes back where it came from, or stays where it
        //    is with the song pointing at it, and the log and the result name it.
        var pending = new List<Move>();

        // Rows the plan below must not delete: the ones this run closed (the user reads their messages
        // until the next run replaces them) and the ones that still hold a file.
        var kept = new HashSet<long>();

        // Songs this run gave up on; they are not planned again in the same run.
        var givenUpSongs = new HashSet<long>();

        foreach (var row in carried)
        {
            var parked = ParkedMoveOf(library, row);

            if (parked is null)
            {
                continue;
            }

            if (!existingSongIds.Contains(row.SongId))
            {
                // Closed or not, the row is kept: closed, for its message; not closed, because it still
                // names a file in staging.
                kept.Add(row.Id);

                if (await DropOrphanAsync(library, parked).ConfigureAwait(false))
                {
                    givenUp++;
                }
                else
                {
                    // The bin refused the file: the row keeps naming it, and the next run tries again.
                    failed++;
                }

                continue;
            }

            if (row.Attempts >= CompactMoveRules.MaxAttempts)
            {
                await GiveUpAsync(library, parked, row.Attempts, stranded).ConfigureAwait(false);
                kept.Add(row.Id);
                givenUpSongs.Add(row.SongId);
                givenUp++;

                continue;
            }

            pending.Add(parked);
        }

        if (pending.Count > 0)
        {
            // Resume: the last run's rows are finished first, and nothing is re-planned while a file
            // is still in the staging folder.
            work = pending;
            resumed = work.Count;

            LogResuming(_logger, libraryId, resumed);
        }
        else if (resumeOnly)
        {
            // A restart finishes or gives up what was left; it never plans anew.
            return new CompactResult(0, 0, failed, 0, givenUp, stranded.Count == 0 ? null : stranded);
        }
        else
        {
            // The plan is made first, while the last run's rows are still there: a template move that
            // failed for good to the very path the planner would offer again is left out of the plan,
            // and its row stays (below) so the next run leaves it out too, not stage it again.
            _database.ChangeTracker.Clear();

            var plan = await _planner.PlanAsync(libraryId, cancellationToken).ConfigureAwait(false);
            var suppressed = (plan.SuppressedRowIds ?? []).ToHashSet();

            // The rows of the last run are spent — placed, given up on, or failed with their file where
            // it was — so the plan replaces them. A failed row must not block every later compaction:
            // its message was logged when it failed, and its file never left its folder (or is recorded
            // at the path it was placed at).
            var spent = (await _database.CompactMoves
                    .Where(row => row.LibraryId == libraryId && !kept.Contains(row.Id))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                // A given-up row whose file still sits in the staging folder stays, so the health
                // check keeps warning about it until the file is dealt with.
                .Where(row => !IsStranded(row) && !suppressed.Contains(row.Id))
                .ToList();

            var dropped = spent.Count(row => row.State == CompactMoveState.Failed);

            if (dropped > 0)
            {
                LogFailedRowsDropped(_logger, libraryId, dropped);
            }

            _database.CompactMoves.RemoveRange(spent);
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _database.ChangeTracker.Clear();

            // A song this run has just given up on is planned again by the next run, not by this one.
            var planned = plan.Moves.Where(move => !givenUpSongs.Contains(move.SongId)).ToList();

            if (planned.Count == 0)
            {
                // Nothing to do: no new rows, no staging folder, no Plex traffic.
                return new CompactResult(0, 0, failed, 0, givenUp, stranded.Count == 0 ? null : stranded);
            }

            var recorded = planned
                .Select(move => new CompactMoveRecord
                {
                    LibraryId = libraryId,
                    SongId = move.SongId,
                    FromPath = move.FromPath,
                    ToPath = move.ToPath,
                    Proposed = SerializeProposed(move.Proposed),
                    State = CompactMoveState.Planned,
                })
                .ToList();

            // One save for the whole plan: from here every move is on disk as a row.
            _database.CompactMoves.AddRange(recorded);
            await _database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            work = [.. recorded.Select(Move.Of)];
            _database.ChangeTracker.Clear();
        }

        // --- 1. Move out ---------------------------------------------------------------------------
        var oldFolders = new List<string>();
        var ready = new List<Move>(work.Count);

        foreach (var row in work)
        {
            if (row.FromPath is null)
            {
                // A context-only change: there is no file to move out.
                ready.Add(row);

                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            oldFolders.Add(Path.GetDirectoryName(row.FromPath) ?? library.RootPath);

            if (row.State != CompactMoveState.Planned)
            {
                // An earlier run staged it already; it is waiting to go back.
                ready.Add(row);

                continue;
            }

            var staged = await StageAsync(library, row, cancellationToken).ConfigureAwait(false);

            if (staged is null)
            {
                // The row is Failed and its file is where it was: there is nothing to place.
                failed++;

                continue;
            }

            ready.Add(row with { State = CompactMoveState.Staged, StagedPath = staged });
        }

        work = ready;

        // A folder the move-out emptied holds nothing but its cover: the cover goes to the recycle bin
        // and the folder — then the artist folder it leaves empty — goes with it. Anything else in it
        // means the user keeps something there, and it is left alone.
        foreach (var folder in oldFolders.Distinct(PathComparer).ToList())
        {
            await CleanUpFolderAsync(library, folder, cancellationToken).ConfigureAwait(false);
        }

        // --- 2. Plex forgets -----------------------------------------------------------------------
        var trashNote = await ForgetInPlexAsync(library, oldFolders, cancellationToken).ConfigureAwait(false);

        // --- 3. Move back (and 4: the scan of the new folders) --------------------------------------
        var processed = 0;

        foreach (var row in work)
        {
            cancellationToken.ThrowIfCancellationRequested();

            processed++;

            if (row.FromPath is null)
            {
                if (await ApplyContextAsync(row, cancellationToken).ConfigureAwait(false))
                {
                    contextOnly++;
                }
                else
                {
                    failed++;
                }
            }
            else if (row.StagedPath is null || !_disk.FileExists(row.StagedPath))
            {
                // The staged file is not in staging. A run that was stopped between placing the file
                // and recording it left it at its new path (or at a "(n)" variant of it), so look
                // there before calling the move failed; nothing is deleted either way.
                var (recorded, reason) = await RecordPlacedFileAsync(library, row, cancellationToken)
                    .ConfigureAwait(false);

                if (recorded)
                {
                    moved++;
                }
                else
                {
                    await SetStateAsync(
                            row.Id,
                            CompactMoveState.Failed,
                            staged: null,
                            final: null,
                            message: reason ?? "the staged file is no longer in the staging folder")
                        .ConfigureAwait(false);

                    failed++;
                }
            }
            else if (await PlaceAsync(library, row, cancellationToken).ConfigureAwait(false))
            {
                moved++;
            }
            else
            {
                failed++;
            }

            if (progress is not null && processed % ProgressEvery == 0)
            {
                await progress(
                        string.Concat(
                            "Compacted ",
                            processed.ToString(CultureInfo.InvariantCulture),
                            " of ",
                            work.Count.ToString(CultureInfo.InvariantCulture),
                            " files"))
                    .ConfigureAwait(false);
            }
        }

        LogCompacted(_logger, libraryId, moved, contextOnly, failed, resumed);

        return new CompactResult(
            moved,
            contextOnly,
            failed,
            resumed,
            givenUp,
            stranded.Count == 0 ? null : stranded,
            trashNote);
    }

    /// <summary>
    /// Reads a carried row back as work to resume, or <see langword="null"/> when it is spent: placed,
    /// or failed with its file where it was.
    /// </summary>
    private Move? ParkedMoveOf(Library library, CompactMoveRecord row)
    {
        // A closed row is never resumed or given up on again.
        if (row.State == CompactMoveState.GivenUp)
        {
            return null;
        }

        if (row.State is CompactMoveState.Planned or CompactMoveState.Staged)
        {
            return Move.Of(row);
        }

        // A failed row whose file is still in the staging folder: under the path the row names, or
        // under the one it would have named had the run not stopped between moving the file and
        // recording that it had.
        var move = Move.Of(row);

        return StagedFileOf(library, move) is { } staged
            ? move with { State = CompactMoveState.Staged, StagedPath = staged }
            : null;
    }

    /// <summary>Where the row's file is parked in staging right now, or <see langword="null"/> when it is not there.</summary>
    private string? StagedFileOf(Library library, Move row)
    {
        if (row.StagedPath is not null && _disk.FileExists(row.StagedPath))
        {
            return row.StagedPath;
        }

        if (row.FromPath is not null)
        {
            var fallback = StagingPathFor(library, row.Id, row.FromPath);

            if (_disk.FileExists(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    /// <summary>Whether a given-up row's file is still in the staging folder.</summary>
    private bool IsStranded(CompactMoveRecord row) =>
        row.State == CompactMoveState.GivenUp
        && row.FinalPath is not null
        && row.FinalPath.Contains(StagingFolderName, StringComparison.Ordinal)
        && _disk.FileExists(row.FinalPath);

    /// <summary>
    /// A move whose song was deleted: its staged file (and the lyrics beside it) goes to the recycle bin
    /// and the row is closed with a reason that names the file. The song cannot be re-filed, and nothing
    /// else would ever pick the file up.
    /// </summary>
    /// <returns><see langword="false"/> when the recycle bin refused the file, which stays where it is.</returns>
    private async Task<bool> DropOrphanAsync(Library library, Move row)
    {
        await using var guard = await _songFileLock.AcquireAsync(row.SongId, CancellationToken.None).ConfigureAwait(false);

        var staged = StagedFileOf(library, row);

        if (staged is null)
        {
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: "the song no longer exists, and its file is not in the staging folder")
                .ConfigureAwait(false);

            return true;
        }

        try
        {
            // CancellationToken.None: a file half-recycled is worse than one finished late.
            foreach (var sidecar in SidecarsOf(staged).ToList())
            {
                await _recycleBin.RecycleAsync(sidecar, library.RootPath, CancellationToken.None).ConfigureAwait(false);
            }

            await _recycleBin.RecycleAsync(staged, library.RootPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOrphanNotRecycled(_logger, row.Id, staged, exception.Message);

            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged,
                    final: null,
                    message: string.Concat(
                        "the song no longer exists, and the recycle bin refused ",
                        staged,
                        ": ",
                        exception.Message))
                .ConfigureAwait(false);

            return false;
        }

        DeleteStagingDirectory(staged);

        LogOrphanRecycled(_logger, row.Id, staged);

        await SetStateAsync(
                row.Id,
                CompactMoveState.Failed,
                staged: null,
                final: null,
                message: string.Concat("the song no longer exists; ", staged, " was moved to the recycle bin"))
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Stops retrying a move that has failed <see cref="CompactMoveRules.MaxAttempts"/> times. The file
    /// goes back to its <c>FromPath</c> where it can; otherwise it stays where it is. Either way the
    /// song's file row is made to say where the file really is, the row is closed so planning goes
    /// ahead without it, and the log (and, for a file that stayed, the run's result) names the file.
    /// </summary>
    private async Task GiveUpAsync(Library library, Move row, int attempts, List<string> stranded)
    {
        // The per-song file guard: an import cannot replace the song's file between the check of where
        // it points and the update below.
        await using var guard = await _songFileLock.AcquireAsync(row.SongId, CancellationToken.None).ConfigureAwait(false);

        var staged = StagedFileOf(library, row);
        var from = row.FromPath;
        var restored = false;

        // Only a song that still points at this move's file (its old path or its staged path) is touched.
        // One that points elsewhere has moved on (an import replaced its file): the parked file is left
        // alone and named, and the song's file row is not changed.
        var current = await _database.SongFiles
            .AsNoTracking()
            .Where(file => file.SongId == row.SongId)
            .Select(file => file.Path)
            .FirstOrDefaultAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var ours = current is null
            || (from is not null && PathRules.AreEqual(current, from))
            || (staged is not null && PathRules.AreEqual(current, staged));

        if (ours && staged is not null && from is not null && !_disk.FileExists(from))
        {
            try
            {
                var directory = Path.GetDirectoryName(from);

                if (!string.IsNullOrEmpty(directory))
                {
                    _disk.CreateDirectory(directory);
                }

                _disk.MoveFile(staged, from);
                restored = true;

                MoveSidecarsBack(staged, from);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRestoreFailed(_logger, row.Id, staged, from, exception.Message);
            }
        }

        var at = restored ? from : staged;
        string message;

        if (at is null)
        {
            message = string.Concat("gave up after ", attempts.ToString(CultureInfo.InvariantCulture), " attempts: the file is not in the staging folder");
        }
        else
        {
            if (ours && current is not null && !PathRules.AreEqual(current, at))
            {
                // The song's file row says where the file is, wherever that turned out to be.
                await _database.SongFiles
                    .Where(file => file.SongId == row.SongId)
                    .ExecuteUpdateAsync(update => update.SetProperty(file => file.Path, at), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (restored)
            {
                message = string.Concat(
                    "gave up after ",
                    attempts.ToString(CultureInfo.InvariantCulture),
                    " attempts: the file is back at ",
                    at);

                DeleteStagingDirectory(staged!);
                _updater.RequestFolder(library.Id, Path.GetDirectoryName(at) ?? library.RootPath);
            }
            else
            {
                message = string.Concat(
                    "gave up after ",
                    attempts.ToString(CultureInfo.InvariantCulture),
                    ours
                        ? " attempts: the file could not be moved back and is left at "
                        : " attempts: the song has a different file now, so the parked file is left at ",
                    at);

                stranded.Add(at);
            }
        }

        LogGaveUp(_logger, row.Id, message);

        await SetStateAsync(row.Id, CompactMoveState.GivenUp, staged: null, final: at, message).ConfigureAwait(false);
    }

    /// <summary>Puts the sidecars of a staged file back beside the audio file's old path; one that cannot go stays in staging.</summary>
    private void MoveSidecarsBack(string staged, string from)
    {
        foreach (var (sidecar, target, targetTaken) in SongFileRules.SidecarMoves(_disk, staged, from))
        {
            if (targetTaken)
            {
                LogSidecarLeftBehind(_logger, sidecar, target);

                continue;
            }

            try
            {
                _disk.MoveFile(sidecar, target);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSidecarNotPlaced(_logger, sidecar, exception.Message);
            }
        }
    }

    /// <summary>
    /// A Staged row whose file is no longer in staging may have been placed by a run that stopped before
    /// it could record that. This looks for the file at the row's <c>ToPath</c> and its <c>(n)</c>
    /// collision variants. A candidate counts only when no other song owns it and its size is close to
    /// the song file's recorded size (tags and a cover change it a little); exactly one such candidate is
    /// recorded as placed. None, or more than one, fails the row and names the candidates: it never picks
    /// blindly.
    /// </summary>
    /// <returns>Whether the file was found and recorded, and the reason when not.</returns>
    private async Task<(bool Recorded, string? Message)> RecordPlacedFileAsync(
        Library library,
        Move row,
        CancellationToken cancellationToken)
    {
        if (row.ToPath is null)
        {
            return (false, null);
        }

        var song = await LoadSongAsync(row.SongId, cancellationToken).ConfigureAwait(false);

        if (song?.AlbumContext is not { } album || song.File is not { } file)
        {
            _database.ChangeTracker.Clear();

            return (false, null);
        }

        var (found, message) = await FindPlacedFileAsync(row, file.Size, cancellationToken).ConfigureAwait(false);

        if (found is null)
        {
            _database.ChangeTracker.Clear();

            return (false, message);
        }

        try
        {
            // The context the run was about to write: unless the user pinned another album since.
            if (!album.Pinned)
            {
                Apply(album, row.Proposed);
            }

            file.Path = found;
            file.Size = _disk.GetFileSize(found);

            _database.History.Add(new HistoryItem
            {
                SongId = song.Id,
                EventType = HistoryEventType.Renamed,
                Data = JsonSerializer.Serialize(new CompactHistoryData(found, row.FromPath), Json),
            });

            await SetStateAsync(row.Id, CompactMoveState.Placed, staged: null, found, message: null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            _database.ChangeTracker.Clear();

            LogRecordFailed(_logger, row.Id, exception);

            return (false, null);
        }

        LogFoundPlaced(_logger, row.Id, found);
        _updater.RequestFolder(library.Id, Path.GetDirectoryName(found) ?? library.RootPath);

        return (true, null);
    }

    /// <summary>
    /// The one of the row's <c>ToPath</c> and its <c>(2)</c>…<c>(99)</c> variants that exists, belongs to
    /// no other song's file, and has about the size the song's file was recorded with.
    /// </summary>
    private async Task<(string? Found, string? Message)> FindPlacedFileAsync(
        Move row,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var target = row.ToPath!;
        var directory = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        var candidates = new List<string>(99) { target };

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            candidates.Add(Path.Combine(directory, string.Concat(name, " (", suffix.ToString(CultureInfo.InvariantCulture), ")", extension)));
        }

        var existing = new List<string>();
        var matching = new List<string>();

        // Tags and a cover make the file a little bigger or smaller than it was recorded: 5 %, at least 1.5 MiB.
        var tolerance = Math.Max(expectedSize / 20, 1_572_864);

        foreach (var candidate in candidates)
        {
            if (!_disk.FileExists(candidate))
            {
                continue;
            }

            // A file another song's row names is that song's, not this one's.
            var owned = await _database.SongFiles
                .AsNoTracking()
                .AnyAsync(file => file.Path == candidate && file.SongId != row.SongId, cancellationToken)
                .ConfigureAwait(false);

            if (owned)
            {
                continue;
            }

            existing.Add(candidate);

            if (Math.Abs(_disk.GetFileSize(candidate) - expectedSize) <= tolerance)
            {
                matching.Add(candidate);
            }
        }

        if (matching.Count == 1)
        {
            return (matching[0], null);
        }

        if (existing.Count == 0)
        {
            return (null, null);
        }

        return (
            null,
            string.Concat(
                matching.Count == 0
                    ? "the staged file is gone, and none of these files at its target looks like it: "
                    : "the staged file is gone, and more than one file at its target looks like it: ",
                string.Join("; ", existing)));
    }

    /// <summary>
    /// Moves one file, and the sidecars beside it, into the library's staging folder. The row is
    /// <see cref="CompactMoveState.Staged"/> before the next file is touched.
    /// </summary>
    /// <param name="library">The library the file came out of.</param>
    /// <param name="row">The move being made.</param>
    /// <param name="cancellationToken">Cancels between steps (never between a file and its sidecars).</param>
    /// <returns>Where the file is parked, or <see langword="null"/> when the move failed and the file stayed where it was.</returns>
    private async Task<string?> StageAsync(Library library, Move row, CancellationToken cancellationToken)
    {
        // The per-song file guard: the import's transcode-to-record span holds the same lock, so a
        // compaction stage and an import can never act on the same song's file at once. The
        // library gate is taken before this lock and released after it — never the other way round.
        await using var _ = await _songFileLock
            .AcquireAsync(row.SongId, cancellationToken)
            .ConfigureAwait(false);

        var from = row.FromPath!;
        var staged = StagingPathFor(library, row.Id, from);
        var directory = Path.GetDirectoryName(staged)!;

        // The plan's file must still be the song's file: an import may have replaced it since the
        // plan was made, and one that is importing right now will. A row whose song moved
        // underneath it is failed and nothing is moved — the file is never touched.
        var currentPath = await _database.SongFiles
            .AsNoTracking()
            .Where(file => file.SongId == row.SongId)
            .Select(file => file.Path)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (currentPath is null || !PathRules.AreEqual(currentPath, from))
        {
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: "the song's file changed since the plan")
                .ConfigureAwait(false);

            return null;
        }

        if (await _database.QueueItems
                .AsNoTracking()
                .AnyAsync(
                    item => item.SongId == row.SongId && item.State == QueueItemState.Importing,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: "the song is being imported")
                .ConfigureAwait(false);

            return null;
        }

        if (!_disk.FileExists(from) && _disk.FileExists(staged))
        {
            // An earlier run moved the file and stopped before it could record that: the file is
            // already parked, so it is adopted where it is, with whatever sidecars are still behind.
            foreach (var sidecar in SidecarsOf(from))
            {
                try
                {
                    _disk.MoveFile(sidecar, Path.Combine(directory, Path.GetFileName(sidecar)));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogSidecarNotStaged(_logger, sidecar, exception.Message);
                }
            }

            await SetStateAsync(row.Id, CompactMoveState.Staged, staged, final: null, message: null)
                .ConfigureAwait(false);

            return staged;
        }

        if (!_disk.FileExists(from))
        {
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: "the file was not found")
                .ConfigureAwait(false);

            return null;
        }

        try
        {
            _disk.CreateDirectory(directory);

            // Inside the library root, so this is a rename on one file system and cannot half-finish.
            _disk.MoveFile(from, staged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStagingFailed(_logger, row.Id, from, exception.Message);

            // The audio file is the first thing moved, so nothing has moved: it stays where it was.
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: exception.Message)
                .ConfigureAwait(false);

            return null;
        }

        // The sidecars travel with their file. One that cannot be moved is left where it is and named
        // in the log; it is never deleted, and it never stops the audio file going back.
        foreach (var sidecar in SidecarsOf(from))
        {
            try
            {
                _disk.MoveFile(sidecar, Path.Combine(directory, Path.GetFileName(sidecar)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSidecarNotStaged(_logger, sidecar, exception.Message);
            }
        }

        await SetStateAsync(row.Id, CompactMoveState.Staged, staged, final: null, message: null)
            .ConfigureAwait(false);

        return staged;
    }

    /// <summary>
    /// Recycles the <c>cover.jpg</c> of an album folder the move-out emptied, then the folder, then the
    /// artist folder it leaves empty. A folder holding anything else — another file, a folder, a cover
    /// and something beside it — is left exactly as it is.
    /// </summary>
    private async Task CleanUpFolderAsync(Library library, string folder, CancellationToken cancellationToken)
    {
        if (!_disk.DirectoryExists(folder) || !PathRules.IsStrictlyInside(library.RootPath, folder))
        {
            return;
        }

        var files = _disk.EnumerateFiles(folder).ToList();

        // An audio file the move did not take with it, or anything beside the art, means this is not
        // a folder we emptied.
        if (!SongFileRules.HoldsOnlyCovers(_disk, folder, out var covers))
        {
            return;
        }

        try
        {
            foreach (var cover in covers)
            {
                await _recycleBin.RecycleAsync(cover, library.RootPath, cancellationToken).ConfigureAwait(false);
            }

            _disk.DeleteEmptyDirectory(folder);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The cover could not be parked (an unreadable folder, a recycle bin that refuses it), so
            // the folder keeps it: nothing is ever deleted without the recycle bin taking it first, and
            // a tidy-up that fails never stops the staged files going back.
            LogFolderNotCleaned(_logger, folder, exception.Message);

            return;
        }

        var parent = Path.GetDirectoryName(folder);

        if (parent is not null
            && !PathRules.AreEqual(parent, library.RootPath)
            && PathRules.IsStrictlyInside(library.RootPath, parent))
        {
            try
            {
                _disk.DeleteEmptyDirectory(parent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFolderNotCleaned(_logger, parent, exception.Message);
            }
        }
    }

    /// <summary>
    /// Step 2: makes Plex forget the tracks that left the library. A partial scan of each old folder
    /// puts them in Plex's trash; emptying the trash is what stops Plex reconsidering them on the scan
    /// that follows. Nothing here can stop the files going back: a failure is a warning and a later
    /// "empty trash" in Plex fixes it by hand. The trash is emptied only when that is safe (see
    /// <see cref="WhyNotEmptyTrashAsync"/>).
    /// </summary>
    /// <returns>Why the trash was not emptied, or <see langword="null"/> when it was or nothing was asked of Plex.</returns>
    private async Task<string?> ForgetInPlexAsync(
        Library library,
        List<string> oldFolders,
        CancellationToken cancellationToken)
    {
        var section = library.PlexSectionId?.Trim();

        if (string.IsNullOrEmpty(section))
        {
            LogNotLinked(_logger, library.Id);

            return null;
        }

        if (oldFolders.Count == 0)
        {
            return null;
        }

        (Uri Server, string Token)? selected;

        try
        {
            selected = await _connection.GetServerContextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPlexFailed(_logger, library.Id, exception.Message);

            return null;
        }

        if (selected is not { } server)
        {
            LogNoServer(_logger, library.Id);

            return null;
        }

        try
        {
            // The same politeness rule the import's partial scans use: past a couple of dozen folders,
            // one scan of the root costs less than dozens of requests.
            var folders = oldFolders.Distinct(PathComparer).ToList();

            if (folders.Count > PlexLibraryUpdater.MaxFoldersPerLibrary)
            {
                LogTooManyFolders(_logger, library.Name, folders.Count);

                folders = [library.RootPath];
            }

            foreach (var folder in folders)
            {
                await _plex
                    .RefreshPathAsync(
                        server.Server,
                        server.Token,
                        section,
                        PlexPathMapper.ToServerPath(library, folder),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var outcome = await WaitForScanAsync(server.Server, server.Token, section, cancellationToken)
                .ConfigureAwait(false);

            var reason = outcome switch
            {
                ScanOutcome.Finished => await WhyNotEmptyTrashAsync(
                        library,
                        server.Server,
                        server.Token,
                        section,
                        cancellationToken)
                    .ConfigureAwait(false),
                ScanOutcome.StillRunning => string.Concat(
                    "the scan of the old folders was not seen to finish within ",
                    ScanFinishTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture),
                    " minutes"),
                _ => "Plex was never seen scanning the old folders, so it cannot be told that the scan finished",
            };

            if (reason is not null)
            {
                LogTrashNotEmptied(_logger, library.Id, reason);

                return TrashNoteFor(reason);
            }

            await _plex
                .EmptyTrashAsync(server.Server, server.Token, section, cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPlexFailed(_logger, library.Id, exception.Message);

            return null;
        }
    }

    /// <summary>
    /// Waits out the section's scan: first for the scan to be seen to start — a scan small enough to
    /// finish between two polls never shows up, and that is fine — then for it to finish.
    /// </summary>
    /// <returns>Whether the scan was seen to finish, was still running at the end, or was never seen at all.</returns>
    private async Task<ScanOutcome> WaitForScanAsync(
        Uri server,
        string token,
        string section,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + ScanStartTimeout;
        var seen = false;

        while (_time.GetUtcNow() < deadline)
        {
            if (await _plex.IsRefreshingAsync(server, token, section, cancellationToken).ConfigureAwait(false))
            {
                seen = true;

                break;
            }

            await Task.Delay(ScanPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        deadline = _time.GetUtcNow() + ScanFinishTimeout;

        while (_time.GetUtcNow() < deadline)
        {
            if (!await _plex.IsRefreshingAsync(server, token, section, cancellationToken).ConfigureAwait(false))
            {
                // A scan that never showed up may have finished between two polls, or never started.
                return seen ? ScanOutcome.Finished : ScanOutcome.NeverSeen;
            }

            await Task.Delay(ScanPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        // The scan is still running. Emptying the trash now could purge items a half-done scan has not
        // reconsidered, so the caller leaves the trash alone; the user can empty it in Plex later.
        LogScanNotFinished(_logger, section, ScanFinishTimeout.TotalMinutes);

        return ScanOutcome.StillRunning;
    }

    /// <summary>How the wait for the section's scan ended.</summary>
    private enum ScanOutcome
    {
        /// <summary>The scan was seen running and then seen finished.</summary>
        Finished,

        /// <summary>The scan was still running when the wait ran out.</summary>
        StillRunning,

        /// <summary>The scan was never seen running, so nothing says it ever finished.</summary>
        NeverSeen,
    }

    /// <summary>The run-result wording of a reason the trash was left alone.</summary>
    private static string TrashNoteFor(string reason) =>
        string.Concat(
            "Plex's trash was not emptied because ",
            reason,
            "; moved songs show as unavailable in Plex until you empty its trash");

    /// <summary>
    /// Decides whether it is safe to empty the section's trash, and says why not when it is not. Not safe:
    /// Plex's <c>autoEmptyTrash</c> preference is on (it empties the trash itself), or a folder of the
    /// section cannot be confirmed present. Plex's handling of an unreachable folder is inconsistent (a
    /// slow share has had its items trashed) and an emptied trash is gone for good, so every
    /// <c>Location</c> must map to a Wondarr library or reference-library root that exists and holds at
    /// least one file (an unmounted mount point exists but is empty). An unreadable or missing
    /// <c>autoEmptyTrash</c> counts as unknown, which does not stop the emptying.
    /// </summary>
    /// <returns>The reason, or <see langword="null"/> when the trash may be emptied.</returns>
    private async Task<string?> WhyNotEmptyTrashAsync(
        Library library,
        Uri server,
        string token,
        string section,
        CancellationToken cancellationToken)
    {
        string? preference = null;

        try
        {
            preference = await _plex
                .GetPreferenceAsync(server, token, AutoEmptyTrashSetting, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Unknown, which is not "on": the folders are still checked.
            LogPreferenceUnreadable(_logger, AutoEmptyTrashSetting, exception.Message);
        }

        LogPreferenceValue(_logger, AutoEmptyTrashSetting, preference ?? "(not listed)");

        if (preference is not null
            && (string.Equals(preference.Trim(), "1", StringComparison.Ordinal)
                || string.Equals(preference.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
        {
            return "Plex empties its trash itself after every scan (autoEmptyTrash is on)";
        }

        IReadOnlyList<PlexSection> sections;

        try
        {
            sections = await _plex.GetSectionsAsync(server, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Concat("the section's folders could not be read from Plex (", exception.Message, ")");
        }

        var plexSection = sections.FirstOrDefault(
            candidate => string.Equals(candidate.Key, section, StringComparison.Ordinal));

        if (plexSection is null || plexSection.Locations.Count == 0)
        {
            return "Plex lists no folders for the section, so none can be confirmed present";
        }

        var libraries = await _database.Libraries
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var references = await _database.ReferenceLibraries
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var location in plexSection.Locations)
        {
            var local = LocalFolderOf(location, library, libraries, references);

            if (local is null)
            {
                return string.Concat(
                    "the section's folder ",
                    location,
                    " is not under any library or reference library root Wondarr knows, so it cannot be confirmed present");
            }

            // An unmounted mount point exists but is empty, so existing is not enough. A folder that
            // cannot even be read cannot be confirmed either.
            bool present;

            try
            {
                present = _disk.DirectoryExists(local) && _disk.EnumerateFiles(local).Any();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return string.Concat(
                    "the section's folder ",
                    location,
                    " (",
                    local,
                    ") could not be read (",
                    exception.Message,
                    ")");
            }

            if (!present)
            {
                return string.Concat(
                    "the section's folder ",
                    location,
                    " (",
                    local,
                    ") is missing or empty, which looks like an unmounted share");
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a folder Plex reports back to the path Wondarr sees it at: through each library's Plex root
    /// (the linked library first), then as a reference-library root or a folder inside one.
    /// </summary>
    /// <returns>The local folder, or <see langword="null"/> when it maps to no root Wondarr knows.</returns>
    private static string? LocalFolderOf(
        string plexFolder,
        Library linked,
        List<Library> libraries,
        List<ReferenceLibrary> references)
    {
        if (string.IsNullOrWhiteSpace(plexFolder))
        {
            return null;
        }

        foreach (var candidate in libraries.OrderBy(entry => entry.Id == linked.Id ? 0 : 1).ThenBy(entry => entry.Id))
        {
            if (PlexPathMapper.ToLocalPath(candidate, plexFolder) is { } local)
            {
                return local;
            }
        }

        foreach (var reference in references)
        {
            if (!string.IsNullOrWhiteSpace(reference.RootPath) && PathRules.IsInside(reference.RootPath, plexFolder))
            {
                return plexFolder;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies one album change that touches no file: the song's album context becomes the proposed one,
    /// and the row is done. <see cref="AlbumContext.Pinned"/> is deliberately left alone.
    /// </summary>
    private async Task<bool> ApplyContextAsync(Move row, CancellationToken cancellationToken)
    {
        var song = await _database.Songs
            .Include(candidate => candidate.AlbumContext)
            .FirstOrDefaultAsync(candidate => candidate.Id == row.SongId, cancellationToken)
            .ConfigureAwait(false);

        if (song?.AlbumContext is not { } album)
        {
            _database.ChangeTracker.Clear();

            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    final: null,
                    message: "the song has no album context")
                .ConfigureAwait(false);

            return false;
        }

        try
        {
            // A choice the user made after the plan stays; the row is done either way.
            if (album.Pinned)
            {
                LogPinnedKept(_logger, row.Id);
            }
            else
            {
                Apply(album, row.Proposed);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            _database.ChangeTracker.Clear();

            await SetStateAsync(row.Id, CompactMoveState.Failed, staged: null, final: null, exception.Message)
                .ConfigureAwait(false);

            return false;
        }

        await SetStateAsync(row.Id, CompactMoveState.Placed, staged: null, final: null, message: null)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Step 3: re-tags the staged file for its new album and places it at its new path through the same
    /// organizer imports use, then asks for the partial scan of the folder it landed in.
    /// </summary>
    /// <param name="library">The library the file goes back into.</param>
    /// <param name="row">The move being finished.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> when the file is at its new path and recorded there.</returns>
    private async Task<bool> PlaceAsync(Library library, Move row, CancellationToken cancellationToken)
    {
        // No song lock here, on purpose: while this row is Staged, an import for the song defers
        // (its guard sees the unfinished move), so nothing but this executor can act on the song's
        // file, and the lock would only be held across the Plex wait for nothing.
        var staged = row.StagedPath!;

        // The song is loaded exactly as the import loads it: the album context decides the folder and
        // the tags, and the credits are what the tag writer writes.
        var song = await LoadSongAsync(row.SongId, cancellationToken).ConfigureAwait(false);

        if (song?.AlbumContext is not { } album || song.File is not { } file)
        {
            _database.ChangeTracker.Clear();

            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged,
                    final: null,
                    message: "the song has no album context or no file")
                .ConfigureAwait(false);

            return false;
        }

        try
        {
            // An album the user chose by hand after the plan was made stays: the file is filed and
            // tagged under the user's album, and the plan's is not applied over it.
            if (album.Pinned)
            {
                LogPinnedKept(_logger, row.Id);
            }
            else
            {
                Apply(album, row.Proposed);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // The file stays in staging, where the row says it is.
            _database.ChangeTracker.Clear();

            await SetStateAsync(row.Id, CompactMoveState.Failed, staged, final: null, exception.Message)
                .ConfigureAwait(false);

            return false;
        }

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

        var extension = Path.GetExtension(staged).TrimStart('.').ToLowerInvariant();

        var credits = song.Artists
            .OrderBy(credit => credit.Position)
            .Select(credit => (credit.Artist, credit.Role))
            .ToList();

        OrganizeResult placement;

        try
        {
            placement = await _organizer
                .OrganizeAsync(
                    new OrganizeRequest(
                        song,
                        album,
                        credits,
                        library,
                        staged,
                        extension,
                        media,
                        file.Quality,
                        file.SourceType,
                        file.AcoustId,
                        KeepSource: false,
                        ReplacesPath: null,

                        // The file carries whatever lyrics the import gave it, and its sidecar is coming
                        // with it: asking LRCLIB again could only overwrite the user's own.
                        LookUpLyrics: false,
                        ReplayGainDb: file.ReplayGainDb,
                        ReplayGainPeak: file.ReplayGainPeak),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The organizer aligns the tracked album context before it does anything that can fail, and
            // that correction must not be saved with a failed file (see its doc comment).
            LogPlacementFailed(_logger, row.Id, exception);

            _database.ChangeTracker.Clear();

            await SetStateAsync(row.Id, CompactMoveState.Failed, staged, final: null, message: exception.Message)
                .ConfigureAwait(false);

            return false;
        }

        // A result that is not a success can still have put the file at FinalPath: a chmod that failed
        // after the move, or a cross-device copy whose source could not be deleted. A file that is
        // there, and is not the staged one, is placed, and the song must be recorded at it.
        var finalPath = placement.FinalPath;
        var placedDespiteError = !placement.Success
            && finalPath is not null
            && !PathRules.AreEqual(finalPath, staged)
            && _disk.FileExists(finalPath);

        if (finalPath is null || (!placement.Success && !placedDespiteError))
        {
            _database.ChangeTracker.Clear();

            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    _disk.FileExists(staged) ? staged : null,
                    final: null,
                    message: placement.Error ?? "the organizer gave no final path")
                .ConfigureAwait(false);

            return false;
        }

        if (placedDespiteError)
        {
            LogPlacedDespiteError(_logger, row.Id, finalPath, placement.Error ?? "no reason given");
        }

        var fileId = file.Id;

        try
        {
            MoveSidecars(staged, finalPath);

            file.Path = finalPath;
            file.Size = _disk.GetFileSize(finalPath);
            file.TagsWritten = JsonSerializer.Serialize(placement.TagsWritten, Json);

            // A library without ReplayGain holds no such tags after the writer's replace; the row
            // must not keep values the file no longer carries.
            if (!library.ReplayGain)
            {
                file.ReplayGainDb = null;
                file.ReplayGainPeak = null;
            }

            // Renamed is the event type for a file that moved: the song is the same song, and what the
            // user is told is where it went and where it came from.
            _database.History.Add(new HistoryItem
            {
                SongId = song.Id,
                EventType = HistoryEventType.Renamed,
                Data = JsonSerializer.Serialize(new CompactHistoryData(finalPath, row.FromPath), Json),
            });

            // One save for the file row, the album context, the history and the move. The file is at its
            // new path already, so a crash before this save leaves the row Staged with nothing in
            // staging: the next run looks for the file at the row's ToPath (and its "(n)" variants)
            // and records it there before it calls the move failed. The save is never cancelled.
            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Placed,
                    staged: null,
                    finalPath,
                    message: placedDespiteError
                        ? string.Concat("placed, but the organizer reported: ", placement.Error)
                        : null)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The file is in the library already; only the bookkeeping failed. The row says so, so the
            // next run leaves it alone rather than filing it twice.
            LogRecordFailed(_logger, row.Id, exception);

            _database.ChangeTracker.Clear();

            // The file is at its new path; the least the database must say is where. The album and
            // the history are left for the user (the row's message names the path).
            try
            {
                await _database.SongFiles
                    .Where(candidate => candidate.Id == fileId)
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(candidate => candidate.Path, finalPath),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception repair) when (repair is not OperationCanceledException)
            {
                LogRecordFailed(_logger, row.Id, repair);
            }

            await SetStateAsync(
                    row.Id,
                    CompactMoveState.Failed,
                    staged: null,
                    finalPath,
                    string.Concat("placed at ", finalPath, " but recording it failed: ", exception.Message))
                .ConfigureAwait(false);

            return false;
        }

        // Step 4: the debounced partial scan, exactly as an import asks for it.
        _updater.RequestFolder(library.Id, Path.GetDirectoryName(finalPath) ?? library.RootPath);

        DeleteStagingDirectory(staged);

        return true;
    }

    /// <summary>Loads the song with everything filing it needs, as <c>ImportService</c> loads it.</summary>
    private async Task<Song?> LoadSongAsync(long songId, CancellationToken cancellationToken) =>
        await _database.Songs
            .IncludeFilingGraphWithQuality()
            .FirstOrDefaultAsync(song => song.Id == songId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Records one step of one move. Each step is its own save, so a crash loses at most the step in
    /// flight, and the tracker is let go of afterwards so a large library's run stays cheap.
    /// </summary>
    /// <remarks>
    /// Never cancelled: it is called after files have moved, and a shutdown that stops the write leaves
    /// a file somewhere no row names. The write is a few bytes; the run's own cancellation is checked
    /// between steps instead.
    /// </remarks>
    private async Task SetStateAsync(
        long rowId,
        CompactMoveState state,
        string? staged,
        string? final,
        string? message)
    {
        var row = await _database.CompactMoves
            .FirstAsync(candidate => candidate.Id == rowId, CancellationToken.None)
            .ConfigureAwait(false);

        row.State = state;
        row.StagedPath = staged;
        row.FinalPath = final;
        row.Message = message;

        if (state == CompactMoveState.Failed)
        {
            row.Attempts++;
        }

        await _database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        _database.ChangeTracker.Clear();
    }

    /// <summary>
    /// Moves the staged sidecars beside the placed file, under the placed file's own name. A sidecar
    /// whose new name is already taken is left in staging and named in the log: the user's own file
    /// wins, and nothing is ever replaced or deleted.
    /// </summary>
    private void MoveSidecars(string stagedPath, string finalPath)
    {
        foreach (var (staged, target, targetTaken) in SongFileRules.SidecarMoves(_disk, stagedPath, finalPath))
        {
            if (targetTaken)
            {
                LogSidecarLeftBehind(_logger, staged, target);

                continue;
            }

            try
            {
                _disk.MoveFile(staged, target);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSidecarNotPlaced(_logger, staged, exception.Message);
            }
        }
    }

    /// <summary>Where a move parks its file: one folder per row under the library's hidden staging folder.</summary>
    private static string StagingPathFor(Library library, long rowId, string fromPath) =>
        Path.Combine(
            library.RootPath,
            StagingFolderName,
            rowId.ToString(CultureInfo.InvariantCulture),
            Path.GetFileName(fromPath));

    /// <summary>Removes the staging folder the placed file and its sidecars have left empty.</summary>
    private void DeleteStagingDirectory(string stagedPath)
    {
        var directory = Path.GetDirectoryName(stagedPath);

        if (directory is null)
        {
            return;
        }

        try
        {
            _disk.DeleteEmptyDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStagingNotDeleted(_logger, directory, exception.Message);
        }
    }

    /// <summary>The sidecars that sit beside a file, as it is now: the same name, <c>.lrc</c> or <c>.txt</c>.</summary>
    private IEnumerable<string> SidecarsOf(string audioPath) => SongFileRules.SidecarsOf(_disk, audioPath);

    /// <summary>Copies every proposed field onto the tracked album context. Pinned is not one of them.</summary>
    private static void Apply(AlbumContext album, string proposed)
    {
        var context = JsonSerializer.Deserialize<ProposedAlbum>(proposed, ProposedJson)
            ?? throw new InvalidOperationException("The move carries no proposed album context.");

        album.Kind = context.Kind;
        album.AlbumTitle = context.AlbumTitle ?? string.Empty;
        album.AlbumArtist = context.AlbumArtist ?? string.Empty;
        album.AlbumKey = context.AlbumKey ?? string.Empty;
        album.MbReleaseId = context.MbReleaseId;
        album.MbReleaseGroupId = context.MbReleaseGroupId;
        album.TrackNo = context.TrackNo;
        album.DiscNo = context.DiscNo;
        album.TotalTracks = context.TotalTracks;
        album.Date = context.Date;
        album.OriginalDate = context.OriginalDate;
        album.Label = context.Label;
        album.CoverUrl = context.CoverUrl;
        album.IsVariousArtists = context.IsVariousArtists;
    }

    /// <summary>The album context a plan decided, as the row's <c>proposed</c> column carries it.</summary>
    private static string SerializeProposed(AlbumContext album) => JsonSerializer.Serialize(
        new ProposedAlbum(
            album.Kind,
            album.AlbumTitle,
            album.AlbumArtist,
            album.AlbumKey,
            album.MbReleaseId,
            album.MbReleaseGroupId,
            album.TrackNo,
            album.DiscNo,
            album.TotalTracks,
            album.Date,
            album.OriginalDate,
            album.Label,
            album.CoverUrl,
            album.IsVariousArtists),
        ProposedJson);

    /// <summary>The fields of a proposed album context, as the <c>proposed</c> column stores them.</summary>
    private sealed record ProposedAlbum(
        AlbumContextKind Kind,
        string? AlbumTitle,
        string? AlbumArtist,
        string? AlbumKey,
        string? MbReleaseId,
        string? MbReleaseGroupId,
        int? TrackNo,
        int? DiscNo,
        int? TotalTracks,
        string? Date,
        string? OriginalDate,
        string? Label,
        string? CoverUrl,
        bool IsVariousArtists);

    /// <summary>The history payload of a compacted file: where it went, and where it came from.</summary>
    private sealed record CompactHistoryData(string Path, string? CompactedFrom);

    /// <summary>One row of the run, read once so the tracker can be let go of between steps.</summary>
    private sealed record Move(
        long Id,
        long SongId,
        string? FromPath,
        string? StagedPath,
        string? ToPath,
        string Proposed,
        CompactMoveState State)
    {
        /// <summary>Reads a tracked row into the run's own copy of it.</summary>
        internal static Move Of(CompactMoveRecord row) => new(
            row.Id,
            row.SongId,
            row.FromPath,
            row.StagedPath,
            row.ToPath,
            row.Proposed,
            row.State);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compaction of library {LibraryId}: dropping {Count} moves an earlier run could not finish (their files stayed where they were, or are recorded where they were placed)")]
    private static partial void LogFailedRowsDropped(ILogger logger, long libraryId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Library {LibraryId} has {Count} unfinished moves from an earlier compaction; finishing those")]
    private static partial void LogResuming(ILogger logger, long libraryId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compacted library {LibraryId}: {Moved} moved, {ContextOnly} without files, {Failed} failed, {Resumed} resumed")]
    private static partial void LogCompacted(ILogger logger, long libraryId, int moved, int contextOnly, int failed, int resumed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move {Path} out of the library for compact move {MoveId}: {Reason}")]
    private static partial void LogStagingFailed(ILogger logger, long moveId, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move the lyrics sidecar {Path} with its file: {Reason}; it stays where it is")]
    private static partial void LogSidecarNotStaged(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not put the lyrics sidecar {Path} beside the placed file: {Reason}; it stays in staging")]
    private static partial void LogSidecarNotPlaced(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The lyrics sidecar {Path} was left in staging: {Target} is where the lyrics are now")]
    private static partial void LogSidecarLeftBehind(ILogger logger, string path, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The emptied album folder {Path} was left as it is: {Reason}")]
    private static partial void LogFolderNotCleaned(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compacting move {MoveId} failed; the file stays in the staging folder")]
    private static partial void LogPlacementFailed(ILogger logger, long moveId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the compaction of move {MoveId} failed; the file is in the library already")]
    private static partial void LogRecordFailed(ILogger logger, long moveId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the empty staging folder {Path}: {Reason}")]
    private static partial void LogStagingNotDeleted(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Library {LibraryId} is not linked to a Plex section; nothing to scan or forget")]
    private static partial void LogNotLinked(ILogger logger, long libraryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Library {LibraryId} is linked to Plex but no server is connected; skipping the scan and empty trash")]
    private static partial void LogNoServer(ILogger logger, long libraryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} album folders changed in {Library}; scanning its root instead")]
    private static partial void LogTooManyFolders(ILogger logger, string library, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plex did not forget the old albums of library {LibraryId}: {Reason}; the files are still moved back")]
    private static partial void LogPlexFailed(ILogger logger, long libraryId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Section {Section} is still scanning after {Minutes} minutes; its trash is left alone")]
    private static partial void LogScanNotFinished(ILogger logger, string section, double minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library {LibraryId}: Plex's trash was not emptied because {Reason}")]
    private static partial void LogTrashNotEmptied(ILogger logger, long libraryId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plex preference {Setting} is {Value}")]
    private static partial void LogPreferenceValue(ILogger logger, string setting, string value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read the Plex preference {Setting}: {Reason}; treating it as unknown")]
    private static partial void LogPreferenceUnreadable(ILogger logger, string setting, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compact move {MoveId}: the song no longer exists and the recycle bin refused {Path}: {Reason}; it stays in staging")]
    private static partial void LogOrphanNotRecycled(ILogger logger, long moveId, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compact move {MoveId}: the song no longer exists; {Path} was moved to the recycle bin")]
    private static partial void LogOrphanRecycled(ILogger logger, long moveId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compact move {MoveId}: could not move {Staged} back to {From}: {Reason}")]
    private static partial void LogRestoreFailed(ILogger logger, long moveId, string staged, string from, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compact move {MoveId}: {Message}")]
    private static partial void LogGaveUp(ILogger logger, long moveId, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compact move {MoveId}: the file an interrupted run placed is at {Path}; recording it")]
    private static partial void LogFoundPlaced(ILogger logger, long moveId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compact move {MoveId}: the file was placed at {Path} although the organizer reported: {Reason}; recording it there")]
    private static partial void LogPlacedDespiteError(ILogger logger, long moveId, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compact move {MoveId}: the album was chosen by hand since the plan; keeping the user's choice")]
    private static partial void LogPinnedKept(ILogger logger, long moveId);
}
