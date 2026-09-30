using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>How far one Compact library move has got. Stored as a string, like the other enums.</summary>
public enum CompactMoveState
{
    /// <summary>The move is recorded but nothing has been touched yet.</summary>
    Planned,

    /// <summary>The file (and its sidecars) is out of the library, in the hidden staging folder.</summary>
    Staged,

    /// <summary>The file is at its new path in the library and the rows are written.</summary>
    Placed,

    /// <summary>The move stopped; <see cref="CompactMove.Message"/> says why, and the next run retries it when it can.</summary>
    Failed,
}

/// <summary>
/// One file (or one album change) a Compact library run has to make: where the file is, where it is
/// parked while Plex forgets it, and where it ends up. Stored in the <c>compact_move</c> table.
/// </summary>
/// <remarks>
/// <para>
/// One row per move, written before anything is touched and saved again after each step, so a crash or
/// a restart resumes from the rows instead of losing files: a file that is not where the library says
/// it is, is in <see cref="StagedPath"/>.
/// </para>
/// <para>
/// It is a <c>Record</c> because <c>Wondarr.Core.Compaction.CompactMove</c> — the planner's description
/// of a move, which is not a row — already holds that name, and the API imports both namespaces.
/// </para>
/// </remarks>
public sealed class CompactMoveRecord : EntityBase
{
    /// <summary>Gets or sets the library being compacted.</summary>
    public long LibraryId { get; set; }

    /// <summary>Gets or sets the song whose album changes.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets where the file was, or <see langword="null"/> for a change that touches no file.</summary>
    public string? FromPath { get; set; }

    /// <summary>Gets or sets where the file is parked while Plex forgets it, or <see langword="null"/>.</summary>
    public string? StagedPath { get; set; }

    /// <summary>Gets or sets the path the planner expected the file to get, for the user to read.</summary>
    public string? ToPath { get; set; }

    /// <summary>Gets or sets where the file ended up, once it has been placed.</summary>
    public string? FinalPath { get; set; }

    /// <summary>
    /// Gets or sets the album context the move writes, as JSON: the kind, title, album artist, key,
    /// release and release-group ids, track/disc/total, date, original date, label, cover URL and the
    /// various-artists flag. The row has to carry it: a resumed run applies the album the plan decided,
    /// not whatever a fresh re-plan would say today.
    /// </summary>
    public string Proposed { get; set; } = "{}";

    /// <summary>Gets or sets how far the move has got.</summary>
    public CompactMoveState State { get; set; } = CompactMoveState.Planned;

    /// <summary>Gets or sets why the move failed, or <see langword="null"/>.</summary>
    public string? Message { get; set; }
}
