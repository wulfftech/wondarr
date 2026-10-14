using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Wondarr.Core.Notifications.Email;

/// <summary>
/// Sends Wondarr's events as email over SMTP: one message per event, or, for completed downloads, imports, upgrades and
/// failures, a digest of everything in a window (see <see cref="NotificationDigestService"/>). Grabs,
/// health and update messages are always sent at once.
/// </summary>
public sealed partial class EmailProvider : IDigestNotificationProvider
{
    private static readonly HashSet<string> Batched =
        new(
            [
                NotificationEventNames.Download,
                NotificationEventNames.Import,
                NotificationEventNames.Upgrade,
                NotificationEventNames.Failure,
            ],
            StringComparer.Ordinal);

    private readonly ISmtpSender _sender;
    private readonly Wondarr.Core.Logging.ISecretRegistry _secrets;
    private readonly ILogger<EmailProvider> _logger;

    /// <summary>Initialises a new instance of the <see cref="EmailProvider"/> class.</summary>
    /// <param name="sender">Hands a finished message to the SMTP server.</param>
    /// <param name="secrets">The redactor's registry; the password is added before every send.</param>
    /// <param name="logger">The log sink; the password and the bodies are never logged.</param>
    public EmailProvider(ISmtpSender sender, Wondarr.Core.Logging.ISecretRegistry secrets, ILogger<EmailProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _secrets = secrets;
        _sender = sender;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Implementation => "Email";

    /// <inheritdoc />
    public IReadOnlyList<NotificationField> Fields { get; } =
    [
        new("host", "SMTP host", "text", Required: true, HelpText: "The mail server, for example smtp.gmail.com.", Target: true),
        new("port", "Port", "number", Required: false, HelpText: "587 for STARTTLS, 465 for SSL on connect. Empty means 587.", Target: true, TargetDefault: "587"),
        new(
            "security",
            "Security",
            "select",
            Required: false,
            HelpText: "startTls upgrades a plain connection, sslOnConnect is encrypted from the start, none is unencrypted.",
            Options: EmailSecurity.All,
            Target: true),
        new("username", "Username", "text", Required: false, HelpText: "Leave empty when the server needs no login."),
        new("password", "Password", "password", Required: false, Secret: true, HelpText: "For Gmail and Fastmail, an app password."),
        new("from", "From", "text", Required: true, HelpText: "The sender address, for example wondarr@example.com."),
        new("to", "To", "text", Required: true, HelpText: "One or more recipient addresses, separated by commas."),
        new(
            "wondarrUrl",
            "Wondarr address",
            "url",
            Required: false,
            HelpText: "For example https://wondarr.example.com. When set, each song in an email links to its page."),
        new(
            "delivery",
            "Delivery",
            "select",
            Required: false,
            HelpText: "each sends one email per song. digest sends one email per window for completed downloads, imports, upgrades and failures.",
            Options: EmailDelivery.All),
        new(
            "digestMinutes",
            "Digest window (minutes)",
            "number",
            Required: false,
            HelpText: "Between 5 and 1440; empty means 30. Used only when Delivery is digest."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(JsonElement settings)
    {
        var parsed = EmailSettings.Read(settings);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            failures.Add("The SMTP host is required.");
        }

        if (parsed.Port is not (>= 1 and <= 65535))
        {
            failures.Add("The port must be a number from 1 to 65535.");
        }

        if (!EmailSecurity.All.Contains(parsed.Security, StringComparer.Ordinal))
        {
            failures.Add("The security setting must be startTls, sslOnConnect or none.");
        }

        if (!EmailSettings.TryAddress(parsed.From, out _))
        {
            failures.Add("The From address is not a valid email address.");
        }

        var recipients = EmailSettings.ParseRecipients(parsed.To, out var invalid);

        if (recipients.Count == 0 && invalid.Count == 0)
        {
            failures.Add("Enter at least one recipient address.");
        }

        foreach (var address in invalid)
        {
            failures.Add($"'{address}' is not a valid email address.");
        }

        if (!string.IsNullOrWhiteSpace(parsed.WondarrUrl) &&
            (!Uri.TryCreate(parsed.WondarrUrl, UriKind.Absolute, out var url) ||
             (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add("The Wondarr address must be an absolute http or https URL.");
        }

        if (!EmailDelivery.All.Contains(parsed.Delivery, StringComparer.Ordinal))
        {
            failures.Add("Delivery must be each or digest.");
        }
        else if (parsed.IsDigest &&
            parsed.DigestMinutes is not (>= EmailSettings.MinDigestMinutes and <= EmailSettings.MaxDigestMinutes))
        {
            failures.Add("The digest window must be a number from 5 to 1440 minutes.");
        }

        return failures;
    }

    /// <inheritdoc />
    public bool Batches(JsonElement settings, string eventName) =>
        Batched.Contains(eventName) && EmailSettings.Read(settings).IsDigest;

    /// <inheritdoc />
    public TimeSpan? DigestWindow(JsonElement settings)
    {
        var parsed = EmailSettings.Read(settings);

        return parsed.IsDigest
            ? TimeSpan.FromMinutes(parsed.DigestMinutes is { } minutes && minutes >= EmailSettings.MinDigestMinutes
                ? Math.Min(minutes, EmailSettings.MaxDigestMinutes)
                : EmailSettings.DefaultDigestMinutes)
            : null;
    }

    /// <inheritdoc />
    public async Task SendAsync(NotificationMessage message, JsonElement settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parsed = EmailSettings.Read(settings);
        _secrets.Register(parsed.Password);
        var (endpoint, from, to) = Resolve(parsed);

        var mime = EmailMessageBuilder.Single(message, parsed, from, to);

        await _sender.SendAsync(endpoint, mime, cancellationToken).ConfigureAwait(false);

        LogSent(_logger, message.Event, to.Count);
    }

    /// <inheritdoc />
    public async Task SendDigestAsync(
        IReadOnlyList<DigestEntry> entries,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeZoneInfo zone,
        JsonElement settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var parsed = EmailSettings.Read(settings);
        _secrets.Register(parsed.Password);
        var (endpoint, sender, recipients) = Resolve(parsed);

        var mime = EmailMessageBuilder.Digest(entries, windowStart, windowEnd, zone, parsed, sender, recipients);

        await _sender.SendAsync(endpoint, mime, cancellationToken).ConfigureAwait(false);

        LogDigestSent(_logger, entries.Count, recipients.Count);
    }

    /// <summary>The server, sender and recipients of a notification, or a send failure naming what is missing.</summary>
    private static (SmtpEndpoint Endpoint, MailboxAddress From, IReadOnlyList<MailboxAddress> To) Resolve(EmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is not (>= 1 and <= 65535))
        {
            throw new NotificationSendException("the email notification has no usable host and port.");
        }

        if (!EmailSettings.TryAddress(settings.From, out var from))
        {
            throw new NotificationSendException("the email notification has no valid From address.");
        }

        var to = EmailSettings.ParseRecipients(settings.To, out _);

        if (to.Count == 0)
        {
            throw new NotificationSendException("the email notification has no valid recipient.");
        }

        return (
            new SmtpEndpoint(
                settings.Host,
                settings.Port.Value,
                EmailSecurity.Options(settings.Security),
                string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username,
                settings.Password),
            from,
            to);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent the {Event} email to {Recipients} recipient(s)")]
    private static partial void LogSent(ILogger logger, string @event, int recipients);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent a digest email of {Entries} event(s) to {Recipients} recipient(s)")]
    private static partial void LogDigestSent(ILogger logger, int entries, int recipients);
}
