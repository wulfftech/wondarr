using System.Net.Http.Json;

namespace FakeSlskd;

/// <summary>The <c>DownloadFileComplete</c> event slskd posts to its configured webhooks.</summary>
/// <param name="Type">The event's name.</param>
/// <param name="Id">The event's id.</param>
/// <param name="Timestamp">When it fired.</param>
/// <param name="LocalFilename">Where the file ended up on disk.</param>
/// <param name="RemoteFilename">The peer's own path for the file.</param>
/// <param name="Transfer">The finished transfer.</param>
public sealed record DownloadFileCompleteEvent(
    string Type,
    Guid Id,
    DateTime Timestamp,
    string LocalFilename,
    string RemoteFilename,
    SlskdTransferResource Transfer)
{
    /// <summary>The event's name, as configured in <c>integrations.webhooks.*.on</c>.</summary>
    public const string EventName = "DownloadFileComplete";
}

/// <summary>
/// Posts <c>DownloadFileComplete</c> to the webhooks the rendered <c>slskd.yml</c> configures, exactly
/// as slskd does: a POST whose body is the event as JSON, with the configured headers. A webhook that
/// cannot be reached is logged and otherwise ignored — the fake must not fail a download over it.
/// </summary>
public sealed class WebhookSender : IDisposable
{
    private readonly IReadOnlyList<WebhookTarget> _targets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Initialises a new instance of the <see cref="WebhookSender"/> class.</summary>
    /// <param name="targets">The configured webhooks.</param>
    public WebhookSender(IReadOnlyList<WebhookTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        _targets = targets;
    }

    /// <summary>Posts <paramref name="payload"/> to every webhook that listens for its event.</summary>
    /// <param name="payload">The event.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task SendAsync(DownloadFileCompleteEvent payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        foreach (var target in _targets.Where(t => t.Listens(DownloadFileCompleteEvent.EventName)))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target.Url)
                {
                    Content = JsonContent.Create(payload),
                };

                foreach (var (name, value) in target.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    FakeSlskdLog.Error($"Webhook {target.Name} answered {(int)response.StatusCode}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // slskd retries a failed webhook; the fake only has to keep the failure out of the
                // download's way.
                FakeSlskdLog.Error($"Webhook {target.Name} failed: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}