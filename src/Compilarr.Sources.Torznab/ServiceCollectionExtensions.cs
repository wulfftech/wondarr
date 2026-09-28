using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Sources.Torznab;

/// <summary>
/// Registers the services owned by Compilarr.Sources.Torznab.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Torznab/Newznab indexers and the qBittorrent and SABnzbd download clients.
    /// </summary>
    public static IServiceCollection AddCompilarrTorznab(this IServiceCollection services) => services;
}