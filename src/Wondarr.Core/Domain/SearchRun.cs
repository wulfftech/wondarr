using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>What started a search run.</summary>
public enum SearchTrigger
{
    /// <summary>The scheduler searched because the song is missing or below cutoff.</summary>
    Automatic,

    /// <summary>The user asked for a search from the UI or the API.</summary>
    Manual,

    /// <summary>The upgrade loop searched for a better file than the one held.</summary>
    Upgrade,

    /// <summary>An import list sync triggered the search.</summary>
    List,
}

/// <summary>How a search run ended.</summary>
public enum SearchOutcome
{
    /// <summary>A candidate was handed to its download client.</summary>
    Grabbed,

    /// <summary>Candidates came back but every one was rejected.</summary>
    NoAcceptableCandidate,

    /// <summary>No source returned anything.</summary>
    NoResults,

    /// <summary>A source was unreachable or refused the search.</summary>
    SourceUnavailable,

    /// <summary>The run errored before it could decide.</summary>
    Failed,

    /// <summary>The run was stopped by the user or by shutdown.</summary>
    Cancelled,
}

/// <summary>
/// One search pass over a song: when it ran, what it asked, what came back and how it ended
/// (ARCHITECTURE §5.4). Stored in the <c>search_run</c> table; the candidates it saw hang off it, so
/// historic runs keep their evidence after the blocklist or the profiles change.
/// </summary>
public sealed class SearchRun : EntityBase
{
    /// <summary>Gets or sets the song that was searched for.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets what started the run.</summary>
    public SearchTrigger Trigger { get; set; }

    /// <summary>Gets or sets the UTC instant the run started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the UTC instant the run finished, or <see langword="null"/> while it is running.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the source types that were asked, in the order they were tried.</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>Gets or sets the search texts that were sent, so a run can be replayed by hand.</summary>
    public List<string> Queries { get; set; } = [];

    /// <summary>Gets or sets how many candidates the run stored.</summary>
    public int CandidateCount { get; set; }

    /// <summary>Gets or sets how the run ended, or <see langword="null"/> while it is running.</summary>
    public SearchOutcome? Outcome { get; set; }

    /// <summary>Gets or sets the human-readable note the run left (why it failed, why nothing was accepted).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the song that was searched for.</summary>
    public Song Song { get; set; } = null!;
}
