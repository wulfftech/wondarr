using System.Net;
using Wondarr.Core.Indexers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Wondarr.Sources.Torznab.Indexers;

namespace Wondarr.Sources.Torznab;

/// <summary>
/// Registers the services owned by Wondarr.Sources.Torznab.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Torznab and Newznab indexers: the two indexer types, their clients and the factory
    /// that chooses one, the caps reader with its 24-hour cache, and the named HTTP client every
    /// request goes through.
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

        return services;
    }
}
