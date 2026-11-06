using FluentAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NSubstitute;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Email;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>The one retry a temporary SMTP refusal (4xx) gets, and the permanent ones that get none.</summary>
public sealed class SmtpSenderTests
{
    private static readonly SmtpEndpoint Endpoint = new("mail.example", 587, SecureSocketOptions.StartTls, null, null);

    private readonly List<TimeSpan> _waits = [];

    [Fact]
    public async Task A_temporary_refusal_is_sent_again_once_after_a_wait()
    {
        var client = Client(Refused(SmtpStatusCode.MailboxBusy), Accepted());

        await Sender(client).SendAsync(Endpoint, Message(), CancellationToken.None);

        await client.Received(2).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress?>());
        _waits.Should().Equal(SmtpSender.RetryDelay);
    }

    [Fact]
    public async Task A_second_temporary_refusal_fails_the_send()
    {
        var client = Client(Refused(SmtpStatusCode.MailboxBusy), Refused(SmtpStatusCode.MailboxBusy));

        var act = () => Sender(client).SendAsync(Endpoint, Message(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotificationSendException>())
            .WithMessage("the mail server refused the message (450).");
        await client.Received(2).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress?>());
    }

    [Fact]
    public async Task A_permanent_refusal_is_not_retried()
    {
        var client = Client(Refused(SmtpStatusCode.MailboxUnavailable), Accepted());

        var act = () => Sender(client).SendAsync(Endpoint, Message(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotificationSendException>())
            .WithMessage("the mail server refused the message (550).");
        await client.Received(1).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress?>());
        _waits.Should().BeEmpty();
    }

    private SmtpSender Sender(ISmtpClient client) => new(() => client, (wait, _) =>
    {
        _waits.Add(wait);

        return Task.CompletedTask;
    });

    private static ISmtpClient Client(Func<Task<string>> first, Func<Task<string>> second)
    {
        var client = Substitute.For<ISmtpClient>();
        client.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress?>())
            .Returns(_ => first(), _ => second());

        return client;
    }

    private static Func<Task<string>> Refused(SmtpStatusCode status) =>
        () => Task.FromException<string>(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, status, "not now"));

    private static Func<Task<string>> Accepted() => () => Task.FromResult("2.0.0 OK");

    private static MimeMessage Message()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Wondarr", "wondarr@example.com"));
        message.To.Add(new MailboxAddress("Owner", "owner@example.com"));
        message.Subject = "Wondarr: test";
        message.Body = new TextPart("plain") { Text = "test" };

        return message;
    }
}
