using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Wondarr.Core.Notifications.Email;

/// <summary>Where and how to log in to an SMTP server. Its text form never shows the password.</summary>
public sealed class SmtpEndpoint
{
    /// <summary>Initialises a new instance of the <see cref="SmtpEndpoint"/> class.</summary>
    /// <param name="host">The server.</param>
    /// <param name="port">The port.</param>
    /// <param name="security">How the connection is secured.</param>
    /// <param name="username">The login, or <see langword="null"/> for none.</param>
    /// <param name="password">The password, or <see langword="null"/>.</param>
    public SmtpEndpoint(string host, int port, SecureSocketOptions security, string? username, string? password)
    {
        Host = host;
        Port = port;
        Security = security;
        Username = username;
        Password = password;
    }

    /// <summary>Gets the server.</summary>
    public string Host { get; }

    /// <summary>Gets the port.</summary>
    public int Port { get; }

    /// <summary>Gets how the connection is secured.</summary>
    public SecureSocketOptions Security { get; }

    /// <summary>Gets the login, or <see langword="null"/>.</summary>
    public string? Username { get; }

    /// <summary>Gets the password, or <see langword="null"/>.</summary>
    public string? Password { get; }

    /// <inheritdoc />
    public override string ToString() => string.Concat(Host, ":", Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Sends one finished message over SMTP. A seam so tests need no server.</summary>
public interface ISmtpSender
{
    /// <summary>Connects, logs in when the endpoint has a login, sends and disconnects.</summary>
    /// <param name="endpoint">The server to use.</param>
    /// <param name="message">The message to send; its sender and recipients are set.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="NotificationSendException">The server could not be reached or refused the message.</exception>
    Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken);
}

/// <summary>The MailKit sender. Its errors say what went wrong and never carry the password.</summary>
public sealed class SmtpSender : ISmtpSender
{
    private const int TimeoutMilliseconds = 15_000;

    /// <summary>
    /// How long a send the server answered with a temporary refusal (4xx) waits before its one retry. With
    /// three 15 s steps either side it stays inside <see cref="NotificationHttp.DeliveryBudget"/>.
    /// </summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);

    private readonly Func<ISmtpClient> _clients;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Initialises a new instance of the <see cref="SmtpSender"/> class with MailKit's client.</summary>
    public SmtpSender()
        : this(() => new SmtpClient())
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SmtpSender"/> class with the client a test supplies.</summary>
    /// <param name="clients">Makes the client one send uses; it is disposed afterwards.</param>
    /// <param name="delay">Waits before a retry; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when not given.</param>
    internal SmtpSender(Func<ISmtpClient> clients, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clients = clients;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await SendOnceAsync(endpoint, message, cancellationToken).ConfigureAwait(false);
        }
        catch (NotificationSendException exception) when (IsTemporary(exception))
        {
            // A 4xx reply is SMTP's "try again later" (greylisting, a rate limit). Without a retry an
            // immediate notification was lost: two were, to a 450, on 2026-11-06, while the next digest
            // three minutes later went through.
            await _delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            await SendOnceAsync(endpoint, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the server refused the message only for now: an SMTP 4xx reply.</summary>
    private static bool IsTemporary(NotificationSendException exception) =>
        exception.InnerException is SmtpCommandException command && (int)command.StatusCode is >= 400 and < 500;

    /// <summary>One connection: connect, log in when the endpoint has a login, send and disconnect.</summary>
    private async Task SendOnceAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = _clients();
        client.Timeout = TimeoutMilliseconds;

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(endpoint.Username))
            {
                await client.AuthenticateAsync(endpoint.Username, endpoint.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationException exception)
        {
            throw new NotificationSendException("the mail server rejected the username or password.", exception);
        }
        catch (SslHandshakeException exception)
        {
            throw new NotificationSendException(
                "the secure connection to the mail server failed; check the security setting and port.",
                exception);
        }
        catch (SmtpCommandException exception)
        {
            throw new NotificationSendException(
                string.Concat("the mail server refused the message (", ((int)exception.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), ")."),
                exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ServiceNotConnectedException or ProtocolException or TimeoutException)
        {
            throw new NotificationSendException("could not reach the mail server; check the host and port.", exception);
        }
        catch (Exception exception) when (exception is not NotificationSendException)
        {
            // Only the type: a library message could name the server.
            throw new NotificationSendException(string.Concat("sending failed (", exception.GetType().Name, ")."), exception);
        }

        // The server has the message. A failed goodbye is not a failed send, and disposing the client
        // closes the connection anyway.
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Swallowed on purpose; see above.
        }
    }
}
