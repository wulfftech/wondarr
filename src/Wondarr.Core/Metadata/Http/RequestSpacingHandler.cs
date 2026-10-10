using System.Net;

namespace Wondarr.Core.Metadata.Http;

/// <summary>
/// Waits for a per-host <see cref="RequestSpacingGate"/> before every attempt, so a retry cannot
/// burst past the host's rate limit, and turns a <c>Retry-After</c> on any 429 or 503 into a cooldown
/// the whole host shares. Registered <em>inside</em> the retry pipeline on purpose.
/// </summary>
/// <remarks>
/// A request made while a person is waiting (<see cref="InteractiveRequests"/>) does not queue behind a
/// cooldown longer than <see cref="InteractiveRequests.MaxWait"/>: it fails at once with the remaining
/// wait, without touching the network. A background request waits the cooldown out.
/// </remarks>
public sealed class RequestSpacingHandler : DelegatingHandler
{
    private readonly RequestSpacingGate _gate;
    private readonly string _provider;

    /// <summary>Initialises a new instance of the <see cref="RequestSpacingHandler"/> class.</summary>
    /// <param name="gate">The gate shared by every client that talks to this host.</param>
    /// <param name="provider">The provider key a fail-fast error names.</param>
    public RequestSpacingHandler(RequestSpacingGate gate, string provider = "unknown")
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(provider);

        _gate = gate;
        _provider = provider;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cooldown = _gate.Cooldown;
        if (cooldown > InteractiveRequests.MaxWait && InteractiveRequests.IsActive)
        {
            throw new MetadataProviderException(
                _provider,
                HttpStatusCode.ServiceUnavailable,
                $"{_provider} asked to be left alone for {(int)Math.Ceiling(cooldown.TotalSeconds)} more seconds.",
                cooldown);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            && RetryAfterHeader.Read(response, _gate.UtcNow) is { } wait)
        {
            _gate.DelayUntil(_gate.UtcNow + wait);
        }

        return response;
    }
}
