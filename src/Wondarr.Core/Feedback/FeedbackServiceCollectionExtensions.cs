using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Updates;

namespace Wondarr.Core.Feedback;

/// <summary>Registers the feedback button's service.</summary>
public static class FeedbackServiceCollectionExtensions
{
    /// <summary>
    /// Adds the GitHub issues client, the feedback service and the settings service of its card. The <c>feedback</c> options are bound by
    /// <c>AddWondarrConfiguration</c>, which has the configuration.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    public static IServiceCollection AddWondarrFeedback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => RunningVersion.FromAssembly(typeof(FeedbackService).Assembly));

        // No resilience handler on purpose: a retried issue POST opens a second issue, and the token check is one
        // GET a person asked for.
        services.AddHttpClient(FeedbackService.ClientName, (serviceProvider, client) =>
        {
            var running = serviceProvider.GetRequiredService<RunningVersion>();

            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                $"Wondarr/{running.Text} (+{UpdateCheckService.ProjectUrl})");
        });

        services.AddSingleton<IFeedbackService, FeedbackService>();

        // Scoped, resolved by a factory: the process environment is a plain IDictionary rather than a service.
        services.AddScoped<IFeedbackSettingsService>(serviceProvider => new FeedbackSettingsService(
            serviceProvider.GetRequiredService<IOptionsMonitor<FeedbackOptions>>(),
            serviceProvider.GetRequiredService<IConfigFileWriter>(),
            serviceProvider.GetRequiredService<IFeedbackService>(),
            serviceProvider.GetRequiredService<ISecretRegistry>(),
            Environment.GetEnvironmentVariables(),
            serviceProvider.GetRequiredService<ILogger<FeedbackSettingsService>>()));

        return services;
    }
}
