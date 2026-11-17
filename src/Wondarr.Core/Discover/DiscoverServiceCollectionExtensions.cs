using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Discover;

/// <summary>Registers the services owned by the Discover namespace.</summary>
public static class DiscoverServiceCollectionExtensions
{
    /// <summary>Adds the <c>discover</c> options, the three recommendation sources and the Discover service.</summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configuration">Configuration to bind the <c>discover</c> section from.</param>
    public static IServiceCollection AddWondarrDiscover(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DiscoverOptions>()
            .Bind(configuration.GetSection("Discover"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<DiscoverOptions>, DiscoverOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<RecommendationWarnings>();

        // Scoped: the sources take typed HTTP clients, which the factory hands out transient. The order
        // is the order a field is taken from when two sources suggest the same song.
        services.AddScoped<IRecommendationSource, ListenBrainzRecommendationSource>();
        services.AddScoped<IRecommendationSource, LastFmRecommendationSource>();
        services.AddScoped<IRecommendationSource, DeezerRecommendationSource>();
        services.AddScoped<IDiscoverService, DiscoverService>();

        return services;
    }
}
