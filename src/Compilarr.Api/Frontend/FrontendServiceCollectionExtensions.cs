using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Compilarr.Api.Frontend;

/// <summary>Registers the web root's file provider and the SPA's <c>index.html</c> provider.</summary>
public static class FrontendServiceCollectionExtensions
{
    /// <summary>
    /// Adds the web root file provider (a <see cref="NullFileProvider"/> until the SPA has been
    /// built into it) and <see cref="IndexHtmlProvider"/>.
    /// </summary>
    public static IServiceCollection AddCompilarrFrontend(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The container owns the PhysicalFileProvider, so it is disposed with the host.
        services.AddSingleton<IFileProvider>(CreateWebRootFileProvider);
        services.AddSingleton<IndexHtmlProvider>();

        return services;
    }

    /// <summary>
    /// Resolves the web root: an explicit <c>webroot</c> host setting wins (integration tests point
    /// it at a temporary directory), otherwise the host's own <c>wwwroot</c>.
    /// </summary>
    public static string? ResolveWebRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[WebHostDefaults.WebRootKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        }

        return string.IsNullOrWhiteSpace(environment.WebRootPath) ? null : environment.WebRootPath;
    }

    private static IFileProvider CreateWebRootFileProvider(IServiceProvider services)
    {
        var webRoot = ResolveWebRoot(
            services.GetRequiredService<IWebHostEnvironment>(),
            services.GetRequiredService<IConfiguration>());

        // PhysicalFileProvider throws when the directory is missing, and it is missing until P0-08
        // has built the SPA; the null provider answers "not found" instead.
        return webRoot is not null && Directory.Exists(webRoot)
            ? new PhysicalFileProvider(webRoot)
            : new NullFileProvider();
    }
}
