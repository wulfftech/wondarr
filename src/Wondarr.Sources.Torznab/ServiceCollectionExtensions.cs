using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Sources.Torznab;

/// <summary>
/// Registers the services owned by Wondarr.Sources.Torznab.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Torznab/Newznab indexers and the qBittorrent and SABnzbd download clients.
    /// </summary>
    public static IServiceCollection AddWondarrTorznab(this IServiceCollection services) => services;
}
