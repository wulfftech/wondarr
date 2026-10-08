using System.Text.Json;
using System.Text.Json.Nodes;
using Wondarr.Core.Indexers;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Api.Indexers;

/// <summary>
/// One indexer as the API hands it out. <see cref="Settings"/> never carries a stored secret: a
/// secret field with a value reads back as <c>"********"</c>.
/// </summary>
/// <param name="Id">The indexer id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Type">The indexer type's name.</param>
/// <param name="Protocol">How this indexer's releases are downloaded.</param>
/// <param name="Enabled">Whether searches ask this indexer.</param>
/// <param name="Priority">The ask order; a lower number is asked first.</param>
/// <param name="DownloadClientId">The client this indexer's grabs go to, or <see langword="null"/>.</param>
/// <param name="Settings">The indexer type's settings, with every secret masked.</param>
public sealed record IndexerResource(
    long Id,
    string Name,
    string Type,
    string Protocol,
    bool Enabled,
    int Priority,
    long? DownloadClientId,
    JsonNode? Settings);

/// <summary>
/// The body of a create, an update or a test. <see cref="Id"/> is only read by the test endpoint: it
/// names the row a masked secret should be kept from.
/// </summary>
/// <param name="Name">The display name.</param>
/// <param name="Type">The indexer type's name.</param>
/// <param name="Protocol">The protocol the row chooses, when its type serves both.</param>
/// <param name="Enabled">Whether searches ask this indexer; left out, it is <see langword="true"/>, as a new indexer is.</param>
/// <param name="Priority">The ask order; left out, it is 25.</param>
/// <param name="DownloadClientId">The client this indexer's grabs go to, or <see langword="null"/>.</param>
/// <param name="Settings">The indexer type's settings.</param>
/// <param name="Id">The row a test is about, or <see langword="null"/>.</param>
public sealed record IndexerInputResource(
    string? Name,
    string? Type,
    string? Protocol,
    bool? Enabled,
    int? Priority,
    long? DownloadClientId,
    JsonNode? Settings,
    long? Id = null);

/// <summary>One field of an indexer type's settings form, as the UI needs it.</summary>
/// <param name="Name">The settings key.</param>
/// <param name="Label">What the form calls it.</param>
/// <param name="Type">The control to render.</param>
/// <param name="Required">Whether the indexer is unusable without it.</param>
/// <param name="HelpText">A sentence shown under the control, or <see langword="null"/>.</param>
/// <param name="Options">The choices of a select, or <see langword="null"/>.</param>
/// <param name="Secret">Whether the value is masked on read.</param>
/// <param name="Advanced">Whether the control belongs in the advanced section.</param>
public sealed record IndexerFieldResource(
    string Name,
    string Label,
    string Type,
    bool Required,
    string? HelpText,
    IReadOnlyList<string>? Options,
    bool Secret,
    bool Advanced);

/// <summary>
/// One registered indexer type and the form it wants. <see cref="Protocol"/> is the type's fixed
/// protocol, or <see langword="null"/> when <see cref="ProtocolChoosable"/> says the row chooses.
/// </summary>
/// <param name="Type">The indexer type's name.</param>
/// <param name="Protocol">The protocol every indexer of this type serves, or <see langword="null"/>.</param>
/// <param name="ProtocolChoosable">Whether the row chooses the protocol.</param>
/// <param name="Fields">The fields of its settings form.</param>
public sealed record IndexerSchemaResource(
    string Type,
    string? Protocol,
    bool ProtocolChoosable,
    IReadOnlyList<IndexerFieldResource> Fields);

/// <summary>Turns stored rows and request bodies into the resources above.</summary>
internal static class IndexerResourceMapper
{
    /// <summary>Maps a row, masking every secret field.</summary>
    /// <param name="indexer">The stored row.</param>
    /// <param name="type">The indexer type it names, or <see langword="null"/> when that type is gone.</param>
    public static IndexerResource ToResource(Core.Domain.Indexer indexer, IIndexerType? type)
    {
        ArgumentNullException.ThrowIfNull(indexer);

        var settings = NotificationSecrets.Masked(
            NotificationSecrets.Read(indexer.Settings),
            type?.Fields ?? []);

        return new IndexerResource(
            indexer.Id,
            indexer.Name,
            indexer.Type,
            indexer.Protocol.ToString(),
            indexer.Enabled,
            indexer.Priority,
            indexer.DownloadClientId,
            settings);
    }

    /// <summary>Maps a request body into the draft the service stores.</summary>
    /// <param name="resource">The body.</param>
    public static IndexerDraft ToDraft(IndexerInputResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new IndexerDraft(
            resource.Name ?? string.Empty,
            resource.Type ?? string.Empty,
            ParseProtocol(resource.Protocol),
            resource.Enabled ?? true,
            resource.Priority ?? 25,
            resource.DownloadClientId,
            ToElement(resource.Settings),
            resource.Id);
    }

    /// <summary>
    /// Reads a protocol name. Absent or empty is <see langword="null"/> (the service asks for one when the
    /// type needs it); a name the enum does not know is a validation error, not "absent".
    /// </summary>
    /// <param name="protocol">The protocol name, or <see langword="null"/>.</param>
    public static DownloadProtocol? ParseProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return null;
        }

        return Enum.TryParse<DownloadProtocol>(protocol, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new IndexerValidationException(
                "protocol",
                string.Concat("Unknown protocol '", protocol, "'; use Torrent or Usenet."));
    }

    /// <summary>Converts the body's settings node into an element the type can read.</summary>
    /// <param name="settings">The node, or <see langword="null"/>.</param>
    public static JsonElement ToElement(JsonNode? settings) =>
        settings is null
            ? NotificationSecrets.Read(null)
            : NotificationSecrets.Read(settings.ToJsonString());
}

/// <summary>What a connection test of an indexer or a download client answers.</summary>
/// <param name="Success">Whether the connection worked.</param>
/// <param name="Error">Why it did not, in words that carry no secret.</param>
public sealed record ProviderTestResource(bool Success, string? Error);
