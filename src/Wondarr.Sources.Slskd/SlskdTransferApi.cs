using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// slskd refused to enqueue a download. It is not retried here: a rejection is either a bad request
/// or slskd asking Wondarr to slow down, and both need the caller to decide.
/// </summary>
public sealed class SlskdEnqueueException : Exception
{
    /// <summary>Message used when slskd answers 429.</summary>
    public const string RateLimitedMessage = "slskd is rate limiting enqueues";

    /// <summary>The longest piece of slskd's own text that is quoted back.</summary>
    public const int MaxQuotedMessageLength = 300;

    /// <summary>Initialises a new instance of the <see cref="SlskdEnqueueException"/> class.</summary>
    /// <param name="statusCode">The status slskd answered with.</param>
    /// <param name="message">The message to carry, without the status prefix.</param>
    public SlskdEnqueueException(HttpStatusCode statusCode, string message)
        : base($"slskd refused the enqueue ({statusCode}): {message}")
    {
        StatusCode = statusCode;
    }

    /// <summary>The status slskd answered with.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// slskd's transfer endpoints. Wondarr only ever downloads, and only ever one file at a time, but
/// every call here is deliberately general: the queue tracker reads transfers it did not enqueue.
/// </summary>
public interface ISlskdTransferApi
{
    /// <summary>Enqueues a batch of downloads (<c>POST /api/v0/transfers/downloads/batches</c>).</summary>
    /// <exception cref="SlskdEnqueueException">slskd refused the batch, or is rate limiting.</exception>
    Task<SlskdEnqueueBatchResponse> EnqueueAsync(
        SlskdEnqueueBatchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads one transfer (<c>GET /api/v0/transfers/downloads/{username}/{id}</c>); <c>null</c> when slskd no longer has it.</summary>
    Task<SlskdTransfer?> GetAsync(string username, Guid id, CancellationToken cancellationToken);

    /// <summary>Reads every transfer slskd knows about (<c>GET /api/v0/transfers/downloads</c>).</summary>
    Task<IReadOnlyList<SlskdUserTransfers>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the peer where the transfer sits in its queue
    /// (<c>GET /api/v0/transfers/downloads/{username}/{id}/position</c>); <c>null</c> when slskd does
    /// not know the transfer or the peer has not answered.
    /// </summary>
    Task<int?> GetPlaceInQueueAsync(string username, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a transfer (<c>DELETE /api/v0/transfers/downloads/{username}/{id}?remove=…</c>). A
    /// transfer slskd no longer has is the same outcome as cancelling it.
    /// </summary>
    Task CancelAsync(string username, Guid id, bool remove, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SlskdTransferApi : ISlskdTransferApi
{
    /// <summary>Path of slskd's download-transfer collection (slskd's own API version, not Wondarr's).</summary>
    public const string DownloadsPath = "/api/v0/transfers/downloads";

    /// <summary>Path batches are created at.</summary>
    public const string BatchesPath = DownloadsPath + "/batches";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SlskdSecretsStore _secrets;

    /// <summary>Initialises a new instance of the <see cref="SlskdTransferApi"/> class.</summary>
    /// <param name="http">The typed client, whose base address is slskd's loopback API.</param>
    /// <param name="options">Soulseek settings, for the port slskd listens on.</param>
    /// <param name="secrets">Source of the API key this client authenticates with.</param>
    public SlskdTransferApi(HttpClient http, IOptionsMonitor<SoulseekOptions> options, SlskdSecretsStore secrets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        _http = http;
        _secrets = secrets;

        // The bundled slskd is always on loopback; the port is the only part that moves.
        _http.BaseAddress ??= new Uri($"http://127.0.0.1:{options.CurrentValue.WebPort}/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<SlskdEnqueueBatchResponse> EnqueueAsync(
        SlskdEnqueueBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, BatchesPath)
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SlskdEnqueueException(HttpStatusCode.TooManyRequests, SlskdEnqueueException.RateLimitedMessage);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw new SlskdEnqueueException(response.StatusCode, await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false));
        }

        // 201: everything enqueued. 207: some of it. 200: none of it — the caller reads Failures.
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.MultiStatus)
        {
            var result = await response.Content
                .ReadFromJsonAsync<SlskdEnqueueBatchResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return result ?? new SlskdEnqueueBatchResponse();
        }

        response.EnsureSuccessStatusCode();

        // Unreachable in practice: EnsureSuccessStatusCode throws for anything not handled above.
        return new SlskdEnqueueBatchResponse();
    }

    /// <inheritdoc />
    public async Task<SlskdTransfer?> GetAsync(string username, Guid id, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, TransferPath(username, id));

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<SlskdTransfer>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlskdUserTransfers>> ListAsync(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, DownloadsPath);

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var transfers = await response.Content
            .ReadFromJsonAsync<List<SlskdUserTransfers>>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return transfers ?? [];
    }

    /// <inheritdoc />
    public async Task<int?> GetPlaceInQueueAsync(string username, Guid id, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{TransferPath(username, id)}/position");

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            // Normally a bare number.
            return JsonSerializer.Deserialize<int?>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            // …but some slskd builds answer with the whole transfer.
            try
            {
                return JsonSerializer.Deserialize<SlskdTransfer>(body, SerializerOptions)?.PlaceInQueue;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(string username, Guid id, bool remove, CancellationToken cancellationToken)
    {
        var path = $"{TransferPath(username, id)}?remove={(remove ? "true" : "false")}";
        using var message = new HttpRequestMessage(HttpMethod.Delete, path);

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);

        // 204 when it was there, 404 when someone got to it first: both mean it is gone.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// slskd's message for a refused enqueue, when it sent one as a plain JSON string. Anything
    /// else — a problem-details object, an empty body, a wall of text — is dropped rather than
    /// quoted, so a response can never put an unbounded or structured body into an exception.
    /// </summary>
    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return "the request was rejected";
        }

        try
        {
            var text = JsonSerializer.Deserialize<string>(body, SerializerOptions);

            return text is { Length: > 0 and <= SlskdEnqueueException.MaxQuotedMessageLength }
                ? text
                : "the request was rejected";
        }
        catch (JsonException)
        {
            return "the request was rejected";
        }
    }

    private static string TransferPath(string username, Guid id) =>
        $"{DownloadsPath}/{Uri.EscapeDataString(username)}/{id:D}";

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var secrets = await _secrets.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        message.Headers.Add(SlskdClient.ApiKeyHeader, secrets.ApiKey);

        return await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}
