using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One candidate a search saw, kept so the interactive search and the history can show *why* it was
/// accepted or rejected (MATCHING_ENGINE §6). Stored in the <c>candidate</c> table.
/// </summary>
public sealed class CandidateRecord : EntityBase
{
    /// <summary>Gets or sets the run that saw this candidate.</summary>
    public long SearchRunId { get; set; }

    /// <summary>Gets or sets the song the candidate was found for.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets the source type; one of <see cref="SourceTypes"/>.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured source instance, or <see langword="null"/> for the bundled slskd.</summary>
    public long? SourceInstanceId { get; set; }

    /// <summary>Gets or sets the stable identity of this file at its source (blocklist key and de-duplication).</summary>
    public string BlocklistKey { get; set; } = string.Empty;

    /// <summary>Gets or sets what the UI shows: the file name, video title or release name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's own path or id for the file, as the source wants it back when grabbing.</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the Soulseek username, YouTube channel or indexer name, or <see langword="null"/>.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the inferred quality id from the <c>quality</c> seed.</summary>
    public long QualityId { get; set; } = 1;

    /// <summary>Gets or sets the size in bytes when the source reported one.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the duration in milliseconds when the source reported one.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Gets or sets the JSON snapshot of the normalised <c>Candidate</c>, an empty object when there is none.</summary>
    public string Normalised { get; set; } = "{}";

    /// <summary>Gets or sets the score the decision engine gave the candidate (0–1000).</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets the JSON breakdown of the score's components and adjustments.</summary>
    public string ScoreBreakdown { get; set; } = "{}";

    /// <summary>Gets or sets the JSON array of hard-rejection reasons, empty when none applied.</summary>
    public string Rejections { get; set; } = "[]";

    /// <summary>Gets or sets a value indicating whether the decision engine accepted the candidate.</summary>
    public bool Accepted { get; set; }

    /// <summary>Gets or sets a value indicating whether the candidate was handed to its download client.</summary>
    public bool Grabbed { get; set; }

    /// <summary>Gets or sets the run that saw this candidate.</summary>
    public SearchRun SearchRun { get; set; } = null!;

    /// <summary>Gets or sets the song the candidate was found for.</summary>
    public Song Song { get; set; } = null!;
}