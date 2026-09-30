using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One ranked identification candidate for a <see cref="ReferenceFile"/>. The identity itself is a JSON
/// blob: only the identification pipeline (Phase 3 task P3-02) knows what it holds, and the scan never
/// writes one.
/// </summary>
public sealed class MatchCandidate : EntityBase
{
    /// <summary>Gets or sets the file this candidate is for.</summary>
    public long ReferenceFileId { get; set; }

    /// <summary>Gets or sets the candidate's rank, 1 being the best.</summary>
    public int Rank { get; set; }

    /// <summary>
    /// Gets or sets the candidate's identity as JSON (<c>source</c>, <c>mbRecordingId?</c>,
    /// <c>deezerId?</c>, <c>title</c>, <c>artistCredit</c>, <c>durationMs?</c>, <c>albumTitle?</c>).
    /// </summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Gets or sets the score that ranked the candidate.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets why the candidate scored what it did.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the file this candidate is for.</summary>
    public ReferenceFile ReferenceFile { get; set; } = null!;
}