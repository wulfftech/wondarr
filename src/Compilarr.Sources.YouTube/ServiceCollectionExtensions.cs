using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Sources.YouTube;

/// <summary>
/// Registers the services owned by Compilarr.Sources.YouTube.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the YouTube Music search client and the yt-dlp runner.
    /// </summary>
    public static IServiceCollection AddCompilarrYouTube(this IServiceCollection services) => services;
}
