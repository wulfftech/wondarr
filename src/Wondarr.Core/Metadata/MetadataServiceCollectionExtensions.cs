using System.Net;
using System.Reflection;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Metadata.ITunes;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata.MusicBrainz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Wondarr.Core.Metadata;

/// <summary>Registers the services owned by the metadata namespace.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The key the MusicBrainz request-spacing gate is registered under.</summary>
    public const string MusicBrainzGateKey = "musicbrainz";

    /// <summary>The key the Cover Art Archive request-spacing gate is registered under.</summary>
    public const string CoverArtArchiveGateKey = "coverartarchive";

    /// <summary>The key the Deezer request-spacing gate is registered under.</summary>
    public const string DeezerGateKey = "deezer";

    /// <summary>The key the iTunes request-spacing gate is registered under.</summary>
    public const string ITunesGateKey = "itunes";

    /// <summary>The key the AcoustID request-spacing gate is registered under.</summary>
    public const string AcoustIdGateKey = "acoustid";

    /// <summary>How long one AcoustID request may take before the lookup is reported as unavailable.</summary>
    private static readonly TimeSpan AcoustIdTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long one Deezer request is spaced from the next: 50 requests per 5 seconds.</summary>
    private static readonly TimeSpan DeezerInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>How long one Cover Art Archive request is spaced from the next. Undocumented, so polite.</summary>
    private static readonly TimeSpan CoverArtArchiveInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long one iTunes request is spaced from the next: about 20 calls per minute.</summary>
    private static readonly TimeSpan ITunesInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Adds the metadata options, the response cache, the per-host request-spacing gates and the
    /// MusicBrainz, Cover Art Archive, Deezer and iTunes clients, plus the cover-art chain.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Configuration to bind the <c>metadata</c> section from.</param>
    public static IServiceCollection AddWondarrMetadata(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<MetadataOptions>()
            .Bind(configuration.GetSection("Metadata"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<MetadataOptions>, MetadataOptionsValidator>();
        services.AddSingleton<IPostConfigureOptions<MetadataOptions>, MetadataOptionsPostConfigure>();

        // The media tools section is bound here rather than in AddWondarrCore because that method takes
        // no IConfiguration; MediaToolsOptions is registered exactly like MetadataOptions above.
        services.AddOptions<MediaToolsOptions>()
            .Bind(configuration.GetSection("Media"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<MediaToolsOptions>, MediaToolsOptionsValidator>();

        services.AddOptions<AcoustIdOptions>()
            .Bind(configuration.GetSection("AcoustId"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AcoustIdOptions>, AcoustIdOptionsValidator>();

        // The post-configure also hands the client key to the secret registry, so the redactor knows it.
        services.AddSingleton<IPostConfigureOptions<AcoustIdOptions>, AcoustIdOptionsPostConfigure>();

        services.AddSingleton<IMetadataCache, MetadataCache>();

        // Keyed, so every HttpClient the factory builds and every handler rotation share one gate
        // per host: MusicBrainz allows one request per second across the whole process.
        services.AddKeyedSingleton(MusicBrainzGateKey, (serviceProvider, _) => new RequestSpacingGate(
            serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value.MusicBrainzRequestInterval,
            serviceProvider.GetRequiredService<TimeProvider>()));

        var musicBrainz = services.AddHttpClient<IMusicBrainzClient, MusicBrainzClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            client.BaseAddress = new Uri(options.MusicBrainzBaseUrl, UriKind.Absolute);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildUserAgent(options));
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        });

        musicBrainz.AddResilienceHandler("musicbrainz", (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,

                // MusicBrainz answers 503 when throttled; when it sends Retry-After, obey it.
                ShouldRetryAfterHeader = true,
            });
        });

        // Registered after the retry pipeline, and therefore inside it: every attempt, the retries
        // included, waits for its own slot.
        musicBrainz.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(MusicBrainzGateKey)));

        services.AddKeyedSingleton(CoverArtArchiveGateKey, (serviceProvider, _) => new RequestSpacingGate(
            CoverArtArchiveInterval,
            serviceProvider.GetRequiredService<TimeProvider>()));

        services.AddKeyedSingleton(DeezerGateKey, (serviceProvider, _) => new RequestSpacingGate(
            DeezerInterval,
            serviceProvider.GetRequiredService<TimeProvider>()));

        services.AddKeyedSingleton(ITunesGateKey, (serviceProvider, _) => new RequestSpacingGate(
            ITunesInterval,
            serviceProvider.GetRequiredService<TimeProvider>()));

        // AcoustID allows three requests per second per client key, retries included. The interval
        // comes from the options, whose validator refuses anything above that ceiling.
        services.AddKeyedSingleton(AcoustIdGateKey, (serviceProvider, _) => new RequestSpacingGate(
            serviceProvider.GetRequiredService<IOptions<AcoustIdOptions>>().Value.RequestInterval,
            serviceProvider.GetRequiredService<TimeProvider>()));

        var acoustId = services.AddHttpClient<IAcoustIdClient, AcoustIdClient>((serviceProvider, client) =>
        {
            var acoustIdOptions = serviceProvider.GetRequiredService<IOptions<AcoustIdOptions>>().Value;

            client.BaseAddress = new Uri(acoustIdOptions.BaseUrl, UriKind.Absolute);
            client.Timeout = AcoustIdTimeout;

            // The same identifying User-Agent MusicBrainz gets; AcoustID asks for one too.
            AddProviderHeaders(client, serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value);
        });

        // No retry pipeline: the client retries a throttle itself, because it has to read the error
        // code out of the body to tell a rate limit from a failure.
        acoustId.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(AcoustIdGateKey)));

        var coverArtArchive = services.AddHttpClient<ICoverArtArchiveClient, CoverArtArchiveClient>(
            (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

                client.BaseAddress = new Uri(options.CoverArtArchiveBaseUrl, UriKind.Absolute);
                AddProviderHeaders(client, options);
            });

        // The front-image endpoint answers 307 and the 307 is the point: it is the stable URL. The
        // archive.org copy it points at changes whenever the storage layout or a merge does.
        coverArtArchive.ConfigurePrimaryHttpMessageHandler(
            () => new HttpClientHandler { AllowAutoRedirect = false });

        coverArtArchive.AddResilienceHandler("coverartarchive", (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                ShouldRetryAfterHeader = true,
            });
        });

        coverArtArchive.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(CoverArtArchiveGateKey)));

        var deezer = services.AddHttpClient<IDeezerClient, DeezerClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            client.BaseAddress = new Uri(options.DeezerBaseUrl, UriKind.Absolute);
            AddProviderHeaders(client, options);
        });

        deezer.AddResilienceHandler("deezer", (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,

                // The quota handler below turns a code-4 body into a 429 with a Retry-After; obey it.
                ShouldRetryAfterHeader = true,
            });
        });

        deezer.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(DeezerGateKey)));

        // Innermost, below the spacing handler: a retried attempt is spaced first, then inspected.
        deezer.AddHttpMessageHandler(() => new DeezerQuotaHandler());

        var itunes = services.AddHttpClient<IITunesClient, ITunesClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            client.BaseAddress = new Uri(options.ITunesBaseUrl, UriKind.Absolute);
            AddProviderHeaders(client, options);
        });

        itunes.AddResilienceHandler("itunes", (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value;

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                ShouldRetryAfterHeader = true,

                // iTunes answers 403 once the caller goes over ~20 requests a minute. That is a rate
                // limit, not a blip: retrying it only spends more of the budget, so it surfaces at once.
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null
                    || (args.Outcome.Result is { } response
                        && response.StatusCode != HttpStatusCode.Forbidden
                        && IsRetryable(response.StatusCode))),
            });
        });

        itunes.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(ITunesGateKey)));

        // Transient, not singleton: it takes the transient clients, and it holds no state of its own.
        services.AddTransient<ICoverArtResolver, CoverArtResolver>();

        // Likewise transient: the identity resolver reads the clients and keeps nothing between calls.
        services.AddTransient<IIdentityResolver, IdentityResolver>();

        return services;
    }

    /// <summary>Sends every provider the same identifying headers.</summary>
    private static void AddProviderHeaders(HttpClient client, MetadataOptions options)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildUserAgent(options));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    /// <summary>Whether a status is the kind worth another attempt.</summary>
    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    /// <summary>
    /// Builds the User-Agent MusicBrainz asks for: application, version and contact URL.
    /// </summary>
    private static string BuildUserAgent(MetadataOptions options)
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        // Informational versions carry the source revision after a '+'; that is build noise here.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        return $"Wondarr/{version} ( {options.ContactUrl} )";
    }
}
