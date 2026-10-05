using Wondarr.Core.HealthCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.YouTube;

/// <summary>
/// Registers the services owned by Wondarr.Sources.YouTube.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the yt-dlp runner, its options and the YouTube health check. The YouTube Music search
    /// client arrives with P4-01.
    /// </summary>
    public static IServiceCollection AddWondarrYouTube(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Bound to the configuration root by the host once the settings API lands (P4-05); until then
        // the defaults apply and the validator still runs.
        services.AddOptions<YouTubeOptions>();

        // Registered against the bound YouTubeOptions type: the pipeline validates the type it
        // resolves, so an IValidateOptions<YtDlpOptions> here would never be asked.
        services.AddSingleton<IValidateOptions<YouTubeOptions>, YtDlpOptionsValidator>();

        // IProcessRunner is registered by Wondarr.Core.
        services.AddSingleton<YtDlpRunner>();
        services.AddSingleton<YtDlpAvailability>();
        services.AddSingleton<IHealthCheck, YtDlpHealthCheck>();

        return services;
    }
}
