// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Authentication/ApiKeyAuthenticationHandler.cs, GPL-3.0.
// Adapted for Wondarr: the key comes from IOptionsMonitor<ServerOptions> and is compared with
// CryptographicOperations.FixedTimeEquals; the "Bearer " prefix is stripped only when present.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Authentication;

/// <summary>Options for the API key schemes (<c>API</c> and <c>SignalR</c>).</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The scheme name reported in the ticket.</summary>
    public const string DefaultScheme = "API Key";

    /// <summary>The scheme name used when building the authentication ticket.</summary>
    public string Scheme => DefaultScheme;

    /// <summary>The authentication type of the resulting identity.</summary>
    public string AuthenticationType { get; set; } = DefaultScheme;

    /// <summary>Header the key is read from.</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>Query parameter the key is read from.</summary>
    public string QueryName { get; set; } = "apikey";

    /// <summary>
    /// Whether a <c>?token=</c> minted by <see cref="DownloadTokenService"/> authenticates a GET of
    /// the one path it was minted for, in place of the key.
    /// </summary>
    public bool AcceptDownloadToken { get; set; }
}

/// <summary>
/// Authenticates <c>X-Api-Key</c>, <c>?apikey=</c> and <c>Authorization: Bearer</c> against the
/// configured <see cref="ServerOptions.ApiKey"/>.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly IOptionsMonitor<ServerOptions> _serverOptions;
    private readonly DownloadTokenService _downloadTokens;

    /// <summary>Initialises a new instance of the <see cref="ApiKeyAuthenticationHandler"/> class.</summary>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ServerOptions> serverOptions,
        DownloadTokenService downloadTokens)
        : base(options, logger, encoder)
    {
        _serverOptions = serverOptions;
        _downloadTokens = downloadTokens;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var providedApiKey = ParseApiKey();
        var configuredApiKey = _serverOptions.CurrentValue.ApiKey;

        if (string.IsNullOrWhiteSpace(providedApiKey) && HasValidDownloadToken())
        {
            var tokenIdentity = new ClaimsIdentity([new Claim("DownloadToken", "true")], Options.AuthenticationType);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(tokenIdentity), Options.Scheme)));
        }

        if (string.IsNullOrWhiteSpace(providedApiKey) || string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = Encoding.UTF8.GetBytes(configuredApiKey);
        var provided = Encoding.UTF8.GetBytes(providedApiKey);

        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim("ApiKey", "true")], Options.AuthenticationType);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Options.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        SetStatusIfUnset(StatusCodes.Status401Unauthorized);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        SetStatusIfUnset(StatusCodes.Status403Forbidden);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets <paramref name="statusCode"/> unless the response has already been decided by another
    /// scheme. The API fallback policy is combined with the policy an endpoint declares, so a UI
    /// endpoint is challenged on the cookie scheme as well: that scheme redirects to the login
    /// form, and overwriting its 302 with 401 would break the web UI entirely.
    /// </summary>
    private void SetStatusIfUnset(int statusCode)
    {
        if (Response.StatusCode is < StatusCodes.Status300MultipleChoices or >= StatusCodes.Status400BadRequest)
        {
            Response.StatusCode = statusCode;
        }
    }

    /// <summary>
    /// A <c>?token=</c> counts only on a GET, only where the scheme takes tokens, and only for the
    /// exact path it was minted for. The path is the request path without the URL base.
    /// </summary>
    private bool HasValidDownloadToken() =>
        Options.AcceptDownloadToken &&
        HttpMethods.IsGet(Request.Method) &&
        Request.Query.TryGetValue(DownloadTokenService.QueryName, out var token) &&
        _downloadTokens.IsValid(token.FirstOrDefault(), Request.Path.Value ?? string.Empty);

    private string? ParseApiKey()
    {
        if (Request.Query.TryGetValue(Options.QueryName, out var queryValue))
        {
            return queryValue.FirstOrDefault();
        }

        if (Request.Headers.TryGetValue(Options.HeaderName, out var headerValue))
        {
            return headerValue.FirstOrDefault();
        }

        var authorization = Request.Headers.Authorization.FirstOrDefault();

        return authorization is not null && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[BearerPrefix.Length..]
            : authorization;
    }
}
