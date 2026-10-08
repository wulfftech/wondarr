using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Api.DownloadClients;

/// <summary>
/// One download client as the API hands it out. <see cref="Settings"/> never carries a stored
/// secret: a secret field with a value reads back as <c>"********"</c>.
/// </summary>
/// <param name="Id">The download client id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Type">The client type's name.</param>
/// <param name="Protocol">What this client downloads.</param>
/// <param name="Enabled">Whether grabs are sent to this client.</param>
/// <param name="Priority">The pick order; a lower number is picked first.</param>
/// <param name="Settings">The client type's settings, with every secret masked.</param>
public sealed record DownloadClientResource(
    long Id,
    string Name,
    string Type,
    string Protocol,
    bool Enabled,
    int Priority,
    JsonNode? Settings);

/// <summary>
/// The body of a create, an update or a test. <see cref="Id"/> is only read by the test endpoint: it
/// names the row a masked secret should be kept from.
/// </summary>
/// <param name="Name">The display name.</param>
/// <param name="Type">The client type's name.</param>
/// <param name="Enabled">Whether grabs are sent to this client; left out, it is <see langword="true"/>, as a new client is.</param>
/// <param name="Priority">The pick order; left out, it is 1.</param>
/// <param name="Settings">The client type's settings.</param>
/// <param name="Id">The row a test is about, or <see langword="null"/>.</param>
public sealed record DownloadClientInputResource(
    string? Name,
    string? Type,
    bool? Enabled,
    int? Priority,
    JsonNode? Settings,
    long? Id = null);

/// <summary>One field of a client type's settings form, as the UI needs it.</summary>
/// <param name="Name">The settings key.</param>
/// <param name="Label">What the form calls it.</param>
/// <param name="Type">The control to render.</param>
/// <param name="Required">Whether the client is unusable without it.</param>
/// <param name="HelpText">A sentence shown under the control, or <see langword="null"/>.</param>
/// <param name="Options">The choices of a select, or <see langword="null"/>.</param>
/// <param name="Secret">Whether the value is masked on read.</param>
/// <param name="Advanced">Whether the control belongs in the advanced section.</param>
public sealed record DownloadClientFieldResource(
    string Name,
    string Label,
    string Type,
    bool Required,
    string? HelpText,
    IReadOnlyList<string>? Options,
    bool Secret,
    bool Advanced);

/// <summary>One registered download client type and the form it wants.</summary>
/// <param name="Type">The client type's name.</param>
/// <param name="Protocol">The protocol every client of this type serves.</param>
/// <param name="Fields">The fields of its settings form.</param>
public sealed record DownloadClientSchemaResource(
    string Type,
    string Protocol,
    IReadOnlyList<DownloadClientFieldResource> Fields);

/// <summary>Turns stored rows and request bodies into the resources above.</summary>
internal static class DownloadClientResourceMapper
{
    /// <summary>Maps a row, masking every secret field.</summary>
    /// <param name="client">The stored row.</param>
    /// <param name="type">The client type it names, or <see langword="null"/> when that type is gone.</param>
    public static DownloadClientResource ToResource(Core.Domain.DownloadClient client, IDownloadClientType? type)
    {
        ArgumentNullException.ThrowIfNull(client);

        var settings = NotificationSecrets.Masked(
            NotificationSecrets.Read(client.Settings),
            type?.Fields ?? []);

        return new DownloadClientResource(
            client.Id,
            client.Name,
            client.Type,
            client.Protocol.ToString(),
            client.Enabled,
            client.Priority,
            settings);
    }

    /// <summary>Maps a request body into the draft the service stores.</summary>
    /// <param name="resource">The body.</param>
    public static DownloadClientDraft ToDraft(DownloadClientInputResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new DownloadClientDraft(
            resource.Name ?? string.Empty,
            resource.Type ?? string.Empty,
            resource.Enabled ?? true,
            resource.Priority ?? 1,
            ToElement(resource.Settings),
            resource.Id);
    }

    /// <summary>Converts the body's settings node into an element the type can read.</summary>
    /// <param name="settings">The node, or <see langword="null"/>.</param>
    public static JsonElement ToElement(JsonNode? settings) =>
        settings is null
            ? NotificationSecrets.Read(null)
            : NotificationSecrets.Read(settings.ToJsonString());
}
