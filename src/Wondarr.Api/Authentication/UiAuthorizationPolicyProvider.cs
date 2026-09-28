// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/UiAuthorizationPolicyProvider.cs, GPL-3.0.
// Adapted for Wondarr: the policy's scheme follows IOptionsMonitor<ServerOptions> per request.

using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Provides the <c>UI</c> policy, whose scheme is whichever authentication method is configured
/// (<c>None</c>, <c>External</c> or <c>Forms</c>) plus the bypassable deny-anonymous requirement.
/// </summary>
public sealed class UiAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>The name of the policy the web UI endpoints use.</summary>
    public const string PolicyName = "UI";

    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="UiAuthorizationPolicyProvider"/> class.</summary>
    public UiAuthorizationPolicyProvider(
        IOptions<AuthorizationOptions> options,
        IOptionsMonitor<ServerOptions> serverOptions)
    {
        FallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
        _serverOptions = serverOptions;
    }

    /// <summary>The provider that handles every policy except <c>UI</c>.</summary>
    public DefaultAuthorizationPolicyProvider FallbackPolicyProvider { get; }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => FallbackPolicyProvider.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => FallbackPolicyProvider.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.Equals(PolicyName, StringComparison.OrdinalIgnoreCase))
        {
            return FallbackPolicyProvider.GetPolicyAsync(policyName);
        }

        var policy = new AuthorizationPolicyBuilder(_serverOptions.CurrentValue.Auth.ToString())
            .AddRequirements(new BypassableDenyAnonymousAuthorizationRequirement());

        return Task.FromResult<AuthorizationPolicy?>(policy.Build());
    }
}
