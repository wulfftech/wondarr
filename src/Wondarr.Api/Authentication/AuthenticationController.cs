// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/AuthenticationController.cs, GPL-3.0.
// Adapted for Wondarr: credentials come from ICredentialStore, the URL base from
// IOptionsMonitor<ServerOptions>, and GET /login renders the form the reverse proxy redirects to.

using System.Security.Claims;
using Wondarr.Core.Authentication;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>Signs the local user in and out of the web UI.</summary>
[AllowAnonymous]
[ApiController]
public sealed class AuthenticationController : Controller
{
    private readonly ICredentialStore _credentials;
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;

    /// <summary>Initialises a new instance of the <see cref="AuthenticationController"/> class.</summary>
    public AuthenticationController(ICredentialStore credentials, IOptionsMonitor<ServerOptions> serverOptions)
    {
        _credentials = credentials;
        _serverOptions = serverOptions;
    }

    /// <summary>The login form.</summary>
    [HttpGet("login")]
    public async Task<IActionResult> LoginForm(
        [FromQuery] bool loginFailed,
        [FromQuery] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var configured = await _credentials.IsConfiguredAsync(cancellationToken).ConfigureAwait(false);

        return Content(
            LoginPage.Render(_serverOptions.CurrentValue.UrlBase, loginFailed, configured, Url.IsLocalUrl(returnUrl) ? returnUrl : null),
            "text/html; charset=utf-8");
    }

    /// <summary>Verifies the form and starts a Forms session.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromForm] LoginResource resource,
        [FromQuery] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var urlBase = _serverOptions.CurrentValue.UrlBase;

        if (resource?.Username is null ||
            resource.Password is null ||
            !await _credentials.VerifyAsync(resource.Username, resource.Password, cancellationToken).ConfigureAwait(false))
        {
            return Redirect($"{urlBase}/login?returnUrl={Uri.EscapeDataString(returnUrl ?? string.Empty)}&loginFailed=true");
        }

        var claims = new List<Claim>
        {
            new("user", resource.Username),
            new("AuthType", nameof(AuthenticationMethod.Forms)),
        };

        var properties = new AuthenticationProperties
        {
            IsPersistent = resource.RememberMe == "on",
        };

        await HttpContext.SignInAsync(
            nameof(AuthenticationMethod.Forms),
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", "user", "identifier")),
            properties).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return Redirect(urlBase + "/");
        }

        return Redirect(string.IsNullOrWhiteSpace(urlBase) || returnUrl.StartsWith(urlBase, StringComparison.Ordinal)
            ? returnUrl
            : urlBase + returnUrl);
    }

    /// <summary>Ends the Forms session.</summary>
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(nameof(AuthenticationMethod.Forms)).ConfigureAwait(false);

        return Redirect(_serverOptions.CurrentValue.UrlBase + "/");
    }
}
