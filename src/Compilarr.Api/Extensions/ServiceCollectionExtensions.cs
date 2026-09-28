using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Compilarr.Api.Authentication;
using Compilarr.Api.Frontend;
using Compilarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Api.Extensions;

/// <summary>Registers the HTTP layer: controllers, authentication, authorization and data protection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the controllers and the authentication pipeline the *arrs use: an API key on
    /// <c>/api/v1</c> by default, and the configured UI method for everything else.
    /// </summary>
    public static IServiceCollection AddCompilarrApi(this IServiceCollection services, CompilarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });

        // Login cookies are encrypted with the data protection key ring, so it has to outlive the process.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.ConfigDir, "asp")));

        services.AddSingleton<IAuthorizationPolicyProvider, UiAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, UiAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy("SignalR", policy =>
            {
                policy.AddAuthenticationSchemes("SignalR");
                policy.RequireAuthenticatedUser();
            });

            // Require authentication on everything except the actions marked [AllowAnonymous].
            options.FallbackPolicy = new AuthorizationPolicyBuilder("API")
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddAppAuthentication();

        // One document, "v1", snapshotted by tests/Compilarr.Api.Tests/OpenApiSnapshotTests.cs.
        services.AddOpenApi("v1", options =>
            options.AddDocumentTransformer(new CompilarrDocumentTransformer().TransformAsync));

        return services;
    }
}
