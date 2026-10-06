using Wondarr.Core.Configuration;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Sources.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.YouTube;

/// <summary>Registers the YouTube settings API's own services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds the <c>youtube</c> section of <c>config.yml</c> to <see cref="YouTubeOptions"/> — the
    /// source's own registration stops at the defaults until the settings API lands — and adds the
    /// read/write service the settings controller calls.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root, reloaded after every settings write.</param>
    public static IServiceCollection AddWondarrYouTubeSettings(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Merges with the options registration the source project already made: the validators and
        // the ValidateOnStart stay, the binding is added here where the configuration root lives.
        services
            .AddOptions<YouTubeOptions>()
            .Bind(configuration.GetSection(YouTubeSettingsService.Section))
            .ValidateOnStart();

        // Scoped, because the default output policy is a setting row and the repository is scoped.
        // Resolved by a factory, because the environment is a plain IDictionary rather than a
        // service; it is read once per scope, exactly as the Soulseek settings read it once at
        // start-up.
        services.AddScoped<IYouTubeSettingsService>(provider => new YouTubeSettingsService(
            provider.GetRequiredService<IOptionsMonitor<YouTubeOptions>>(),
            provider.GetRequiredService<IConfigFileWriter>(),
            provider.GetServices<IValidateOptions<YouTubeOptions>>(),
            provider.GetRequiredService<ISettingsRepository>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<YtDlpAvailability>(),
            Environment.GetEnvironmentVariables(),
            provider.GetRequiredService<ILogger<YouTubeSettingsService>>(),
            provider.GetRequiredService<ILogger<YtDlpAvailability>>()));

        return services;
    }
}
