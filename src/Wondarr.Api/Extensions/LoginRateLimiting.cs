using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Wondarr.Api.Authentication;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wondarr.Api.Extensions;

/// <summary>
/// Throttles <c>POST /login</c> and <c>POST /login/setup</c> (R-037). Two limits apply, both
/// answered with 429 and <c>Retry-After</c>:
/// <list type="bullet">
/// <item>per caller: a fixed window of <see cref="AttemptsPerMinute"/> attempts a minute, partitioned by the socket address;</item>
/// <item>across all callers: <see cref="ConcurrentHashes"/> requests at a time, because each one runs PBKDF2 and a flood must not pin every core.</item>
/// </list>
/// </summary>
public static class LoginRateLimiting
{
    /// <summary>Attempts one address may make in a minute.</summary>
    public const int AttemptsPerMinute = 10;

    /// <summary>Login requests that may run at once, across all callers.</summary>
    public const int ConcurrentHashes = 2;

    /// <summary>The message the login page shows after a 429.</summary>
    public const string TooManyAttemptsMessage = "Too many sign-in attempts. Wait a minute and try again.";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registers the limiters and the 429 response.</summary>
    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsLoginPost(context.Request)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.GetRemoteIp()?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = AttemptsPerMinute,
                            Window = Window,
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter("other")),
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsLoginPost(context.Request)
                    ? RateLimitPartition.GetConcurrencyLimiter(
                        "login",
                        _ => new ConcurrencyLimiterOptions { PermitLimit = ConcurrentHashes, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other")));

            options.OnRejected = WriteRejectionAsync;
        });
    }

    private static bool IsLoginPost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/login/setup", StringComparison.OrdinalIgnoreCase));

    private static async ValueTask WriteRejectionAsync(OnRejectedContext rejection, CancellationToken cancellationToken)
    {
        var context = rejection.HttpContext;
        var retryAfter = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
            ? (int)Math.Ceiling(retry.TotalSeconds)
            : 5;

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/html; charset=utf-8";

        // The form posts the page itself, so a bare 429 would leave the user on a blank browser error.
        var urlBase = context.RequestServices.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue.UrlBase;
        var setup = context.Request.Path.Equals("/login/setup", StringComparison.OrdinalIgnoreCase);
        var returnUrl = context.Request.Query["returnUrl"].ToString();

        await context.Response.WriteAsync(
            LoginPage.Render(
                urlBase,
                false,
                setup ? LoginPage.Variant.Create : LoginPage.Variant.SignIn,
                IsLocalUrl(returnUrl) ? returnUrl : null,
                TooManyAttemptsMessage),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLocalUrl(string url) =>
        url.Length > 1 && url[0] == '/' && url[1] != '/' && url[1] != '\\';
}
