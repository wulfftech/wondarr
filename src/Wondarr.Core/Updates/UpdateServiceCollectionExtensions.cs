using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Updates;

/// <summary>Registers the update checker.</summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>
    /// Adds the GitHub client, the version of this build, the check service and the settings service.
    /// The <c>update</c> options are bound by <c>AddWondarrConfiguration</c>, which has the configuration.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    public static IServiceCollection AddWondarrUpdates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => RunningVersion.FromAssembly(typeof(UpdateCheckService).Assembly));

        services.AddHttpClient(UpdateCheckService.ClientName, (serviceProvider, client) =>
        {
            var running = serviceProvider.GetRequiredService<RunningVersion>();

            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                $"Wondarr/{running.Text} (+{UpdateCheckService.ProjectUrl})");
        });

        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

        // Scoped, resolved by a factory: the process environment is a plain IDictionary rather than a service.
        services.AddScoped<IUpdateSettingsService>(serviceProvider => new UpdateSettingsService(
            serviceProvider.GetRequiredService<IOptionsMonitor<UpdateOptions>>(),
            serviceProvider.GetRequiredService<Configuration.IConfigFileWriter>(),
            Environment.GetEnvironmentVariables()));

        return services;
    }
}
