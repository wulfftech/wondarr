using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One configured notification (ARCHITECTURE §5.7): a provider, its settings and the events the user
/// wants to hear about. Stored in the <c>notification</c> table. The settings and the event list are
/// JSON text columns so a new provider needs no migration.
/// </summary>
public sealed class Notification : EntityBase
{
    /// <summary>Gets or sets the display name the user gave this notification.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's implementation name, for example <c>Webhook</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's settings as a JSON object.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Gets or sets the subscribed event names as a JSON array of <c>NotificationEventNames</c> values.</summary>
    public string Events { get; set; } = "[]";

    /// <summary>Gets or sets a value indicating whether the dispatcher sends this notification.</summary>
    public bool Enabled { get; set; } = true;
}
