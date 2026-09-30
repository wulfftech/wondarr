// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Apprise/ApprisePayload.cs, GPL-3.0.
// Adapted for Wondarr: `System.Text.Json` camelCase names, an explicit `format` of `text`, and the
// stateless URLs written only when the notification has them.

using System.Text.Json.Serialization;

namespace Wondarr.Core.Notifications.Apprise;

/// <summary>
/// The body of one Apprise post. Apprise fans it out to whichever services the configuration or the
/// stateless URLs name.
/// </summary>
internal sealed record ApprisePayload
{
    /// <summary>The services to notify, when the notification sends statelessly; otherwise <see langword="null"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Urls { get; init; }

    /// <summary>The notification's title.</summary>
    public required string Title { get; init; }

    /// <summary>The notification's body.</summary>
    public required string Body { get; init; }

    /// <summary>One of <see cref="AppriseNotificationTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>How the body is to be read; Wondarr always sends plain text.</summary>
    public string Format { get; init; } = "text";

    /// <summary>The tags to notify, joined with commas, or <see langword="null"/> when none are set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; init; }
}
