using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.Updates;

namespace Wondarr.Core.Feedback;

/// <summary>Registers the feedback button's service.</summary>
public static class FeedbackServiceCollectionExtensions
{
    /// <summary>
    /// Adds the GitHub issues client and the feedback service. The <c>feedback</c> options are bound by
    /// <c>AddWondarrConfiguration</c>, which has the configuration.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    public static IServiceCollection AddWondarrFeedback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => RunningVersion.FromAssembly(typeof(FeedbackService).Assembly));

        // No resilience handler on purpose: the only request is a POST, and a retried one opens a second issue.
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

        return services;
    }
}
