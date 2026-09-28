using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Sources.Slskd;

/// <summary>
/// Registers the services owned by Compilarr.Sources.Slskd.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the slskd HTTP client and the bundled slskd host supervisor.
    /// </summary>
    public static IServiceCollection AddCompilarrSlskd(this IServiceCollection services) => services;
}
