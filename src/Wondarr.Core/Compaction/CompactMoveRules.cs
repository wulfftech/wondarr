using System.Linq.Expressions;
using Wondarr.Core.Domain;

namespace Wondarr.Core.Compaction;

/// <summary>
/// Which <c>compact_move</c> rows are still in flight: the ones an import or an upgrade selection
/// must wait out before they touch the song's file (P5-02).
/// </summary>
public static class CompactMoveRules
{
    /// <summary>
    /// Whether a row is unfinished: it is planned or staged, or it failed while its file is still
    /// parked in the staging folder under the row's <c>staged_path</c> (the next run resumes those).
    /// </summary>
    public static readonly Expression<Func<CompactMoveRecord, bool>> IsUnfinished = row =>
        row.State == CompactMoveState.Planned
        || row.State == CompactMoveState.Staged
        || (row.State == CompactMoveState.Failed && row.StagedPath != null);

    /// <summary>Only the unfinished rows of a set of moves.</summary>
    /// <param name="moves">The rows to filter.</param>
    /// <returns>The rows that are planned, staged, or failed with their file still in staging.</returns>
    public static IQueryable<CompactMoveRecord> Unfinished(IQueryable<CompactMoveRecord> moves) =>
        moves.Where(IsUnfinished);
}
