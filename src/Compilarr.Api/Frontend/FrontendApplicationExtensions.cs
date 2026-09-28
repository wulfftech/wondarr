using Compilarr.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Compilarr.Api.Frontend;

/// <summary>
/// Hosts the built React app: static files under the web root, and a fallback that serves
/// <c>index.html</c> for client-side routes so the SPA works under any URL base.
/// </summary>
public static class FrontendApplicationExtensions
{
    private const string AssetsPath = "/assets";

    /// <summary>
    /// Serves the web root's static files. They are anonymous (they hold no data) and the hashed
    /// build output under <c>/assets/</c> is immutable, so it can be cached for a year.
    /// </summary>
    public static WebApplication UseCompilarrFrontend(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var webRoot = app.Services.GetRequiredService<IFileProvider>();

        app.UseStaticFiles(new StaticFileOptions
        {
            // index.html is hidden here so it can only be served through IndexHtmlProvider.
            FileProvider = new IndexHtmlHidingFileProvider(webRoot),
            OnPrepareResponse = context =>
            {
                if (context.Context.Request.Path.StartsWithSegments(AssetsPath))
                {
                    context.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            },
        });

        return app;
    }

    /// <summary>
    /// Maps the SPA fallback, last in the pipeline. It is behind the UI policy, so a remote caller
    /// gets the login page before any of the app's HTML.
    /// </summary>
    public static WebApplication MapCompilarrSpa(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var indexHtml = app.Services.GetRequiredService<IndexHtmlProvider>();

        // "{*path:nonfile}": a path that looks like a file (assets/app.js) must never be claimed by
        // the fallback, or the static-file middleware yields to it and the browser gets index.html
        // for every script. index.html itself is hidden from static files, so /index.html is a 404
        // and the page is only ever served here, with its placeholder replaced. Server-owned paths
        // are rejected inside HandleAsync.
        app.MapFallback("{*path:nonfile}", context => HandleAsync(context, indexHtml))
            .RequireAuthorization(UiAuthorizationPolicyProvider.PolicyName);

        return app;
    }

    private static async Task HandleAsync(HttpContext context, IndexHtmlProvider indexHtml)
    {
        // Anything that is not a page request is a miss, not a route: only GET/HEAD get the SPA.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await WriteNotFoundProblemAsync(context);
            return;
        }

        if (IsServerPath(context.Request.Path))
        {
            await WriteNotFoundProblemAsync(context);
            return;
        }

        var html = indexHtml.GetIndexHtml();

        if (html is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("UI not built", context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";

        // The HTML names the hashed asset files, so it must never be cached.
        context.Response.Headers["Cache-Control"] = "no-cache";

        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    /// <summary>Server-owned paths that must never fall through to the SPA.</summary>
    private static bool IsServerPath(PathString path) =>
        path.StartsWithSegments("/api") ||
        path.StartsWithSegments("/signalr") ||
        path.StartsWithSegments("/docs") ||
        path.Equals("/initialize.json", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteNotFoundProblemAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found")
            .ExecuteAsync(context);
    }
}
