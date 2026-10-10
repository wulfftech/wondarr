namespace Wondarr.Core.Notifications;

/// <summary>
/// The event names a notification may subscribe to, stored in
/// <see cref="Domain.Notification.Events"/> and sent to providers as the message's event.
/// </summary>
public static class NotificationEventNames
{
    /// <summary>A candidate was handed to a download source.</summary>
    public const string Grab = "grab";

    /// <summary>A downloaded file became a library file.</summary>
    public const string Import = "import";

    /// <summary>A better file replaced the one a song already had.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>A grab or an import failed.</summary>
    public const string Failure = "failure";

    /// <summary>A health check started failing.</summary>
    public const string Health = "health";

    /// <summary>A newer Wondarr release exists; sent once per new version, never for a development build.</summary>
    public const string Update = "update";

    /// <summary>The test message the API sends; never selectable, so it is not in <see cref="All"/>.</summary>
    public const string Test = "test";

    /// <summary>Every event a user may subscribe to.</summary>
    public static readonly IReadOnlyList<string> All = [Grab, Import, Upgrade, Failure, Health, Update];
}
