namespace Compilarr.Core.Metadata.Http;

/// <summary>
/// Waits for a per-host <see cref="RequestSpacingGate"/> before every attempt, so a retry cannot
/// burst past the host's rate limit. Registered <em>inside</em> the retry pipeline on purpose.
/// </summary>
public sealed class RequestSpacingHandler : DelegatingHandler
{
    private readonly RequestSpacingGate _gate;

    /// <summary>Initialises a new instance of the <see cref="RequestSpacingHandler"/> class.</summary>
    /// <param name="gate">The gate shared by every client that talks to this host.</param>
    public RequestSpacingHandler(RequestSpacingGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        _gate = gate;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}