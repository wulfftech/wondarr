namespace Wondarr.Sources.Slskd;

/// <summary>
/// The body of <c>POST /api/v0/searches</c>. slskd accepts a client-chosen <c>id</c>, so Wondarr can
/// poll and delete the search it just created even if the response never arrives.
/// </summary>
/// <param name="Id">The id Wondarr picked for this search.</param>
/// <param name="SearchText">What to ask the network for.</param>
/// <param name="SearchTimeout">How long peers may take to answer, in milliseconds.</param>
/// <param name="ResponseLimit">Stop after this many peer responses (the network's own guidance is 100).</param>
/// <param name="FileLimit">Stop after this many files across all responses.</param>
/// <param name="MinimumPeerUploadSpeed">Bytes per second a peer must offer to be counted.</param>
public sealed record SlskdSearchRequest(
    Guid Id,
    string SearchText,
    int SearchTimeout,
    int ResponseLimit,
    int FileLimit,
    int MinimumPeerUploadSpeed);

/// <summary>
/// The state of one search, as slskd reports it. Fields slskd omits when empty are nullable or
/// defaulted here, because it never sends them as JSON <c>null</c>.
/// </summary>
public sealed record SlskdSearch
{
    /// <summary>The id the search was created with.</summary>
    public Guid Id { get; init; }

    /// <summary>The text that was searched for.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>A flags string: <c>InProgress</c>, or <c>Completed</c> plus one or more of
    /// <c>ResponseLimitReached</c>, <c>TimedOut</c>, <c>FileLimitReached</c>, <c>Cancelled</c>,
    /// <c>Errored</c>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Whether slskd has stopped collecting responses for this search.</summary>
    public bool IsComplete { get; init; }

    /// <summary>How many peers have answered.</summary>
    public int ResponseCount { get; init; }

    /// <summary>How many files those answers offered, locked ones included.</summary>
    public int FileCount { get; init; }

    /// <summary>How many of those files are locked behind a private share.</summary>
    public int LockedFileCount { get; init; }

    /// <summary>When the search was submitted.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>When it stopped, absent while it is still running.</summary>
    public DateTime? EndedAt { get; init; }
}

/// <summary>One peer's answer to a search.</summary>
public sealed record SlskdSearchResponse
{
    /// <summary>The peer's Soulseek username.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Whether the peer has an upload slot free right now.</summary>
    public bool HasFreeUploadSlot { get; init; }

    /// <summary>The peer's advertised upload speed in bytes per second.</summary>
    public long UploadSpeed { get; init; }

    /// <summary>How many transfers are ahead of ours in the peer's queue.</summary>
    public int QueueLength { get; init; }

    /// <summary>How many files the answer offers, locked ones included.</summary>
    public int FileCount { get; init; }

    /// <summary>How many of those files are locked behind a private share.</summary>
    public int LockedFileCount { get; init; }

    /// <summary>The files the peer offered.</summary>
    public IReadOnlyList<SlskdSearchFile> Files { get; init; } = [];

    /// <summary>The files the peer offered but keeps locked; only usable after the user is granted access.</summary>
    public IReadOnlyList<SlskdSearchFile> LockedFiles { get; init; } = [];
}

/// <summary>
/// One file inside a <see cref="SlskdSearchResponse"/>. Soulseek peers do not send reliable audio
/// attributes, so every one of them is optional.
/// </summary>
public sealed record SlskdSearchFile
{
    /// <summary>The peer's own path, with backslash separators.</summary>
    public string Filename { get; init; } = string.Empty;

    /// <summary>The file's size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>The extension the peer declared; empty in practice, so parse the filename instead.</summary>
    public string? Extension { get; init; }

    /// <summary>The bitrate in kbps, absent for lossless files.</summary>
    public int? BitRate { get; init; }

    /// <summary>The bit depth, for lossless files.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The sample rate in Hz, for lossless files.</summary>
    public int? SampleRate { get; init; }

    /// <summary>The length in seconds.</summary>
    public int? Length { get; init; }

    /// <summary>Whether the peer says the file is variable bitrate; often absent.</summary>
    public bool? IsVariableBitRate { get; init; }

    /// <summary>Whether the file sits behind the peer's private share.</summary>
    public bool IsLocked { get; init; }
}