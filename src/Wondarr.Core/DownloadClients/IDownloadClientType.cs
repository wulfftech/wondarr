using System.Text.Json;
using Wondarr.Core.Notifications;
using Wondarr.Core.Sources;

namespace Wondarr.Core.DownloadClients;

/// <summary>
/// One kind of download client. Implementations are registered as singletons and are stateless:
/// everything a validation or a connection test needs arrives in its arguments. The concrete types
/// arrive with the source projects (P7-05, P7-06).
/// </summary>
public interface IDownloadClientType
{
    /// <summary>Gets the type name stored in <see cref="Domain.DownloadClient.Type"/>, for example <c>qbittorrent</c>.</summary>
    string Type { get; }

    /// <summary>Gets the protocol every client of this type serves; a client type never serves both.</summary>
    DownloadProtocol Protocol { get; }

    /// <summary>Gets the fields of the settings form the UI renders.</summary>
    IReadOnlyList<NotificationField> Fields { get; }

    /// <summary>
    /// Checks a settings object. Returns the human messages to show the user, empty when the settings
    /// are usable.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    IReadOnlyList<string> Validate(JsonElement settings);

    /// <summary>Connects with the settings and reports whether the far end answered.</summary>
    /// <param name="settings">The settings to connect with.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<ProviderTestResult> TestAsync(JsonElement settings, CancellationToken cancellationToken);
}
