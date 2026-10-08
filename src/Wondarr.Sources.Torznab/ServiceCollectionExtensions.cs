using System.Net;
using Wondarr.Core.Indexers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Microsoft.Extensions.Logging;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;

namespace Wondarr.Sources.Torznab;

/// <summary>
/// Registers the services owned by Wondarr.Sources.Torznab.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Torznab and Newznab indexers (the two indexer types, their clients and the factory
    /// that chooses one, the caps reader with its 24-hour cache, and the named HTTP client every
    /// request goes through), the qBittorrent and SABnzbd download clients, and the torrent and usenet sources
    /// that search the indexers.
    /// </summary>
    public static IServiceCollection AddWondarrTorznab(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: a second call (a test, or a host that composes the sources itself) must not
        // duplicate the registrations.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IIndexerClientFactory)))
        {
            return services;
        }

        services.AddMemoryCache();

        // Redirects are followed by hand (IndexerHttp.GetAsync) so a download URL that points at a
        // magnet: URI can be handed back instead of followed, the way Prowlarr's proxy links do.
        services.AddHttpClient(IndexerHttp.ClientName, client =>
            {
                client.Timeout = IndexerHttp.RequestTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(IndexerHttp.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            })
            .AddResilienceHandler("indexer", builder => builder.AddRetry(new HttpRetryStrategyOptions
            {
                // Indexers, Prowlarr included, answer 502/503 when an upstream tracker hiccups; two
                // retries with jitter, and a Retry-After is obeyed.
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldRetryAfterHeader = true,
            }));

        services.AddSingleton<NewznabCapabilitiesReader>();
        services.AddSingleton<TorznabIndexerClient>();
        services.AddSingleton<NewznabIndexerClient>();
        services.AddSingleton<IIndexerClientFactory, IndexerClientFactory>();
        services.AddSingleton<IIndexerType, TorznabIndexerType>();
        services.AddSingleton<IIndexerType, NewznabIndexerType>();

        // The proxy talks to whatever host each client row names, so it builds its own URLs and only
        // borrows the factory's handler pool. No retry policy: a wrong password or an unreachable
        // client must surface at once, not after a silent retry loop.
        services.AddHttpClient(QBittorrentProxy.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        // The proxy owns one session (cookie and version) per client row, so it is a singleton; the
        // client and the type over it are stateless.
        services.AddSingleton<QBittorrentProxy>();
        services.AddSingleton<ITorrentClient, QBittorrentClient>();
        services.AddSingleton<IDownloadClientType, QBittorrentClientType>();

        // SABnzbd: the key travels in the query, so the client's own logging redacts the query and
        // the proxy logs the mode only. No retry policy, for the same reason as qBittorrent's.
        services.AddHttpClient(SabnzbdProxy.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<SabnzbdProxy>();
        services.AddSingleton<IUsenetClient, SabnzbdClient>();
        services.AddSingleton<IDownloadClientType, SabnzbdClientType>();

        // One source per protocol, tier 3 (DECISIONS build session 8 #7): torrents through the
        // Torznab indexers, usenet through the Newznab ones.
        services.AddSingleton<ISourceProvider>(provider => CreateSource(provider, DownloadProtocol.Torrent));
        services.AddSingleton<ISourceProvider>(provider => CreateSource(provider, DownloadProtocol.Usenet));

        return services;
    }

    private static IndexerSourceProvider CreateSource(IServiceProvider provider, DownloadProtocol protocol) =>
        new(
            protocol,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IIndexerClientFactory>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<IndexerSourceProvider>>());
}
