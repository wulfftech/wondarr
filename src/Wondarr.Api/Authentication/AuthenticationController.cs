// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/AuthenticationController.cs, GPL-3.0.
// Adapted for Wondarr: credentials come from ICredentialStore, the URL base from
// IOptionsMonitor<ServerOptions>, and GET /login renders the form the reverse proxy redirects to.

using System.Net;
using System.Security.Claims;
using Wondarr.Api.Extensions;
using Wondarr.Core.Authentication;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>Signs the local user in and out of the web UI.</summary>
[AllowAnonymous]
[ApiController]
public sealed partial class AuthenticationController : Controller
{
    private readonly ICredentialStore _credentials;
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;
    private readonly ILogger<AuthenticationController> _logger;

    /// <summary>Initialises a new instance of the <see cref="AuthenticationController"/> class.</summary>
    public AuthenticationController(
        ICredentialStore credentials,
        IOptionsMonitor<ServerOptions> serverOptions,
        ILogger<AuthenticationController> logger)
    {
        _credentials = credentials;
        _serverOptions = serverOptions;
        _logger = logger;
    }

    /// <summary>The login form, or the create-your-login form when no login exists yet.</summary>
    [HttpGet("login")]
    public async Task<IActionResult> LoginForm(
        [FromQuery] bool loginFailed,
        [FromQuery] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var configured = await _credentials.IsConfiguredAsync(cancellationToken).ConfigureAwait(false);

        return Content(
            LoginPage.Render(
                _serverOptions.CurrentValue.UrlBase,
                loginFailed,
                VariantFor(configured),
                Url.IsLocalUrl(returnUrl) ? returnUrl : null,
                notice: configured && Request.Query.ContainsKey("exists") ? "A login already exists. Sign in with it." : null),
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

        return await SignInAndRedirectAsync(resource.Username, resource.RememberMe, returnUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the first login from the login page. Only for <c>auth: forms</c>, only from a local
    /// address, and only while no login exists. Like <c>POST /login</c> it carries no antiforgery
    /// token.
    /// </summary>
    [HttpPost("login/setup")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Setup(
        [FromForm] SetupResource resource,
        [FromQuery] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var options = _serverOptions.CurrentValue;

        if (options.Auth != AuthenticationMethod.Forms)
        {
            return NotFound();
        }

        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        var remoteAddress = HttpContext.GetRemoteIp();

        if (!IsLocalRequest())
        {
            LogSetupRefused(_logger, remoteAddress);

            return Page(
                StatusCodes.Status403Forbidden,
                LoginPage.Render(options.UrlBase, false, LoginPage.Variant.CreateFromLocalNetwork, safeReturnUrl));
        }

        var username = resource?.Username;
        var password = resource?.Password;

        var error = Validate(username, password, resource?.PasswordAgain);

        if (error is not null)
        {
            return Page(
                StatusCodes.Status400BadRequest,
                LoginPage.Render(options.UrlBase, false, LoginPage.Variant.Create, safeReturnUrl, error, username));
        }

        if (!await _credentials.TrySetInitialAsync(username!, password!, cancellationToken).ConfigureAwait(false))
        {
            var query = string.IsNullOrEmpty(safeReturnUrl) ? string.Empty : $"returnUrl={Uri.EscapeDataString(safeReturnUrl)}&";

            return Redirect($"{options.UrlBase}/login?{query}exists=true");
        }

        LogSetupCompleted(_logger, remoteAddress);

        return await SignInAndRedirectAsync(username!, resource!.RememberMe, returnUrl).ConfigureAwait(false);
    }

    /// <summary>Ends the Forms session.</summary>
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(nameof(AuthenticationMethod.Forms)).ConfigureAwait(false);

        return Redirect(_serverOptions.CurrentValue.UrlBase + "/");
    }

    private static string? Validate(string? username, string? password, string? passwordAgain)
    {
        // The same rules as PUT /api/v1/auth/user, plus the two passwords must match.
        if (string.IsNullOrWhiteSpace(username) || username.Length > AuthUserController.MaxUsernameLength)
        {
            return $"The username must be between 1 and {AuthUserController.MaxUsernameLength} characters.";
        }

        if (password is null || password.Length < AuthUserController.MinPasswordLength)
        {
            return $"The password must be at least {AuthUserController.MinPasswordLength} characters.";
        }

        return string.Equals(password, passwordAgain, StringComparison.Ordinal)
            ? null
            : "The two passwords do not match.";
    }

    private static ContentResult Page(int statusCode, string html) =>
        new() { StatusCode = statusCode, Content = html, ContentType = "text/html; charset=utf-8" };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused to create the first login from {RemoteAddress}: it is not a local address")]
    private static partial void LogSetupRefused(ILogger logger, IPAddress? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "The first login was created from {RemoteAddress}")]
    private static partial void LogSetupCompleted(ILogger logger, IPAddress? remoteAddress);

    /// <summary>
    /// Whether the caller is on the local network, with exactly the definition
    /// <see cref="UiAuthorizationHandler"/> uses for <c>disabledForLocalAddresses</c>: a request that
    /// carries <c>X-Forwarded-For</c> is never local.
    /// </summary>
    private bool IsLocalRequest() =>
        !Request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName) &&
        HttpContext.GetRemoteIp() is { } remoteIp &&
        remoteIp.IsLocalAddress();

    private LoginPage.Variant VariantFor(bool configured)
    {
        // Only the forms login has a page to create a login on.
        if (configured || _serverOptions.CurrentValue.Auth != AuthenticationMethod.Forms)
        {
            return LoginPage.Variant.SignIn;
        }

        return IsLocalRequest() ? LoginPage.Variant.Create : LoginPage.Variant.CreateFromLocalNetwork;
    }

    private async Task<IActionResult> SignInAndRedirectAsync(string username, string? rememberMe, string? returnUrl)
    {
        var urlBase = _serverOptions.CurrentValue.UrlBase;

        var claims = new List<Claim>
        {
            new("user", username),
            new("AuthType", nameof(AuthenticationMethod.Forms)),
        };

        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe == "on",
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
}
