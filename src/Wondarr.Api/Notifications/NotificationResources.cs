using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Core.Notifications;

namespace Wondarr.Api.Notifications;

/// <summary>
/// One notification as the API hands it out. <see cref="Settings"/> never carries a stored secret:
/// a secret field with a value reads back as <c>"********"</c>.
/// </summary>
/// <param name="Id">The notification id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Implementation">The provider's implementation name.</param>
/// <param name="Enabled">Whether the dispatcher sends it.</param>
/// <param name="Events">The subscribed event names.</param>
/// <param name="Settings">The provider's settings, with every secret masked.</param>
public sealed record NotificationResource(
    long Id,
    string Name,
    string Implementation,
    bool Enabled,
    IReadOnlyList<string> Events,
    JsonNode? Settings);

/// <summary>
/// The body of a create, an update or a test. <see cref="Id"/> is only read by the test endpoint: it
/// names the row a masked secret should be kept from.
/// </summary>
/// <param name="Name">The display name.</param>
/// <param name="Implementation">The provider's implementation name.</param>
/// <param name="Enabled">Whether the dispatcher sends it.</param>
/// <param name="Events">The subscribed event names.</param>
/// <param name="Settings">The provider's settings.</param>
/// <param name="Id">The row a test is about, or <see langword="null"/>.</param>
public sealed record NotificationInputResource(
    string? Name,
    string? Implementation,
    bool Enabled,
    IReadOnlyList<string>? Events,
    JsonNode? Settings,
    long? Id = null);

/// <summary>One field of a provider's settings form, as the UI needs it.</summary>
/// <param name="Name">The settings key.</param>
/// <param name="Label">What the form calls it.</param>
/// <param name="Type">The control to render.</param>
/// <param name="Required">Whether the notification is unusable without it.</param>
/// <param name="HelpText">A sentence shown under the control, or <see langword="null"/>.</param>
/// <param name="Options">The choices of a select, or <see langword="null"/>.</param>
/// <param name="Secret">Whether the value is masked on read.</param>
/// <param name="Advanced">Whether the control belongs in the advanced section.</param>
public sealed record NotificationFieldResource(
    string Name,
    string Label,
    string Type,
    bool Required,
    string? HelpText,
    IReadOnlyList<string>? Options,
    bool Secret,
    bool Advanced);

/// <summary>One registered provider and the form it wants.</summary>
/// <param name="Implementation">The provider's implementation name.</param>
/// <param name="Fields">The fields of its settings form.</param>
public sealed record NotificationSchemaResource(string Implementation, IReadOnlyList<NotificationFieldResource> Fields);

/// <summary>Turns stored rows and request bodies into the resources above.</summary>
internal static class NotificationResourceMapper
{
    /// <summary>Maps a row, masking every secret field.</summary>
    /// <param name="notification">The stored row.</param>
    /// <param name="provider">The provider it names, or <see langword="null"/> when that provider is gone.</param>
    public static NotificationResource ToResource(
        Core.Domain.Notification notification,
        INotificationProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var settings = NotificationSecrets.Masked(
            NotificationSecrets.Read(notification.Settings),
            provider?.Fields ?? []);

        return new NotificationResource(
            notification.Id,
            notification.Name,
            notification.Type,
            notification.Enabled,
            Events(notification.Events),
            settings);
    }

    /// <summary>Maps a request body into the draft the service stores.</summary>
    /// <param name="resource">The body.</param>
    public static NotificationDraft ToDraft(NotificationInputResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new NotificationDraft(
            resource.Name ?? string.Empty,
            resource.Implementation ?? string.Empty,
            resource.Enabled,
            resource.Events ?? [],
            ToElement(resource.Settings),
            resource.Id);
    }

    /// <summary>Reads the stored subscription list; an unreadable column subscribes to nothing.</summary>
    /// <param name="json">The column's text.</param>
    public static IReadOnlyList<string> Events(string? json)
    {
        var events = NotificationSecrets.Read(json);

        if (events.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. events.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()!)
            .Where(name => !string.IsNullOrEmpty(name))];
    }

    /// <summary>Converts the body's settings node into an element the provider can read.</summary>
    /// <param name="settings">The node, or <see langword="null"/>.</param>
    public static JsonElement ToElement(JsonNode? settings) =>
        settings is null
            ? NotificationSecrets.Read(null)
            : NotificationSecrets.Read(settings.ToJsonString());
}
