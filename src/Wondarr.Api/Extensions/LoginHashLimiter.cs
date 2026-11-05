using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Extensions;

/// <summary>
/// Caps the password checks that run at once, across all callers. The login actions take a lease
/// after the form is read, so a connection that trickles its body in holds no permit. There is no
/// queue: a caller who finds both permits taken is answered 429 at once.
/// </summary>
public sealed class LoginHashLimiter : IDisposable
{
    private readonly ConcurrencyLimiter _limiter = new(
        new ConcurrencyLimiterOptions { PermitLimit = LoginRateLimiting.ConcurrentHashes, QueueLimit = 0 });

    /// <summary>Takes a permit, or returns <see langword="null"/> when every permit is taken.</summary>
    /// <returns>The lease to dispose when the password check is done.</returns>
    public RateLimitLease? TryAcquire()
    {
        var lease = _limiter.AttemptAcquire();

        return lease.IsAcquired ? lease : null;
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}

/// <summary>The 429 "busy" page, written the way the rate-limit middleware writes it.</summary>
internal sealed class LoginBusyResult : IActionResult
{
    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context) =>
        LoginRateLimiting.WriteBusyAsync(context.HttpContext, context.HttpContext.RequestAborted).AsTask();
}
