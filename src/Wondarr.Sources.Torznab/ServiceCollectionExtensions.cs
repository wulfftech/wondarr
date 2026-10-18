using System.Net;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Organizer;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Messaging;
using Wondarr.Core.Searching;
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

        // Gazelle's rate limit runs on the clock; the host registers one, a bare container gets the system's.
        services.TryAddSingleton(TimeProvider.System);

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
            .AddResilienceHandler("indexer", builder => builder.AddRetry(IndexerRetry()));

        // Gazelle has its own client with no retry handler (R-024): its limiter counts one turn per
        // request sent, so a retry hidden in the handler would send requests the limiter never saw.
        // A failed request surfaces at once; the next search is the retry.
        services.AddHttpClient(GazelleClient.HttpClientName, client =>
            {
                client.Timeout = IndexerHttp.RequestTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(IndexerHttp.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });

        services.AddSingleton<NewznabCapabilitiesReader>();
        services.AddSingleton<TorznabIndexerClient>();
        services.AddSingleton<NewznabIndexerClient>();
        services.AddSingleton<PushedReleaseClient>();
        services.AddSingleton<ProwlarrSearchClient>();
        services.AddSingleton<GazelleClient>();
        services.AddSingleton<IIndexerClientFactory, IndexerClientFactory>();
        services.AddSingleton<IIndexerType, TorznabIndexerType>();
        services.AddSingleton<IIndexerType, NewznabIndexerType>();
        services.AddSingleton<IIndexerType, ProwlarrIndexerType>();
        services.AddSingleton<IIndexerType, GazelleIndexerType>();

        // The proxy talks to whatever host each client row names, so it builds its own URLs and only
        // borrows the factory's handler pool. No retry policy: a wrong password or an unreachable
        // client must surface at once, not after a silent retry loop.
        services.AddHttpClient(QBittorrentProxy.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        // The proxy owns one session (cookie and version) per client row, so it is a singleton; the
        // client and the type over it are stateless.
        services.AddSingleton<QBittorrentProxy>();
        services.AddSingleton<IHandle<DownloadClientDeletedEvent>, SessionEvictor>();
        services.AddSingleton<ITorrentClient, QBittorrentClient>();
        services.AddSingleton<IDownloadClientType, QBittorrentClientType>();

        // SABnzbd: the key travels in the query, so the client's own logging redacts the query and
        // the proxy logs the mode only. No retry policy, for the same reason as qBittorrent's.
        services.AddHttpClient(SabnzbdProxy.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<SabnzbdProxy>();
        services.AddSingleton<IUsenetClient, SabnzbdClient>();
        services.AddSingleton<IDownloadClientType, SabnzbdClientType>();

        // The NZBs a search downloads for their file lists, kept so a grab does not download them
        // again: indexers cap NZB downloads per day. Process-wide, so every search and grab shares it.
        services.AddSingleton(provider => new NzbCache(provider.GetRequiredService<TimeProvider>()));

        // Which indexers refused NZB downloads and until when, so a later search does not try again.
        // Process-wide for the same reason; memory only, so a restart forgets.
        services.AddSingleton(provider => new NzbCooldowns(provider.GetRequiredService<TimeProvider>()));

        // One source per protocol, tier 3 (DECISIONS build session 8 #7): torrents through the
        // Torznab indexers, usenet through the Newznab ones.
        services.AddSingleton<ISourceProvider>(provider => CreateSource(provider, DownloadProtocol.Torrent));
        services.AddSingleton<ISourceProvider>(provider => CreateSource(provider, DownloadProtocol.Usenet));

        // release/push for torrents and NZBs runs in the request's scope, beside the search service.
        services.AddScoped<IReleasePushHandler, ReleasePushHandler>();

        return services;
    }

    private static IndexerSourceProvider CreateSource(IServiceProvider provider, DownloadProtocol protocol) =>
        new(
            protocol,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IIndexerClientFactory>(),
            provider.GetRequiredService<ITorrentClient>(),
            provider.GetRequiredService<IUsenetClient>(),
            provider.GetRequiredService<IDiskOperations>(),
            provider.GetRequiredService<IOptionsMonitor<ImportOptions>>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<IndexerSourceProvider>>(),
            budget: null,
            nzbCache: provider.GetRequiredService<NzbCache>(),
            secrets: provider.GetRequiredService<ISecretRegistry>(),
            search: provider.GetService<IOptionsMonitor<SearchOptions>>(),
            nzbCooldowns: provider.GetRequiredService<NzbCooldowns>());

    /// <summary>
    /// The retry of the named indexer client. Indexers, Prowlarr included, answer 502/503 when an upstream
    /// tracker hiccups; two retries with jitter, and a Retry-After is obeyed. An NZB download
    /// (<see cref="IndexerHttp.NzbDownload"/>) is held to less: a 429 is not retried (it is how an indexer
    /// says its daily quota is spent, and a retry is another doomed download), anything else is retried once,
    /// and it waits at most <see cref="IndexerHttp.NzbRetryMaxDelay"/> for it, however long a Retry-After
    /// asks, so the request never outlasts its timeout.
    /// </summary>
    private static HttpRetryStrategyOptions IndexerRetry()
    {
        var retry = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(2),
            ShouldRetryAfterHeader = true,
            ShouldHandle = args => new ValueTask<bool>(
                HttpClientResiliencePredicates.IsTransient(args.Outcome) && !IsNzbQuotaAnswer(args) && !IsNzbRetrySpent(args)),
        };

        // The generator the options come with reads the Retry-After header; an NZB download's delay is cut short.
        var retryAfter = retry.DelayGenerator;

        retry.DelayGenerator = async args =>
        {
            var delay = retryAfter is null ? null : await retryAfter(args).ConfigureAwait(false);

            return delay is { } asked && IsNzbDownload(args.Context)
                ? TimeSpan.FromTicks(Math.Min(asked.Ticks, IndexerHttp.NzbRetryMaxDelay.Ticks))
                : delay;
        };

        return retry;
    }

    /// <summary>Whether the request is an NZB download (<see cref="IndexerHttp.NzbDownload"/>).</summary>
    private static bool IsNzbDownload(ResilienceContext context) =>
        context.GetRequestMessage() is { } request
        && request.Options.TryGetValue(IndexerHttp.NzbDownload, out var nzbDownload)
        && nzbDownload;

    /// <summary>Whether the answer is a 429 to an NZB download.</summary>
    private static bool IsNzbQuotaAnswer(RetryPredicateArguments<HttpResponseMessage> args) =>
        args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests } && IsNzbDownload(args.Context);

    /// <summary>Whether the outcome is of an NZB download that has been retried already: it gets one retry.</summary>
    private static bool IsNzbRetrySpent(RetryPredicateArguments<HttpResponseMessage> args) =>
        args.AttemptNumber >= 1 && IsNzbDownload(args.Context);
}
