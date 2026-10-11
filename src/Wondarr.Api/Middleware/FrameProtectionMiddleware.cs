using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Middleware;

/// <summary>
/// Keeps other sites from framing the web UI (R-059): every response that serves HTML carries
/// <c>Content-Security-Policy: frame-ancestors 'self'</c> followed by <c>server.frame_ancestors</c>.
/// While that list is empty it also sends <c>X-Frame-Options: SAMEORIGIN</c> for older browsers; with
/// a list it omits it, because that header cannot express an allow-list. API JSON responses need
/// neither.
/// </summary>
public sealed class FrameProtectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="FrameProtectionMiddleware"/> class.</summary>
    public FrameProtectionMiddleware(RequestDelegate next, IOptionsMonitor<ServerOptions> serverOptions)
    {
        _next = next;
        _serverOptions = serverOptions;
    }

    /// <summary>Adds the headers when the response turns out to be HTML.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var contentType = context.Response.ContentType;

            if (contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                // Validated at start-up; the normalised origin is what goes into the header, never the raw text.
                var ancestors = _serverOptions.CurrentValue.FrameAncestors
                    .Select(entry => Uri.TryCreate(entry, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
                    .OfType<string>()
                    .ToList();

                context.Response.Headers.ContentSecurityPolicy = ancestors.Count == 0
                    ? "frame-ancestors 'self'"
                    : $"frame-ancestors 'self' {string.Join(' ', ancestors)}";

                if (ancestors.Count == 0)
                {
                    context.Response.Headers.XFrameOptions = "SAMEORIGIN";
                }
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
