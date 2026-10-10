// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/Webhook.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookBase.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookProxy.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Webhook/WebhookEventType.cs, GPL-3.0.
// Adapted for Wondarr: `IHttpClientFactory` and `System.Text.Json` instead of NzbDrone's `IHttpClient`,
// no FluentValidation and no NLog, and one song-level payload rather than an artist/album hierarchy.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Notifications.Webhook;

/// <summary>
/// Posts Wondarr's events to a URL of the user's choosing.
/// </summary>
/// <remarks>
/// The body follows Lidarr's webhook shape so an existing *arr consumer keeps working: <c>eventType</c>
/// carries Lidarr's PascalCase values (<c>Test</c>, <c>Grab</c>, <c>Download</c> for an import or an
/// upgrade, <c>DownloadFailure</c>, <c>Health</c>), <c>instanceName</c> is <c>Wondarr</c>, and
/// <c>applicationUrl</c> is present and null until Wondarr knows its own URL. Where Lidarr sends an
/// artist and an album, Wondarr sends its camelCase <c>song</c> object, plus <c>release</c> on a grab
/// and <c>trackFile</c> on an import. An update payload (<c>eventType</c> <c>Update</c>) carries an
/// <c>update</c> object with <c>currentVersion</c>, <c>latestVersion</c> and <c>releaseUrl</c>. A health payload carries <c>level</c>, <c>message</c>,
/// <c>type</c> and <c>wikiUrl</c> as Lidarr's <c>WebhookHealthPayload</c> does.
/// </remarks>
public sealed partial class WebhookProvider : INotificationProvider
{
    /// <summary>camelCase property names, matching the rest of Wondarr's wire format.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<WebhookProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="WebhookProvider"/> class.</summary>
    /// <param name="clients">The factory the named <see cref="NotificationHttp.ClientName"/> client comes from.</param>
    /// <param name="time">The clock a rate-limit wait is measured on.</param>
    /// <param name="logger">The log sink; a send failure is a debug line, the dispatcher warns.</param>
    public WebhookProvider(
        IHttpClientFactory clients,
        TimeProvider time,
        ILogger<WebhookProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Implementation => "Webhook";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("url", "URL", "url", Required: true, HelpText: "Where the JSON body is sent."),
        new(
            "method",
            "Method",
            "select",
            Required: false,
            HelpText: "Which HTTP method to submit with.",
            Options: WebhookMethods.All),
        new("username", "Username", "text", Required: false, HelpText: "For HTTP basic authentication."),
        new("password", "Password", "password", Required: false, Secret: true, HelpText: "For HTTP basic authentication."),
        new(
            "headers",
            "Headers",
            "keyValueList",
            Required: false,
            HelpText: "Extra headers to send, as name/value pairs. Their values are shown in full here; put credentials in Username and Password, which are not.",
            Advanced: true),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = WebhookSettings.From(settings);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(parsed.Url) ||
            !Uri.TryCreate(parsed.Url.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("The URL must be an absolute http or https address.");
        }

        if (!WebhookMethods.All.Contains(parsed.Method, StringComparer.Ordinal))
        {
            failures.Add("The method must be POST or PUT.");
        }

        foreach (var header in parsed.Headers)
        {
            if (!IsValidHeaderName(header.Key))
            {
                failures.Add($"'{header.Key}' is not a valid HTTP header name.");
            }
        }

        return failures;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        NotificationMessage message,
        JsonElement settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parsed = WebhookSettings.From(settings);

        if (string.IsNullOrWhiteSpace(parsed.Url) || !Uri.TryCreate(parsed.Url.Trim(), UriKind.Absolute, out var url))
        {
            throw new NotificationSendException("the webhook has no usable URL.");
        }

        var body = JsonSerializer.Serialize(Build(message), PayloadJson);
        var client = _clients.CreateClient(NotificationHttp.ClientName);

        await NotificationHttp
            .SendAsync(client, () => Request(parsed, url, body), _time, cancellationToken)
            .ConfigureAwait(false);

        LogSent(_logger, message.Event);
    }

    /// <summary>Builds the body of one message.</summary>
    private static WebhookPayload Build(NotificationMessage message) => message.Event switch
    {
        NotificationEventNames.Grab => Payload(message, WebhookEventTypes.Grab) with
        {
            Release = message.Release is { } release
                ? new WebhookRelease(release.Title, release.SourceType, release.Quality, release.SizeBytes)
                : null,
        },

        NotificationEventNames.Import or NotificationEventNames.Upgrade => Payload(message, WebhookEventTypes.Download) with
        {
            TrackFile = message.File is { } file ? new WebhookTrackFile(file.Path, file.Quality) : null,
            IsUpgrade = message.IsUpgrade,
        },

        NotificationEventNames.Failure => Payload(message, WebhookEventTypes.DownloadFailure) with
        {
            Message = message.Body,
        },

        NotificationEventNames.Health => Payload(message, WebhookEventTypes.Health) with
        {
            Level = message.Health?.Level,
            Message = message.Health?.Message,
            Type = message.Health?.Source,
            WikiUrl = message.Health?.WikiUrl,
        },

        NotificationEventNames.Update => Payload(message, WebhookEventTypes.Update) with
        {
            Message = message.Body,
            Update = message.Update is { } update
                ? new WebhookUpdate(update.CurrentVersion, update.LatestVersion, update.ReleaseUrl)
                : null,
        },

        NotificationEventNames.Test => Payload(message, WebhookEventTypes.Test) with
        {
            Song = new WebhookSong(1, "Test Title", "Test Artist", "Test Album", "00000000-0000-0000-0000-000000000000"),
        },

        _ => throw new NotificationSendException($"no webhook payload exists for the event '{message.Event}'."),
    };

    private static WebhookPayload Payload(NotificationMessage message, string eventType) =>
        new(eventType, "Wondarr")
        {
            Song = message.Song is { } song
                ? new WebhookSong(song.Id, song.Title, song.ArtistCredit, song.AlbumTitle, song.MbRecordingId)
                : null,
        };

    /// <summary>
    /// Builds one request. Called again for a retry: an <see cref="HttpRequestMessage"/> and its content
    /// cannot be sent twice.
    /// </summary>
    private static HttpRequestMessage Request(WebhookSettings settings, Uri url, string body)
    {
        var request = new HttpRequestMessage(
            string.Equals(settings.Method, WebhookMethods.Put, StringComparison.Ordinal) ? HttpMethod.Put : HttpMethod.Post,
            url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.Username) || !string.IsNullOrWhiteSpace(settings.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(string.Concat(settings.Username ?? string.Empty, ":", settings.Password ?? string.Empty)));

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        foreach (var header in settings.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    /// <summary>Whether a name is one HTTP allows in a header.</summary>
    private static bool IsValidHeaderName(string name) =>
        !string.IsNullOrWhiteSpace(name) && new HttpRequestMessage().Headers.TryAddWithoutValidation(name, "probe");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent the {Event} webhook")]
    private static partial void LogSent(ILogger logger, string @event);
}
