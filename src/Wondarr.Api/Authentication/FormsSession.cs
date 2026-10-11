using System.Security.Claims;
using Wondarr.Core.Authentication;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wondarr.Api.Authentication;

/// <summary>
/// What the Forms session ticket carries and how it is checked. The ticket holds a credential
/// stamp (<see cref="ICredentialStore.GetStampAsync"/>), so changing the username or password ends
/// every other session (R-036).
/// </summary>
internal static class FormsSession
{
    /// <summary>The claim that holds the credential stamp.</summary>
    public const string StampClaim = "stamp";

    /// <summary>Builds the principal for a sign-in, stamped with the current credentials.</summary>
    public static async Task<ClaimsPrincipal> CreatePrincipalAsync(
        string username,
        ICredentialStore credentials,
        CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new("user", username),
            new("AuthType", nameof(AuthenticationMethod.Forms)),
        };

        var stamp = await credentials.GetStampAsync(cancellationToken).ConfigureAwait(false);

        if (stamp is not null)
        {
            claims.Add(new Claim(StampClaim, stamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", "user", "identifier"));
    }

    /// <summary>
    /// Rejects a ticket whose stamp no longer matches the stored credentials. A ticket without a
    /// stamp (issued before stamps existed) is rejected too: it signs in again once.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var credentials = context.HttpContext.RequestServices.GetRequiredService<ICredentialStore>();
        var current = await credentials.GetStampAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        var ticketStamp = context.Principal?.FindFirst(StampClaim)?.Value;

        if (current is not null && string.Equals(current, ticketStamp, StringComparison.Ordinal))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(nameof(AuthenticationMethod.Forms)).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the cookie should carry <c>Secure</c>: the request is HTTPS, or a proxy says the
    /// visitor's side was (first <c>X-Forwarded-Proto</c> value). A spoofed header can only make the
    /// caller's own cookie stricter (R-058).
    /// </summary>
    public static bool IsSecureRequest(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }

        var forwarded = request.Headers["X-Forwarded-Proto"].ToString();
        var first = forwarded.Split(',', 2)[0].Trim();

        return first.Equals("https", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Adds <c>Secure</c> to every cookie the Forms scheme writes (sign-in and sliding renewal) when
/// <see cref="FormsSession.IsSecureRequest"/> says so.
/// </summary>
internal sealed class SecureAwareCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, key);

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        if (FormsSession.IsSecureRequest(context.Request))
        {
            options.Secure = true;
        }

        _inner.AppendResponseCookie(context, key, value, options);
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
    {
        if (FormsSession.IsSecureRequest(context.Request))
        {
            options.Secure = true;
        }

        _inner.DeleteCookie(context, key, options);
    }
}
