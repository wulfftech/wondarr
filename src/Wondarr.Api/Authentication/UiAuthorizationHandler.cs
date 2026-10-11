// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/UiAuthorizationHandler.cs, GPL-3.0.
// Adapted for Wondarr: reads IOptionsMonitor<ServerOptions> per request instead of a cached
// config field, drops CGNAT trust, and the local rule also checks every proxy header and the Host
// (see LocalCallerRules).

using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Succeeds the UI requirement for callers on the local network when
/// <see cref="ServerOptions.AuthRequired"/> is <see cref="AuthenticationRequired.DisabledForLocalAddresses"/>.
/// "Local" is <see cref="LocalCallerRules"/>: a request that carries any proxy header is never
/// trusted, because the proxy in front of the app is the one that decides who the caller really is,
/// and neither is one whose <c>Host</c> is not a LAN-style name (DNS rebinding).
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
        var options = _serverOptions.CurrentValue;

        if (options.AuthRequired == AuthenticationRequired.DisabledForLocalAddresses &&
            context.Resource is HttpContext httpContext &&
            LocalCallerRules.IsLocalCaller(httpContext, options))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
