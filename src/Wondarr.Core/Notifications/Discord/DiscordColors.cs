// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Discord/DiscordColors.cs, GPL-3.0.
// Adapted for Wondarr: no change beyond the namespace.

namespace Wondarr.Core.Notifications.Discord;

/// <summary>
/// The embed colours Discord accepts, as the RGB integers Lidarr uses.
/// </summary>
internal enum DiscordColors
{
    /// <summary>Red, for a failure.</summary>
    Danger = 15749200,

    /// <summary>Green, for an import or an upgrade.</summary>
    Success = 2605644,

    /// <summary>Orange, for a health check at warning level.</summary>
    Warning = 16753920,

    /// <summary>Blue, for a grab, a test or anything else.</summary>
    Standard = 16761392,

    /// <summary>Teal, Lidarr's own upgrade colour.</summary>
    Upgrade = 4089856,
}
