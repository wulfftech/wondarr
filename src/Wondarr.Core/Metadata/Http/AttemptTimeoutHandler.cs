using System.Globalization;

namespace Wondarr.Core.Metadata.Http;

/// <summary>
/// Limits one attempt at the network: the headers and the body. Registered <em>after</em>
/// <see cref="RequestSpacingHandler"/>, so the wait at the spacing gate (a queue, or a <c>Retry-After</c>
/// cooldown of up to five minutes) is never inside the timed part, and before the network, so a stalled
/// body read is bounded too (<see cref="HttpClient.Timeout"/> stops at the headers with
/// <c>ResponseHeadersRead</c>).
/// </summary>
/// <remarks>
/// A timeout surfaces as the <see cref="TaskCanceledException"/> that <see cref="HttpClient.Timeout"/>
/// used to raise, so every existing "the provider did not answer" catch keeps working. The caller's own
/// cancellation passes through untouched.
/// </remarks>
public sealed class AttemptTimeoutHandler : DelegatingHandler
{
    private readonly TimeSpan _timeout;
    private readonly string _provider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="AttemptTimeoutHandler"/> class.</summary>
    /// <param name="timeout">How long one attempt may take, headers and body together.</param>
    /// <param name="provider">The provider named in the timeout message.</param>
    /// <param name="timeProvider">The clock the limit runs on; tests drive it with <c>FakeTimeProvider</c>.</param>
    public AttemptTimeoutHandler(TimeSpan timeout, string provider, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(provider);

        _timeout = timeout;
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timer = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        HttpResponseMessage? response = null;

        try
        {
            response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);

            // Read the body now, inside the limit, so a server that sends headers and then stalls
            // cannot hold the caller for ever.
            await response.Content.LoadIntoBufferAsync(linked.Token).ConfigureAwait(false);

            return response;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();

            throw new TaskCanceledException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_provider} did not answer within {_timeout.TotalSeconds:0} s."));
        }
        catch
        {
            response?.Dispose();

            throw;
        }
    }
}
