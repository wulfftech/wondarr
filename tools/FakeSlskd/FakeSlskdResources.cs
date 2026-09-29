using System.Text.Json.Serialization;

namespace FakeSlskd;

/// <summary>The body of <c>POST /api/v0/searches</c>, as the app sends it.</summary>
/// <param name="Id">The id the caller picked, when it picked one.</param>
/// <param name="SearchText">What to search for.</param>
/// <param name="SearchTimeout">How long peers may take, in milliseconds (unused by the fake).</param>
/// <param name="ResponseLimit">Unused by the fake.</param>
/// <param name="FileLimit">Unused by the fake.</param>
/// <param name="MinimumPeerUploadSpeed">Unused by the fake.</param>
public sealed record SearchRequestBody(
    Guid? Id,
    string SearchText,
    int? SearchTimeout,
    int? ResponseLimit,
    int? FileLimit,
    int? MinimumPeerUploadSpeed);

/// <summary>One search, as slskd reports it.</summary>
public sealed record SlskdSearchResource
{
    /// <summary>The search's id.</summary>
    public Guid Id { get; init; }

    /// <summary>The text that was searched for.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>The token slskd assigned to the search.</summary>
    public int Token { get; init; }

    /// <summary>A flags string: <c>InProgress</c>, or <c>Completed</c> plus a reason.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>When the search was submitted.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>When it stopped; omitted while it is still running.</summary>
    public DateTime? EndedAt { get; init; }

    /// <summary>How many files the accumulated responses offer.</summary>
    public int FileCount { get; init; }

    /// <summary>How many of those files are locked.</summary>
    public int LockedFileCount { get; init; }

    /// <summary>How many peers answered.</summary>
    public int ResponseCount { get; init; }

    /// <summary>Whether slskd has stopped collecting responses.</summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Always empty, like the real endpoint's default. slskd only fills this in when the caller asks
    /// for responses; <c>GET /searches/{id}/responses</c> is the endpoint that returns them.
    /// </summary>
    public IReadOnlyList<SlskdResponseResource> Responses { get; init; } = [];
}

/// <summary>One peer's answer to a search.</summary>
public sealed record SlskdResponseResource
{
    /// <summary>The peer's Soulseek username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>The token of the search this answered.</summary>
    public int Token { get; init; }

    /// <summary>Whether the peer has an upload slot free.</summary>
    public bool HasFreeUploadSlot { get; init; }

    /// <summary>The peer's advertised upload speed in bytes per second.</summary>
    public long UploadSpeed { get; init; }

    /// <summary>How many transfers are ahead of ours in the peer's queue.</summary>
    public int QueueLength { get; init; }

    /// <summary>How many files the answer offers.</summary>
    public int FileCount { get; init; }

    /// <summary>How many of them are locked.</summary>
    public int LockedFileCount { get; init; }

    /// <summary>The files the peer offers.</summary>
    public IReadOnlyList<SlskdFileResource> Files { get; init; } = [];

    /// <summary>The files the peer keeps locked; always empty in the fake.</summary>
    public IReadOnlyList<SlskdFileResource> LockedFiles { get; init; } = [];
}

/// <summary>One file inside a search response.</summary>
public sealed record SlskdFileResource
{
    /// <summary>The peer's own path, with backslash separators.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>The file's size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Empty in practice, like the real responses.</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>The Soulseek attribute code for audio files.</summary>
    public int Code { get; init; } = 1;

    /// <summary>The bitrate in kbps; omitted for lossless files.</summary>
    public int? BitRate { get; init; }

    /// <summary>The sample rate in Hz; omitted for lossy files.</summary>
    public int? SampleRate { get; init; }

    /// <summary>The bit depth; omitted for lossy files.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The length in seconds.</summary>
    public int Length { get; init; }

    /// <summary>Whether the file sits behind the peer's private share.</summary>
    public bool IsLocked { get; init; }
}

/// <summary>The body of <c>POST /api/v0/transfers/downloads/batches</c>.</summary>
/// <param name="Id">The batch id the caller picked, when it picked one.</param>
/// <param name="SearchId">The search the files came from, when the caller says so.</param>
/// <param name="Username">The peer the files belong to.</param>
/// <param name="Files">The files to queue.</param>
/// <param name="Options">Per-batch options.</param>
public sealed record EnqueueBatchRequestBody(
    Guid? Id,
    Guid? SearchId,
    string Username,
    IReadOnlyList<EnqueueFileBody> Files,
    EnqueueOptionsBody? Options);

/// <summary>One file in a batch request.</summary>
/// <param name="Filename">The peer's path for the file.</param>
/// <param name="Size">The size the caller believes the file has.</param>
public sealed record EnqueueFileBody(string Filename, long Size);

/// <summary>Per-batch options.</summary>
/// <param name="Destination">Subdirectory of the downloads directory the files belong in.</param>
/// <param name="ExternalId">The caller's own id for the batch.</param>
public sealed record EnqueueOptionsBody(string? Destination, string? ExternalId);

/// <summary>The batch a request created.</summary>
public sealed record SlskdBatchResource
{
    /// <summary>The batch's id.</summary>
    public Guid Id { get; init; }

    /// <summary>The peer the batch belongs to.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>The search the batch came from, when the caller gave one.</summary>
    public Guid? SearchId { get; init; }
}

/// <summary>The body of the batch response.</summary>
public sealed record EnqueueBatchResponse
{
    /// <summary>The batch that was created.</summary>
    public SlskdBatchResource Batch { get; init; } = new();

    /// <summary>The files that could not be queued.</summary>
    public IReadOnlyList<EnqueueFailureResource> Failures { get; init; } = [];
}

/// <summary>One file that could not be queued.</summary>
/// <param name="Filename">The file's path.</param>
/// <param name="Message">Why it could not be queued.</param>
public sealed record EnqueueFailureResource(string Filename, string Message);

/// <summary>One download, as slskd reports it.</summary>
public sealed record SlskdTransferResource
{
    /// <summary>The transfer's id.</summary>
    public Guid Id { get; init; }

    /// <summary>The batch the transfer belongs to.</summary>
    public Guid BatchId { get; init; }

    /// <summary>The peer the file comes from.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Always <c>Download</c>.</summary>
    public string Direction { get; init; } = "Download";

    /// <summary>The peer's path for the file.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>The file's size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>A flags string, for example <c>Queued, Remotely</c> or <c>Completed, Succeeded</c>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>When the transfer was requested.</summary>
    public DateTime RequestedAt { get; init; }

    /// <summary>When the peer accepted it.</summary>
    public DateTime? EnqueuedAt { get; init; }

    /// <summary>When the first byte arrived.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When it finished.</summary>
    public DateTime? EndedAt { get; init; }

    /// <summary>How many bytes have arrived.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>The average speed in bytes per second.</summary>
    public double AverageSpeed { get; init; }

    /// <summary>How far down the peer's queue the transfer is.</summary>
    public int? PlaceInQueue { get; init; }

    /// <summary>Whether the record has been removed.</summary>
    public bool Removed { get; init; }

    /// <summary>How many bytes are left.</summary>
    public long BytesRemaining => Math.Max(0, Size - BytesTransferred);

    /// <summary>How long the transfer has been running, in milliseconds.</summary>
    public long ElapsedTime => StartedAt is { } started
        ? (long)((EndedAt ?? DateTime.UtcNow) - started).TotalMilliseconds
        : 0;

    /// <summary>How much of the file has arrived, in percent.</summary>
    public double PercentComplete => Size <= 0 ? 0 : Math.Round(100.0 * BytesTransferred / Size, 2);

    /// <summary>An estimate of the time left, in milliseconds; omitted while nothing is arriving.</summary>
    public long? RemainingTime => AverageSpeed > 0 && BytesRemaining > 0
        ? (long)(BytesRemaining / AverageSpeed * 1000)
        : null;
}

/// <summary>One peer's downloads, grouped by directory.</summary>
public sealed record DownloadUserResource
{
    /// <summary>The peer.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>The peer's directories that hold transfers.</summary>
    public IReadOnlyList<DownloadDirectoryResource> Directories { get; init; } = [];
}

/// <summary>One remote directory's transfers.</summary>
public sealed record DownloadDirectoryResource
{
    /// <summary>The peer's directory.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>How many transfers the directory holds.</summary>
    public int FileCount { get; init; }

    /// <summary>The transfers.</summary>
    public IReadOnlyList<SlskdTransferResource> Files { get; init; } = [];
}

/// <summary>What the gate reads back from <c>GET /fake/log</c>.</summary>
/// <param name="Searches">Every search the app submitted, newest last.</param>
/// <param name="MaxInFlight">The most searches that were in flight at any moment.</param>
/// <param name="Transfers">Every download the app queued.</param>
public sealed record GateLogResource(
    IReadOnlyList<GateSearchResource> Searches,
    int MaxInFlight,
    IReadOnlyList<GateTransferResource> Transfers);

/// <summary>One search, as the gate's log reports it.</summary>
/// <param name="Id">The search's id.</param>
/// <param name="Text">The text that was searched for.</param>
/// <param name="PostedAt">When it was submitted, ISO-8601 UTC with milliseconds.</param>
/// <param name="DeletedAt">When it was deleted, or <see langword="null"/> when it still exists.</param>
public sealed record GateSearchResource(
    Guid Id,
    string Text,
    string PostedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DeletedAt);

/// <summary>One download, as the gate's log reports it.</summary>
/// <param name="Id">The transfer's id.</param>
/// <param name="Username">The peer.</param>
/// <param name="Filename">The peer's path for the file.</param>
/// <param name="Destination">The subdirectory of the downloads directory the file was moved to.</param>
/// <param name="State">The transfer's state.</param>
public sealed record GateTransferResource(Guid Id, string Username, string Filename, string Destination, string State);
