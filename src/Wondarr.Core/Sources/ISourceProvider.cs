using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>What a source needs to know about the wanted song to search for it.</summary>
/// <param name="SongId">The song being searched for.</param>
/// <param name="Title">The song's title as stored (may carry version hints such as "(Live)").</param>
/// <param name="ArtistCredit">The display credit, e.g. "Daft Punk feat. Pharrell Williams".</param>
/// <param name="MainArtists">Names of the main (not featured) credited artists, in credit order.</param>
/// <param name="AlbumTitle">The assigned album context's title, when there is one (used for the "artist album" query).</param>
/// <param name="DurationMs">Known duration.</param>
/// <param name="VersionFlags">The song's own version flags.</param>
public sealed record SongSearchRequest(
    long SongId,
    string Title,
    string ArtistCredit,
    IReadOnlyList<string> MainArtists,
    string? AlbumTitle,
    int? DurationMs,
    VersionFlags VersionFlags)
{
    /// <summary>
    /// Called by the source after each query with every candidate found so far; returning <c>true</c> stops
    /// the query sequence early ("stop early when the candidate pool is good", MATCHING_ENGINE §6.4).
    /// Null means "run every query".
    /// </summary>
    public Func<IReadOnlyList<Candidate>, bool>? IsPoolGoodEnough { get; init; }
}

/// <summary>The outcome of one <see cref="ISourceProvider.SearchAsync"/> call.</summary>
/// <param name="Candidates">Every candidate from every query, de-duplicated by <see cref="Candidate.BlocklistKey"/>.</param>
/// <param name="Queries">The search texts actually submitted, in order.</param>
/// <param name="Message">Why the search returned nothing or stopped, when it did (e.g. "Soulseek: not logged in").</param>
public sealed record SourceSearchResult(
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<string> Queries,
    string? Message = null);

/// <summary>
/// Opaque, persistable reference to a grab at a source. <see cref="Value"/> is JSON only the source that
/// produced it reads (Soulseek: username, transfer id, remote path).
/// </summary>
public sealed record GrabHandle(string SourceType, string Value);

/// <summary>Where a grab stands.</summary>
public enum DownloadState
{
    /// <summary>Accepted locally, not yet acknowledged by the peer.</summary>
    Queued,

    /// <summary>Waiting in the peer's upload queue.</summary>
    RemotelyQueued,

    /// <summary>Bytes are flowing.</summary>
    Downloading,

    /// <summary>The file is complete on disk at <see cref="DownloadStatus.CompletedPath"/>.</summary>
    Completed,

    /// <summary>The transfer failed (errored, rejected, timed out, aborted, peer offline).</summary>
    Failed,

    /// <summary>Cancelled by us.</summary>
    Cancelled,
}

/// <summary>A snapshot of a grab's progress.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Progress">0–1.</param>
/// <param name="BytesTransferred">Bytes received so far.</param>
/// <param name="SizeBytes">Expected size, when known.</param>
/// <param name="PlaceInQueue">Position in the peer's queue, when remotely queued and known.</param>
/// <param name="Message">Human-readable detail (the source's error text on failure).</param>
/// <param name="CompletedPath">Local path of the finished file (only when <see cref="DownloadState.Completed"/>).</param>
public sealed record DownloadStatus(
    DownloadState State,
    double Progress,
    long BytesTransferred,
    long? SizeBytes = null,
    int? PlaceInQueue = null,
    string? Message = null,
    string? CompletedPath = null);

/// <summary>
/// One source type (ARCHITECTURE §5.3). Implementations are singletons and must be safe for concurrent
/// calls; rate limits (the Soulseek search budget) are enforced inside the implementation.
/// </summary>
public interface ISourceProvider
{
    /// <summary>One of <see cref="SourceTypes"/>.</summary>
    string SourceType { get; }

    /// <summary>Whether the source can take a search right now (configured, logged in); with the reason when not.</summary>
    Task<(bool Available, string? Reason)> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Runs the source's query strategy for <paramref name="request"/>. Never throws for "no results".</summary>
    Task<SourceSearchResult> SearchAsync(SongSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Starts downloading <paramref name="candidate"/> into a per-grab folder named <paramref name="destination"/>
    /// (relative, no traversal) under the source's download directory.
    /// </summary>
    Task<GrabHandle> GrabAsync(Candidate candidate, string destination, CancellationToken cancellationToken);

    /// <summary>Current progress of a grab.</summary>
    Task<DownloadStatus> GetStatusAsync(GrabHandle handle, CancellationToken cancellationToken);

    /// <summary>Cancels a grab and removes it from the source's transfer list. Idempotent.</summary>
    Task CancelAsync(GrabHandle handle, CancellationToken cancellationToken);
}
