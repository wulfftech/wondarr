using System.Net;
using System.Net.Http.Headers;

namespace Wondarr.Core.Notifications;

/// <summary>
/// The shared HTTP behaviour of every notification provider: one send, one waited-out rate limit, and
/// a failure that never carries the endpoint.
/// </summary>
public static class NotificationHttp
{
    /// <summary>The name of the named <see cref="HttpClient"/> every provider sends through.</summary>
    public const string ClientName = "notifications";

    /// <summary>How long one send may take before it is abandoned.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest <c>Retry-After</c> that is waited out. A longer one is the far end's way of saying
    /// "not now", and a notification is not worth holding the queue for.
    /// </summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most one delivery may take in all: a send, the longest wait a rate limit is honoured for,
    /// and the retry. The dispatcher bounds each delivery by this, not by <see cref="SendTimeout"/>, so a
    /// waited-out <c>Retry-After</c> is not cut short.
    /// </summary>
    public static readonly TimeSpan DeliveryBudget = (SendTimeout * 2) + MaxRetryAfter;

    /// <summary>
    /// Sends one request and throws <see cref="NotificationSendException"/> unless it succeeded. A
    /// <c>429</c> with a <c>Retry-After</c> of at most <see cref="MaxRetryAfter"/> is waited out (on
    /// <paramref name="timeProvider"/>, so tests need no real waiting) and sent once more.
    /// </summary>
    /// <param name="client">The client to send through.</param>
    /// <param name="requestFactory">Builds a fresh request; called again for the retry.</param>
    /// <param name="timeProvider">The clock the wait is measured on.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    public static async Task SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        using var response = await SendOnceAsync(client, requestFactory, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests &&
            RetryAfter(response.Headers, timeProvider) is { } wait &&
            wait <= MaxRetryAfter)
        {
            await Task.Delay(wait, timeProvider, cancellationToken).ConfigureAwait(false);

            using var retried = await SendOnceAsync(client, requestFactory, cancellationToken).ConfigureAwait(false);

            EnsureSuccess(retried);

            return;
        }

        EnsureSuccess(response);
    }

    /// <summary>The wait a <c>Retry-After</c> header asks for, in either of its two forms.</summary>
    private static TimeSpan? RetryAfter(HttpResponseHeaders headers, TimeProvider timeProvider)
    {
        var value = headers.RetryAfter;

        if (value?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (value?.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();

            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var request = requestFactory();

        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (NotificationSendException)
        {
            request.Dispose();

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The transport's own message names the host, so only its type is carried across.
            request.Dispose();

            throw new NotificationSendException(
                string.Concat("the request failed (", exception.GetType().Name, ")"),
                exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new NotificationSendException(string.Concat(
            "the endpoint answered HTTP ",
            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            " (",
            response.ReasonPhrase ?? response.StatusCode.ToString(),
            ")"));
    }
}
