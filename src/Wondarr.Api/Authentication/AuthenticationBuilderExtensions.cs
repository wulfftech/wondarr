// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/AuthenticationBuilderExtensions.cs, GPL-3.0.
// Adapted for Wondarr: the cookie is always named WondarrAuth, so the Diacritical dependency
// and the instance-name mangling are gone.

using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Api.Authentication;

/// <summary>Registers the schemes the app authenticates with.</summary>
public static class AuthenticationBuilderExtensions
{
    /// <summary>The name of the Forms authentication cookie.</summary>
    public const string CookieName = "WondarrAuth";

    /// <summary>Adds an API key scheme reading <paramref name="name"/>'s header and query parameter.</summary>
    public static AuthenticationBuilder AddApiKey(
        this AuthenticationBuilder authenticationBuilder,
        string name,
        Action<ApiKeyAuthenticationOptions> options) =>
        authenticationBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(name, options);

    /// <summary>Adds the scheme that trusts every caller.</summary>
    public static AuthenticationBuilder AddNone(this AuthenticationBuilder authenticationBuilder, string name) =>
        authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, _ => { });

    /// <summary>Adds the scheme for an external, authenticated reverse proxy.</summary>
    public static AuthenticationBuilder AddExternal(this AuthenticationBuilder authenticationBuilder, string name) =>
        authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(name, _ => { });

    /// <summary>
    /// Adds <c>None</c>, <c>External</c>, <c>Forms</c> (cookie), <c>API</c> and <c>SignalR</c>.
    /// </summary>
    public static AuthenticationBuilder AddAppAuthentication(this IServiceCollection services)
    {
        services.AddOptions<CookieAuthenticationOptions>(nameof(AuthenticationMethod.Forms))
            .Configure(options =>
            {
                options.Cookie.Name = CookieName;
                options.AccessDeniedPath = "/login?loginFailed=true";
                options.LoginPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.ReturnUrlParameter = "returnUrl";
            });

        return services.AddAuthentication()
            .AddNone(nameof(AuthenticationMethod.None))
            .AddExternal(nameof(AuthenticationMethod.External))
            .AddCookie(nameof(AuthenticationMethod.Forms))
            .AddApiKey("API", options =>
            {
                options.HeaderName = "X-Api-Key";
                options.QueryName = "apikey";
            })
            .AddApiKey("SignalR", options =>
            {
                options.HeaderName = "X-Api-Key";
                options.QueryName = "access_token";
            });
    }
}
