// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/UiAuthorizationHandler.cs, GPL-3.0.
// Adapted for Compilarr: reads IOptionsMonitor<ServerOptions> per request instead of a cached
// config field, and drops CGNAT trust.

using Compilarr.Api.Extensions;
using Compilarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Compilarr.Api.Authentication;

/// <summary>
/// Succeeds the UI requirement for callers on the local network when
/// <see cref="ServerOptions.AuthRequired"/> is <see cref="AuthenticationRequired.DisabledForLocalAddresses"/>.
/// A request that carries <c>X-Forwarded-For</c> is never trusted, because the proxy in front of
/// the app is the one that decides who the caller really is.
/// </summary>
public sealed class UiAuthorizationHandler : AuthorizationHandler<BypassableDenyAnonymousAuthorizationRequirement>
{
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="UiAuthorizationHandler"/> class.</summary>
    public UiAuthorizationHandler(IOptionsMonitor<ServerOptions> serverOptions) => _serverOptions = serverOptions;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BypassableDenyAnonymousAuthorizationRequirement requirement)
    {
        if (_serverOptions.CurrentValue.AuthRequired != AuthenticationRequired.DisabledForLocalAddresses ||
            context.Resource is not HttpContext httpContext ||
            httpContext.Request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName))
        {
            return Task.CompletedTask;
        }

        var remoteIp = httpContext.GetRemoteIp();

        if (remoteIp is not null && remoteIp.IsLocalAddress())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
