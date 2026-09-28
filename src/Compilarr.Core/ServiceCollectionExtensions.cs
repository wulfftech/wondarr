using Compilarr.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Registers the resolved paths and the validated, bound <see cref="ServerOptions"/>.
    /// </summary>
    public static IServiceCollection AddCompilarrConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        CompilarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);

        services.AddOptions<ServerOptions>()
            .Bind(configuration.GetSection("Server"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        services.AddSingleton<IPostConfigureOptions<ServerOptions>, ServerOptionsPostConfigure>();

        return services;
    }
}
