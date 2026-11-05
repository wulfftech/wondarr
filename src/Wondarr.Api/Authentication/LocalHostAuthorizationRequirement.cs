using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Added to the <c>UI</c> policy when <c>auth: none</c> and <see cref="ServerOptions.RequireLocalHost"/>
/// are both in force: the request must be addressed to a local host name. This is the Host rule of
/// <see cref="LocalCallerRules"/> alone, not the socket-address or proxy-header rules, so a rebound
/// public name (DNS rebinding) cannot read <c>/initialize.json</c>.
/// </summary>
public sealed class LocalHostAuthorizationRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds <see cref="LocalHostAuthorizationRequirement"/> for a local <c>Host</c>. A refusal is left
/// to <see cref="LocalHostAuthorizationResultHandler"/>, which answers it; this handler does not call
/// <c>Fail</c>, so the requirement stays in the failure's list of unmet requirements.
/// </summary>
public sealed class LocalHostAuthorizationHandler : AuthorizationHandler<LocalHostAuthorizationRequirement>
{
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="LocalHostAuthorizationHandler"/> class.</summary>
    public LocalHostAuthorizationHandler(IOptionsMonitor<ServerOptions> serverOptions) => _serverOptions = serverOptions;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        LocalHostAuthorizationRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext &&
            LocalCallerRules.IsLocalHost(httpContext.Request.Host.Host, _serverOptions.CurrentValue.AllowedHosts))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
