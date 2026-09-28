// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Middleware/UrlBaseMiddleware.cs, GPL-3.0.
// Adapted for Wondarr: /ping is passed through so the container health check works under any URL base.

using Microsoft.AspNetCore.Http;

namespace Wondarr.Api.Middleware;

/// <summary>
/// Redirects requests that reach the app without the configured URL base. Runs after routing, so
/// only paths that did not match an endpoint are redirected; <c>/ping</c> is always served.
/// </summary>
public sealed class UrlBaseMiddleware
{
    private const string PingPath = "/ping";

    private readonly RequestDelegate _next;
    private readonly string _urlBase;

    /// <summary>Initialises a new instance of the <see cref="UrlBaseMiddleware"/> class.</summary>
    public UrlBaseMiddleware(RequestDelegate next, string urlBase)
    {
        _next = next;
        _urlBase = urlBase;
    }

    /// <summary>Handles a request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(_urlBase) &&
            string.IsNullOrWhiteSpace(context.Request.PathBase.Value) &&
            !context.Request.Path.Equals(PingPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect($"{_urlBase}{context.Request.Path}{context.Request.QueryString}");
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;

            return;
        }

        await _next(context);
    }
}
