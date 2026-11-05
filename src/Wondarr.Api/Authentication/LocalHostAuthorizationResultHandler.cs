using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Answers a request refused by <see cref="LocalHostAuthorizationRequirement"/>. A browser navigation gets
/// a short page saying which name it used and what to do about it; <c>/initialize.json</c> and anything
/// that does not ask for HTML gets a 403 problem-details body with the same sentence. Every other
/// outcome goes to the framework's own handler, unchanged.
/// </summary>
public sealed class LocalHostAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="LocalHostAuthorizationResultHandler"/> class.</summary>
    public LocalHostAuthorizationResultHandler(IOptionsMonitor<ServerOptions> serverOptions) => _serverOptions = serverOptions;

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!authorizeResult.Succeeded &&
            authorizeResult.AuthorizationFailure is { } failure &&
            failure.FailedRequirements.OfType<LocalHostAuthorizationRequirement>().Any())
        {
            await RefuseAsync(context).ConfigureAwait(false);

            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    private static string Detail(string host) =>
        $"Wondarr was opened as {host}, which is not a local name. With authentication off it only answers local names: " +
        "open it by its IP address or a local name, add the name to server.allowed_hosts in config.yml, " +
        "or turn off \"Only answer local host names\" under Settings > General > Security (reached by a local name).";

    private static bool WantsHtml(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
        !request.Path.Equals("/initialize.json", StringComparison.OrdinalIgnoreCase) &&
        request.GetTypedHeaders().Accept.Any(accept => string.Equals(accept.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase));

    private async Task RefuseAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";

        if (WantsHtml(context.Request))
        {
            context.Response.ContentType = "text/html; charset=utf-8";

            var page = LoginPage.Render(
                _serverOptions.CurrentValue.UrlBase,
                loginFailed: false,
                LoginPage.Variant.LocalHostRequired,
                host: string.IsNullOrWhiteSpace(host) ? "(no host name)" : host);

            await context.Response.WriteAsync(page, context.RequestAborted).ConfigureAwait(false);

            return;
        }

        await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Host name is not local",
                detail: Detail(string.IsNullOrWhiteSpace(host) ? "(no host name)" : host))
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }
}
