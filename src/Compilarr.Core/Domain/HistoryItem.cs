using Compilarr.Core.Persistence;

namespace Compilarr.Core.Domain;

/// <summary>
/// What happened to a song. Stored as a string so the rows stay readable and renumbering the enum
/// cannot rewrite history (ARCHITECTURE §5.4).
/// </summary>
public enum HistoryEventType
{
    /// <summary>A candidate was handed to a download client.</summary>
    Grabbed,

    /// <summary>A file was imported and satisfied the song.</summary>
    Imported,

    /// <summary>A better file replaced the one already held.</summary>
    Upgraded,

    /// <summary>A grab or an import failed.</summary>
    Failed,

    /// <summary>An imported file passed duration and fingerprint verification.</summary>
    Verified,

    /// <summary>A candidate was rejected during verification and went to the blocklist.</summary>
    Rejected,

    /// <summary>A file was removed.</summary>
    Deleted,

    /// <summary>Files were moved or renamed.</summary>
    Renamed,
}

/// <summary>
/// One event in a song's lifecycle: what happened, when, and the JSON payload the caller stored.
/// Stored in the <c>history</c> table. The event time is <see cref="EntityBase.CreatedAt"/>.
/// </summary>
public sealed class HistoryItem : EntityBase
{
    /// <summary>Gets or sets the song the event is about.</summary>
    public long SongId { get; set; }

    /// <summary>Gets or sets what happened.</summary>
    public HistoryEventType EventType { get; set; }

    /// <summary>Gets or sets the source instance the grab went through, or <see langword="null"/> (no FK yet).</summary>
    public long? SourceInstanceId { get; set; }

    /// <summary>Gets or sets the quality the event involved, or <see langword="null"/>.</summary>
    public long? QualityId { get; set; }

    /// <summary>Gets or sets the event's payload as JSON text, an empty object when there is none.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>Gets or sets the song the event is about.</summary>
    public Song Song { get; set; } = null!;

    /// <summary>Gets or sets the quality the event involved, or <see langword="null"/>.</summary>
    public Quality? Quality { get; set; }
}
