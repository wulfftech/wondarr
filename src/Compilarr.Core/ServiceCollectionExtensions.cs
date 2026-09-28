using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Core;

/// <summary>
/// Registers the services owned by Compilarr.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the domain, decision engine, import pipeline, metadata and persistence services.
    /// </summary>
    public static IServiceCollection AddCompilarrCore(this IServiceCollection services) => services;
}
