// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/NoAuthenticationHandler.cs, GPL-3.0.
// Adapted for Wondarr: the AuthType claim uses Wondarr's AuthenticationMethod enum.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>
/// Authenticates everyone as <c>Anonymous</c>. Backs both the <c>None</c> and the <c>External</c>
/// schemes: with <c>None</c> the app trusts the caller, and with <c>External</c> the reverse proxy
/// in front of it is responsible for authentication.
/// </summary>
public sealed class NoAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Initialises a new instance of the <see cref="NoAuthenticationHandler"/> class.</summary>
    public NoAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new("user", "Anonymous"),
            new("AuthType", nameof(AuthenticationMethod.None)),
        };

        var identity = new ClaimsIdentity(claims, "NoAuth", "user", "identifier");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "NoAuth");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
