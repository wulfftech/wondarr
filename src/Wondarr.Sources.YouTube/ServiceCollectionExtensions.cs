using System.Net;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Registers the services owned by Wondarr.Sources.YouTube.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the YouTube Music search client, the yt-dlp runner, their options and the YouTube health
    /// check.
    /// </summary>
    public static IServiceCollection AddWondarrYouTube(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: a second call (a test, or a host that composes the sources itself) must not
        // duplicate the validators or the typed-client registration.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IInnertubeClient)))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);

        // Bound to the configuration root by the host once the settings API lands (P4-05); until then
        // the defaults apply and the validators still run.
        services.AddOptions<YouTubeOptions>().ValidateOnStart();

        // Both validators are registered against the bound YouTubeOptions type: the pipeline
        // validates the type it resolves, so an IValidateOptions<YtDlpOptions> would never be asked.
        services.AddSingleton<IValidateOptions<YouTubeOptions>, YouTubeOptionsValidator>();
        services.AddSingleton<IValidateOptions<YouTubeOptions>, YtDlpOptionsValidator>();

        // One search is one POST whose body is hundreds of kilobytes, so the headers are read first
        // and the body once. The consent cookie is set per request, so the handler must keep no cookie
        // jar of its own (one would drop the header), and decompression is on so the body is JSON.
        services.AddHttpClient<IInnertubeClient, InnertubeClient>(InnertubeClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
            });

        // IProcessRunner is registered by Wondarr.Core.
        services.AddSingleton<IYtDlpRunner, YtDlpRunner>();
        services.AddSingleton<YtDlpAvailability>();
        services.AddSingleton<IHealthCheck, YtDlpHealthCheck>();

        // The provider is a singleton, so the ISRC reader opens its own scope per call.
        services.AddSingleton<ISongIsrcSource, SongIsrcSource>();
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();

        return services;
    }
}
