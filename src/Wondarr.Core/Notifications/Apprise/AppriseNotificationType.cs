// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Apprise/AppriseNotificationType.cs, GPL-3.0.
// Adapted for Wondarr: the four enum members as the strings the wire body carries, so no
// `EnumMemberAttribute` and no serialiser setting is needed.

namespace Wondarr.Core.Notifications.Apprise;

/// <summary>
/// The severity an Apprise notification is tagged with. These are Apprise's own values, sent as text.
/// </summary>
internal static class AppriseNotificationTypes
{
    /// <summary>An informational message; the default.</summary>
    public const string Info = "info";

    /// <summary>A success.</summary>
    public const string Success = "success";

    /// <summary>A warning.</summary>
    public const string Warning = "warning";

    /// <summary>A failure.</summary>
    public const string Failure = "failure";

    /// <summary>Every type the settings form offers.</summary>
    public static readonly IReadOnlyList<string> All = [Info, Success, Warning, Failure];
}
