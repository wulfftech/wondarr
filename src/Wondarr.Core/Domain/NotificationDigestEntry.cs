using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One event an email notification in digest mode is holding back until its window ends. Stored in the
/// <c>notification_digest_entry</c> table, so a restart loses nothing; deleted with its notification.
/// </summary>
public sealed class NotificationDigestEntry : EntityBase
{
    /// <summary>Gets or sets the notification the entry waits for.</summary>
    public long NotificationId { get; set; }

    /// <summary>Gets or sets the event name, one of <c>import</c>, <c>upgrade</c> or <c>failure</c>.</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>Gets or sets the song the event is about, or <see langword="null"/>.</summary>
    public long? SongId { get; set; }

    /// <summary>Gets or sets the message as it was when the event happened, as JSON.</summary>
    public string Snapshot { get; set; } = "{}";
}
