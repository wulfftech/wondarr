using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Http;

namespace Wondarr.Core.Lyrics;

/// <summary>Registers the services owned by the lyrics namespace.</summary>
public static class LyricsServiceCollectionExtensions
{
    /// <summary>The key the LRCLIB request-spacing gate is registered under.</summary>
    public const string LrclibGateKey = "lrclib";

    /// <summary>
    /// Adds the LRCLIB options and client. There is no retry pipeline: a throttled or failing LRCLIB is
    /// simply "no lyrics this time", so the only politeness is the spacing gate.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Configuration to bind the <c>lyrics</c> section from.</param>
    public static IServiceCollection AddWondarrLyrics(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LyricsOptions>()
            .Bind(configuration.GetSection("Lyrics"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<LyricsOptions>, LyricsOptionsValidator>();

        // Keyed, so every client the factory builds and every handler rotation share one gate: LRCLIB
        // gets the configured gap across the whole process, however many imports run at once.
        services.AddKeyedSingleton(LrclibGateKey, (serviceProvider, _) => new RequestSpacingGate(
            TimeSpan.FromMilliseconds(
                serviceProvider.GetRequiredService<IOptions<LyricsOptions>>().Value.RequestIntervalMs),
            serviceProvider.GetRequiredService<TimeProvider>()));

        var lyrics = services.AddHttpClient<ILrclibClient, LrclibClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<LyricsOptions>>().Value;

            client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);

            // Bounds what a slow LRCLIB can cost an import; the import is never failed by the wait.
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            // The same identifying User-Agent the metadata providers get: LRCLIB asks callers to say
            // who they are.
            Metadata.ServiceCollectionExtensions.AddProviderHeaders(
                client,
                serviceProvider.GetRequiredService<IOptions<MetadataOptions>>().Value);
        });

        lyrics.AddHttpMessageHandler(serviceProvider => new RequestSpacingHandler(
            serviceProvider.GetRequiredKeyedService<RequestSpacingGate>(LrclibGateKey)));

        return services;
    }
}
