using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wondarr.Api.Extensions;
using Wondarr.Core.Messaging;
using Wondarr.Sources.Slskd;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Slskd;

/// <summary>
/// Receives slskd's <c>DownloadFileComplete</c> webhook and turns it into an event.
/// <para>
/// The endpoint cannot require Wondarr's API key, because slskd does not have one: it is
/// authenticated instead by a token Wondarr generates and writes into <c>slskd.yml</c>, and it only
/// answers requests that arrived on loopback, which is where the bundled slskd always runs.
/// </para>
/// <para>
/// Nothing here is authoritative. The queue tracker polls the transfers API; this only lets it react
/// sooner, so an event that is missed or ignored costs latency and never correctness.
/// </para>
/// </summary>
[AllowAnonymous]
[ApiController]
public sealed partial class SlskdWebhookController : ControllerBase
{
    /// <summary>Header slskd sends the generated token in.</summary>
    public const string HeaderName = SlskdConfigRenderer.WebhookHeaderName;

    /// <summary>The only event Wondarr acts on.</summary>
    public const string CompletedEventType = SlskdConfigRenderer.WebhookEvent;

    /// <summary>Wondarr's own route, under the API's versioned prefix.</summary>
    public const string Route = "/api/v1/slskd/webhook";

    private readonly SlskdSecretsStore _secrets;
    private readonly IEventAggregator _events;
    private readonly ILogger<SlskdWebhookController> _logger;

    /// <summary>Initialises a new instance of the <see cref="SlskdWebhookController"/> class.</summary>
    /// <param name="secrets">Source of the webhook token this endpoint authenticates with.</param>
    /// <param name="events">Where a completed download is published.</param>
    /// <param name="logger">Debug lines; never the token and never the body.</param>
    public SlskdWebhookController(
        SlskdSecretsStore secrets,
        IEventAggregator events,
        ILogger<SlskdWebhookController> logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);

        _secrets = secrets;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Accepts one slskd event. Answers 204 whatever it decides to do with the body, so slskd never
    /// retries an event Wondarr did not want in the first place.
    /// </summary>
    [HttpPost(Route)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Bundled slskd is always on loopback; anything else did not come from it.
        var remote = HttpContext.GetRemoteIp();

        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            LogRejectedAddress(_logger, remote);

            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!await HasValidTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            // No detail: the caller is not entitled to know how close it was.
            return StatusCode(StatusCodes.Status401Unauthorized);
        }

        JsonElement body;

        try
        {
            using var document = await JsonDocument
                .ParseAsync(Request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // The document is disposed with the using; the event needs the values, not the reader.
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            LogUnreadableBody(_logger);

            return NoContent();
        }

        var type = ReadString(body, "type");

        if (!string.Equals(type, CompletedEventType, StringComparison.Ordinal))
        {
            LogIgnoredEvent(_logger, type);

            return NoContent();
        }

        body.TryGetProperty("transfer", out var transfer);

        var username = ReadString(transfer, "username") ?? string.Empty;
        var id = ReadGuid(transfer, "id");

        LogCompleted(_logger, id, username);

        await _events
            .PublishAsync(
                new SlskdDownloadCompletedEvent(
                    username,
                    id,
                    ReadString(body, "remoteFilename") ?? string.Empty,
                    ReadString(body, "localFilename") ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Compares the header with the stored token in constant time over the UTF-8 bytes, so a wrong
    /// token cannot be recovered byte by byte from how long the answer took.
    /// </summary>
    private async Task<bool> HasValidTokenAsync(CancellationToken cancellationToken)
    {
        var provided = Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var secrets = await _secrets.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var expected = secrets.WebhookToken;

        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }

    /// <summary>A string property of a loosely parsed body, or <c>null</c> when it is absent or another type.</summary>
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A GUID property of a loosely parsed body; <see cref="Guid.Empty"/> when it is absent.</summary>
    private static Guid ReadGuid(JsonElement element, string name) =>
        ReadString(element, name) is { } text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a slskd webhook from {RemoteAddress}: it is not a loopback address")]
    private static partial void LogRejectedAddress(ILogger logger, IPAddress? remoteAddress);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a slskd webhook with a body that is not JSON")]
    private static partial void LogUnreadableBody(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a slskd {EventType} event; the queue tracker only wants DownloadFileComplete")]
    private static partial void LogIgnoredEvent(ILogger logger, string? eventType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "slskd reported transfer {TransferId} for {Username} as complete")]
    private static partial void LogCompleted(ILogger logger, Guid transferId, string username);
}