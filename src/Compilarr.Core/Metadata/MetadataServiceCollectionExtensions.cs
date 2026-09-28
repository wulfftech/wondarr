using System.Reflection;
using Compilarr.Core.Metadata.Http;
using Compilarr.Core.Metadata.MusicBrainz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Compilarr.Core.Metadata;

/// <summary>Registers the services owned by the metadata namespace.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The key the MusicBrainz request-spacing gate is registered under.</summary>
    public const string MusicBrainzGateKey = "musicbrainz";

    /// <summary>
    /// Adds the metadata options, the response cache, the per-host request-spacing gate and the
    /// MusicBrainz client.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Configuration to bind the <c>metadata</c> section from.</param>
    public static IServiceCollection AddCompilarrMetadata(
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

        services.AddSingleton<IMetadataCache, MetadataCache>();

        // Keyed, so every HttpClient the factory builds and every handler rotation share one gate
        // per host: MusicBrainz allows one request per second across the whole process.
        services.AddKeyedSingleton(MusicBrainzGateKey, (serviceProvider, _) => new RequestSpacingGate(
            TimeSpan.FromSeconds(1),
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

        return services;
    }

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

        return $"Compilarr/{version} ( {options.ContactUrl} )";
    }
}