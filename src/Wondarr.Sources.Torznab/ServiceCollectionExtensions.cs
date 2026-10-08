using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.DownloadClients;
using Wondarr.Sources.Torznab.Clients;

namespace Wondarr.Sources.Torznab;

/// <summary>
/// Registers the services owned by Wondarr.Sources.Torznab.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Torznab/Newznab indexers and the qBittorrent and SABnzbd download clients.
    /// </summary>
    public static IServiceCollection AddWondarrTorznab(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The proxy talks to whatever host each client row names, so it builds its own URLs and only
        // borrows the factory's handler pool. No retry policy: a wrong password or an unreachable
        // client must surface at once, not after a silent retry loop.
        services.AddHttpClient(QBittorrentProxy.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        // The proxy owns one session (cookie and version) per client row, so it is a singleton; the
        // client and the type over it are stateless.
        services.AddSingleton<QBittorrentProxy>();
        services.AddSingleton<ITorrentClient, QBittorrentClient>();
        services.AddSingleton<IDownloadClientType, QBittorrentClientType>();

        return services;
    }
}
