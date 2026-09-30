// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Apprise/Apprise.cs, GPL-3.0.
// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Notifications/Apprise/AppriseProxy.cs, GPL-3.0.
// Adapted for Wondarr: `IHttpClientFactory` and `System.Text.Json` instead of NzbDrone's `IHttpClient`,
// no NLog or `AppriseError` parsing, one message per send rather than an event per method, and the
// retry and error handling of `NotificationHttp` in place of Lidarr's own.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Notifications.Apprise;

/// <summary>
/// Posts Wondarr's events to an Apprise API server, which fans each one out to the hundred-odd services
/// Apprise speaks for.
/// </summary>
/// <remarks>
/// The notification sends either through a persistent-storage configuration key
/// (<c>POST {server}/notify/{key}</c>) or statelessly (<c>POST {server}/notify</c> with the service URLs
/// in the body); the settings say which, and validation accepts exactly one of the two.
/// </remarks>
public sealed partial class AppriseProvider : INotificationProvider
{
    /// <summary>The wire format: Apprise's own camelCase property names, nothing written for an absent value.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<AppriseProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="AppriseProvider"/> class.</summary>
    /// <param name="clients">The factory the named <see cref="NotificationHttp.ClientName"/> client comes from.</param>
    /// <param name="time">The clock a rate-limit wait is measured on.</param>
    /// <param name="logger">The log sink; a send failure is a debug line, the dispatcher warns.</param>
    public AppriseProvider(
        IHttpClientFactory clients,
        TimeProvider time,
        ILogger<AppriseProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Implementation => "Apprise";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new(
            "serverUrl",
            "Apprise Server URL",
            "url",
            Required: true,
            HelpText: "The Apprise API server, including http(s):// and the port."),
        new(
            "configurationKey",
            "Configuration Key",
            "text",
            Required: false,
            HelpText: "The persistent-storage key to notify, for example 'wondarr'. Leave empty when using stateless URLs."),
        new(
            "statelessUrls",
            "Stateless URLs",
            "text",
            Required: false,
            Secret: true,
            HelpText: "One or more service URLs, separated by commas. They carry the service tokens, so they are never shown again. Leave empty when using a configuration key."),
        new(
            "notificationType",
            "Notification Type",
            "select",
            Required: false,
            HelpText: "How Apprise tags the notification.",
            Options: AppriseNotificationTypes.All),
        new(
            "tags",
            "Tags",
            "text",
            Required: false,
            HelpText: "Notify only the services carrying these tags, separated by commas."),
        new("authUsername", "Username", "text", Required: false, HelpText: "For HTTP basic authentication."),
        new("authPassword", "Password", "password", Required: false, Secret: true, HelpText: "For HTTP basic authentication."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = AppriseSettings.From(settings);
        var failures = new List<string>();

        if (!AppriseSettings.TryServerUrl(parsed.ServerUrl, out _))
        {
            failures.Add("The server URL must be an absolute http or https address.");
        }

        if (parsed.HasConfigurationKey == parsed.HasStatelessUrls)
        {
            failures.Add("Use either Configuration Key or Stateless URLs.");
        }

        if (!AppriseNotificationTypes.All.Contains(parsed.NotificationType, StringComparer.Ordinal))
        {
            failures.Add("The notification type must be info, success, warning or failure.");
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

        var parsed = AppriseSettings.From(settings);

        if (!AppriseSettings.TryServerUrl(parsed.ServerUrl, out var server))
        {
            throw new NotificationSendException("the Apprise server has no usable URL.");
        }

        if (parsed.HasConfigurationKey == parsed.HasStatelessUrls)
        {
            throw new NotificationSendException("the Apprise notification needs either a configuration key or stateless URLs, and has neither or both.");
        }

        var body = JsonSerializer.Serialize(Payload(message, parsed), PayloadJson);
        var client = _clients.CreateClient(NotificationHttp.ClientName);

        await NotificationHttp
            .SendAsync(client, () => Request(server, parsed, body), _time, cancellationToken)
            .ConfigureAwait(false);

        LogSent(_logger, message.Event);
    }

    /// <summary>Builds the body of one message.</summary>
    private static ApprisePayload Payload(NotificationMessage message, AppriseSettings settings) => new()
    {
        Urls = settings.HasStatelessUrls ? settings.StatelessUrls!.Trim() : null,
        Title = message.Title,
        Body = message.Body,
        Type = settings.NotificationType,
        Tag = settings.Tags.Count == 0 ? null : string.Join(",", settings.Tags),
    };

    /// <summary>
    /// Builds one request. Called again for a retry: an <see cref="HttpRequestMessage"/> and its content
    /// cannot be sent twice.
    /// </summary>
    private static HttpRequestMessage Request(Uri server, AppriseSettings settings, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, NotifyUri(server, settings))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.AuthUsername) || !string.IsNullOrWhiteSpace(settings.AuthPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(string.Concat(settings.AuthUsername ?? string.Empty, ":", settings.AuthPassword ?? string.Empty)));

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return request;
    }

    /// <summary>The <c>/notify</c> address one notification posts to, keyed or stateless.</summary>
    private static Uri NotifyUri(Uri server, AppriseSettings settings)
    {
        var root = server.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return settings.HasConfigurationKey
            ? new Uri(string.Concat(root, "/notify/", Uri.EscapeDataString(settings.ConfigurationKey!.Trim())), UriKind.Absolute)
            : new Uri(string.Concat(root, "/notify"), UriKind.Absolute);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent the {Event} Apprise notification")]
    private static partial void LogSent(ILogger logger, string @event);
}
