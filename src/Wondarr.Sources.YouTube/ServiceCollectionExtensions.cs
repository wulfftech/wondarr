using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Registers the services owned by Wondarr.Sources.YouTube.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the YouTube Music search client and the yt-dlp runner.
    /// </summary>
    public static IServiceCollection AddWondarrYouTube(this IServiceCollection services) => services;
}
