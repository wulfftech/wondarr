using System.Text.Json;

namespace Wondarr.Core.Notifications;

/// <summary>One event held back for a digest, with the time it happened.</summary>
/// <param name="Message">The message as it was when the event happened.</param>
/// <param name="At">When the event was stored.</param>
public sealed record DigestEntry(NotificationMessage Message, DateTimeOffset At);

/// <summary>
/// A provider that can hold some events back and send them together. The dispatcher asks it whether to
/// hold a message back; <see cref="NotificationDigestService"/> sends what was held once the window ends.
/// </summary>
public interface IDigestNotificationProvider : INotificationProvider
{
    /// <summary>Gets whether a notification with these settings holds this event back for a digest.</summary>
    /// <param name="settings">The notification's settings.</param>
    /// <param name="eventName">One of the <see cref="NotificationEventNames"/> values.</param>
    bool Batches(JsonElement settings, string eventName);

    /// <summary>Gets how long a window lasts, or <see langword="null"/> when the settings are not in digest mode.</summary>
    /// <param name="settings">The notification's settings.</param>
    TimeSpan? DigestWindow(JsonElement settings);

    /// <summary>Sends held-back events as one message.</summary>
    /// <param name="entries">The events, oldest first.</param>
    /// <param name="windowStart">When the window began (its first entry).</param>
    /// <param name="windowEnd">When the window ended (now).</param>
    /// <param name="zone">The time zone the message shows times in.</param>
    /// <param name="settings">The notification's settings.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendDigestAsync(
        IReadOnlyList<DigestEntry> entries,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeZoneInfo zone,
        JsonElement settings,
        CancellationToken cancellationToken);
}
