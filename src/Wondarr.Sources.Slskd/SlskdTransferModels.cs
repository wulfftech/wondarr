using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// The body of <c>POST /api/v0/transfers/downloads/batches</c>. Files in one batch must belong to
/// one peer, so one batch carries one username.
/// </summary>
/// <param name="Username">The peer the files are downloaded from.</param>
/// <param name="Files">The files to enqueue, at least one.</param>
/// <param name="Options">Where slskd should put them and which grab they belong to.</param>
/// <param name="Id">The batch id Wondarr chose, so the batch can be recognised again.</param>
public sealed record SlskdEnqueueBatchRequest(
    string Username,
    IReadOnlyList<SlskdEnqueueFile> Files,
    SlskdBatchOptions? Options,
    Guid? Id = null);

/// <summary>One file to enqueue, as the peer advertised it.</summary>
/// <param name="Filename">The peer's remote path, with backslash separators.</param>
/// <param name="Size">The size the peer advertised, in bytes.</param>
public sealed record SlskdEnqueueFile(string Filename, long Size);

/// <summary>Per-batch options: a download subfolder and the caller's own correlation id.</summary>
/// <param name="Destination">A path relative to slskd's configured download directory.</param>
/// <param name="ExternalId">Wondarr's own identifier for the grab, echoed back by slskd.</param>
public sealed record SlskdBatchOptions(string? Destination, string? ExternalId);

/// <summary>
/// slskd's answer to an enqueue: the batch it created, and one entry per file it could not enqueue.
/// 201 means every file was enqueued, 207 means some were, 200 means none were (only failures).
/// </summary>
public sealed record SlskdEnqueueBatchResponse
{
    /// <summary>The batch, absent when nothing was enqueued.</summary>
    public SlskdBatch? Batch { get; init; }

    /// <summary>The files slskd refused, with its own reason for each.</summary>
    public IReadOnlyList<SlskdEnqueueFailure> Failures { get; init; } = [];
}

/// <summary>One file slskd refused to enqueue.</summary>
/// <param name="Filename">The file that was refused.</param>
/// <param name="Message">slskd's reason.</param>
public sealed record SlskdEnqueueFailure(string Filename, string Message);

/// <summary>
/// The batch slskd created. Only <see cref="Id"/> is relied on: the rest of slskd's batch shape was
/// not verified against 0.26.0, so unknown fields are kept here rather than modelled.
/// </summary>
public sealed record SlskdBatch
{
    /// <summary>The batch id, which Wondarr chose and slskd echoed back.</summary>
    public Guid Id { get; init; }

    /// <summary>Every other field slskd sent, unmodelled.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Additional { get; init; }
}

/// <summary>
/// One transfer, as slskd reports it. Fields slskd omits when empty are nullable or defaulted here,
/// because it never sends them as JSON <c>null</c>.
/// </summary>
public sealed record SlskdTransfer
{
    /// <summary>The transfer id.</summary>
    public Guid Id { get; init; }

    /// <summary>The batch the transfer belongs to, absent when it was not enqueued as one.</summary>
    public Guid? BatchId { get; init; }

    /// <summary>The peer on the other end.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary><c>Download</c> or <c>Upload</c>.</summary>
    public string Direction { get; init; } = string.Empty;

    /// <summary>The remote path, with backslash separators.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>The file's size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>A comma-separated flags string; see <see cref="SlskdTransferStates.Parse"/>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>How many bytes have moved so far.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>The average speed so far, in bytes per second.</summary>
    public double AverageSpeed { get; init; }

    /// <summary>How complete the transfer is, as a percentage.</summary>
    public double PercentComplete { get; init; }

    /// <summary>Where we sit in the peer's upload queue, once the peer has told us.</summary>
    public int? PlaceInQueue { get; init; }

    /// <summary>Why the transfer failed, when it did.</summary>
    public string? Exception { get; init; }

    /// <summary>How many times slskd has tried.</summary>
    public int Attempts { get; init; }

    /// <summary>When Wondarr asked for the file.</summary>
    public DateTime RequestedAt { get; init; }

    /// <summary>When slskd handed the request to the peer.</summary>
    public DateTime? EnqueuedAt { get; init; }

    /// <summary>When the bytes started moving.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When the transfer stopped, for whatever reason.</summary>
    public DateTime? EndedAt { get; init; }

    /// <summary>Whether the transfer was removed from slskd's record.</summary>
    public bool Removed { get; init; }
}

/// <summary>One peer's downloads, grouped the way slskd groups them.</summary>
public sealed record SlskdUserTransfers
{
    /// <summary>The peer's Soulseek username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>The remote directories holding this peer's transfers.</summary>
    public IReadOnlyList<SlskdTransferDirectory> Directories { get; init; } = [];
}

/// <summary>One remote directory's worth of transfers.</summary>
public sealed record SlskdTransferDirectory
{
    /// <summary>The remote directory, with backslash separators.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>How many files slskd reports in it.</summary>
    public int FileCount { get; init; }

    /// <summary>The transfers in it.</summary>
    public IReadOnlyList<SlskdTransfer> Files { get; init; } = [];
}

/// <summary>
/// Every state a transfer can be in, as the flag values Soulseek.NET uses. A transfer carries
/// several of these at once (<c>Queued, Remotely</c>, <c>Completed, Succeeded</c>), so they are
/// tested with <see cref="Enum.HasFlag(Enum)"/> rather than compared.
/// </summary>
[Flags]
public enum SlskdTransferState
{
    /// <summary>Nothing known yet.</summary>
    None = 0,

    /// <summary>Wondarr has asked for the file.</summary>
    Requested = 1,

    /// <summary>Queued, either locally or with the peer.</summary>
    Queued = 2,

    /// <summary>The transfer is being set up.</summary>
    Initializing = 4,

    /// <summary>Bytes are moving.</summary>
    InProgress = 8,

    /// <summary>The transfer has stopped, for one of the reasons below.</summary>
    Completed = 16,

    /// <summary>It finished, and the file is complete.</summary>
    Succeeded = 32,

    /// <summary>We cancelled it.</summary>
    Cancelled = 64,

    /// <summary>The peer stopped answering.</summary>
    TimedOut = 128,

    /// <summary>It failed.</summary>
    Errored = 256,

    /// <summary>The peer refused it.</summary>
    Rejected = 512,

    /// <summary>It was abandoned, by either side.</summary>
    Aborted = 1024,

    /// <summary>In our own queue.</summary>
    Locally = 2048,

    /// <summary>In the peer's queue.</summary>
    Remotely = 4096,
}

/// <summary>
/// Reads slskd's own state strings: a comma-separated list of the flag names above, in no
/// particular order (<c>"Queued, Remotely"</c>, <c>"Completed, Succeeded"</c>, <c>"InProgress"</c>).
/// </summary>
public static class SlskdTransferStates
{
    private static readonly Dictionary<string, SlskdTransferState> Names = new(StringComparer.Ordinal)
    {
        ["Requested"] = SlskdTransferState.Requested,
        ["Queued"] = SlskdTransferState.Queued,
        ["Initializing"] = SlskdTransferState.Initializing,
        ["InProgress"] = SlskdTransferState.InProgress,
        ["Completed"] = SlskdTransferState.Completed,
        ["Succeeded"] = SlskdTransferState.Succeeded,
        ["Cancelled"] = SlskdTransferState.Cancelled,
        ["TimedOut"] = SlskdTransferState.TimedOut,
        ["Errored"] = SlskdTransferState.Errored,
        ["Rejected"] = SlskdTransferState.Rejected,
        ["Aborted"] = SlskdTransferState.Aborted,
        ["Locally"] = SlskdTransferState.Locally,
        ["Remotely"] = SlskdTransferState.Remotely,
    };

    /// <summary>
    /// Parses one of slskd's state strings. Words slskd adds that Wondarr does not model are
    /// ignored, so a new flag on slskd's side degrades to a coarser state instead of an exception.
    /// </summary>
    public static SlskdTransferState Parse(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return SlskdTransferState.None;
        }

        var parsed = SlskdTransferState.None;

        foreach (var word in state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Names.TryGetValue(word, out var flag))
            {
                parsed |= flag;
            }
        }

        return parsed;
    }
}