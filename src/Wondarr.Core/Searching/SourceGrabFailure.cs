namespace Wondarr.Core.Searching;

/// <summary>
/// What a source says about a grab that failed: whether the candidate itself is dead, or whether the
/// failure was the source's own bad moment. Implemented by a source's classified grab exceptions —
/// Core cannot name them (the source projects depend on Core, not the other way round), so the search
/// service matches the inner exception of a <see cref="GrabFailedException"/> against this interface.
/// </summary>
public interface ISourceGrabFailure
{
    /// <summary>
    /// True: the candidate is dead — geo-restricted, age-gated, gone — so it is blocklisted for the
    /// song and the next candidate is tried. False: the source failed, not the candidate (a bot check,
    /// a rate limit, a missing tool), so the run stops and the song's own backoff owns the retry.
    /// </summary>
    bool BlocklistCandidate { get; }

    /// <summary>The classified reason, recorded on the blocklist row or the failed item.</summary>
    string Message { get; }
}
