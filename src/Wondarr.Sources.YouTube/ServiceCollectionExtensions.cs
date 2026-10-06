using System.Net;
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
    /// Adds the YouTube Music search client and the yt-dlp runner.
    /// </summary>
    public static IServiceCollection AddWondarrYouTube(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<YouTubeOptions>().ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<YouTubeOptions>, YouTubeOptionsValidator>();

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

        return services;
    }
}
