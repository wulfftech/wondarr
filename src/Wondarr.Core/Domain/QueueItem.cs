using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>Where a grabbed candidate is in the download and import pipeline.</summary>
public enum QueueItemState
{
    /// <summary>Recorded but not yet handed to the source.</summary>
    Queued,

    /// <summary>The source has accepted the grab (slskd enqueued it, the client added the torrent).</summary>
    RemotelyQueued,

    /// <summary>Bytes are moving.</summary>
    Downloading,

    /// <summary>The source reports the file complete; the import pipeline has not picked it up yet.</summary>
    Completed,

    /// <summary>The import pipeline is verifying, tagging and placing the file.</summary>
    Importing,

    /// <summary>The file was imported and the song satisfied.</summary>
    Imported,

    /// <summary>The grab or the import failed; the search loop may try the next candidate.</summary>
    Failed,

    /// <summary>The grab was stopped by the user.</summary>
    Cancelled,
}

/// <summary>
/// One grab in flight: what was grabbed, where it is, and when it was last heard from
/// (ARCHITECTURE §5.4). Stored in the <c>queue_item</c> table.
/// </summary>
public sealed class QueueItem : EntityBase
{
    /// <summary>Gets or sets the song the grab is for.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets the candidate that was grabbed.</summary>
    public long CandidateId { get; set; }

    /// <summary>Gets or sets the search run that produced the candidate.</summary>
    public long SearchRunId { get; set; }

    /// <summary>Gets or sets the source type; one of <see cref="SourceTypes"/>.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured source instance, or <see langword="null"/> for the bundled slskd.</summary>
    public long? SourceInstanceId { get; set; }

    /// <summary>Gets or sets the source's grab handle as JSON (<c>GrabHandle.Value</c>), or <see langword="null"/> before the grab is accepted.</summary>
    public string? Handle { get; set; }

    /// <summary>Gets or sets the per-grab folder the source downloads into, for example <c>wondarr/17</c>.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Gets or sets where the grab is in the pipeline.</summary>
    public QueueItemState State { get; set; }

    /// <summary>Gets or sets how far the download has got, 0–1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets how many bytes have arrived.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Gets or sets the total size in bytes when the source reports one.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the position in the source's own queue (Soulseek), or <see langword="null"/>.</summary>
    public int? PlaceInQueue { get; set; }

    /// <summary>Gets or sets the last message the source reported (a stall reason, a bot check).</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the absolute path the source downloaded to, once known.</summary>
    public string? DownloadPath { get; set; }

    /// <summary>Gets or sets which automatic attempt of the search this grab is, 1-based.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>Gets or sets the UTC instant the state last changed.</summary>
    public DateTime StateChangedAt { get; set; }

    /// <summary>Gets or sets the UTC instant progress was last seen to move.</summary>
    public DateTime LastProgressAt { get; set; }

    /// <summary>Gets or sets the UTC instant the queue poll should look at this item next, or <see langword="null"/>.</summary>
    public DateTime? NextCheckAt { get; set; }

    /// <summary>Gets or sets the UTC instant the grab finished (imported, failed or cancelled), or <see langword="null"/>.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the song the grab is for.</summary>
    public Song Song { get; set; } = null!;

    /// <summary>Gets or sets the candidate that was grabbed.</summary>
    public CandidateRecord Candidate { get; set; } = null!;
}
