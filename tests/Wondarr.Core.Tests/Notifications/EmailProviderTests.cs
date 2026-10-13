using System.Text.Json;
using FluentAssertions;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Logging;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Email;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>The email provider: its form, its checks, its secret rule and a send per event.</summary>
public sealed class EmailProviderTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSmtpSender _smtp = new();
    private readonly SecretRegistry _secrets = new();
    private readonly EmailProvider _provider;
    private readonly NotificationService _service;

    public EmailProviderTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        _provider = new EmailProvider(_smtp, _secrets, NullLogger<EmailProvider>.Instance);
        _service = new NotificationService(_database.CreateContext(_time), [_provider], NullLogger<NotificationService>.Instance, _secrets);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void The_fields_flag_the_server_as_the_target_and_the_password_as_the_secret()
    {
        _provider.Fields.Where(field => field.Target).Select(field => field.Name).Should().Equal("host", "port", "security");
        _provider.Fields.Where(field => field.Secret).Select(field => field.Name).Should().Equal("password");
        _provider.Fields.Single(field => field.Name == "security").Options.Should().Equal("startTls", "sslOnConnect", "none");
        _provider.Fields.Single(field => field.Name == "delivery").Options.Should().Equal("each", "digest");
    }

    [Fact]
    public void Good_settings_have_no_failures()
    {
        _provider.Validate(EmailSamples.Settings()).Should().BeEmpty();
        _provider.Validate(EmailSamples.Settings(("port", null), ("digestMinutes", null), ("wondarrUrl", ""), ("username", null))).Should().BeEmpty();
        _provider.Validate(EmailSamples.Settings(("delivery", "digest"), ("digestMinutes", 5))).Should().BeEmpty();
        _provider.Validate(EmailSamples.Settings(("delivery", "digest"), ("digestMinutes", 1440))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("host", "", "host")]
    [InlineData("port", 0, "port")]
    [InlineData("port", 70000, "port")]
    [InlineData("port", "abc", "port")]
    [InlineData("security", "tls13", "security")]
    [InlineData("from", "not-an-address", "From")]
    [InlineData("from", "", "From")]
    [InlineData("to", "", "recipient")]
    [InlineData("to", "me@example.com, nope", "'nope'")]
    [InlineData("wondarrUrl", "wondarr.local", "Wondarr address")]
    [InlineData("wondarrUrl", "ftp://wondarr.local", "Wondarr address")]
    [InlineData("delivery", "weekly", "Delivery")]
    public void Bad_settings_are_reported(string key, object value, string mentions)
    {
        var failures = _provider.Validate(EmailSamples.Settings((key, value)));

        failures.Should().ContainSingle().Which.Should().Contain(mentions);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    public void A_digest_window_outside_5_to_1440_is_refused_only_in_digest_mode(int minutes)
    {
        _provider.Validate(EmailSamples.Settings(("delivery", "digest"), ("digestMinutes", minutes)))
            .Should().ContainSingle().Which.Should().Contain("5 to 1440");

        _provider.Validate(EmailSamples.Settings(("delivery", "each"), ("digestMinutes", minutes))).Should().BeEmpty();
    }

    [Fact]
    public void Only_imports_upgrades_and_failures_are_batched_and_only_in_digest_mode()
    {
        var digest = EmailSamples.Settings(("delivery", "digest"));
        var each = EmailSamples.Settings();

        foreach (var batched in new[] { NotificationEventNames.Import, NotificationEventNames.Upgrade, NotificationEventNames.Failure })
        {
            _provider.Batches(digest, batched).Should().BeTrue(batched);
            _provider.Batches(each, batched).Should().BeFalse(batched);
        }

        foreach (var immediate in new[] { NotificationEventNames.Grab, NotificationEventNames.Health, NotificationEventNames.Update, NotificationEventNames.Test })
        {
            _provider.Batches(digest, immediate).Should().BeFalse(immediate);
        }

        _provider.DigestWindow(digest).Should().Be(TimeSpan.FromMinutes(30));
        _provider.DigestWindow(EmailSamples.Settings(("delivery", "digest"), ("digestMinutes", 90))).Should().Be(TimeSpan.FromMinutes(90));
        _provider.DigestWindow(each).Should().BeNull();
    }

    [Fact]
    public async Task Each_mode_sends_one_email_per_event_through_the_sender()
    {
        await _provider.SendAsync(EmailSamples.Song(id: 1), EmailSamples.Settings(), Token);
        await _provider.SendAsync(EmailSamples.Song(NotificationEventNames.Failure, id: 2), EmailSamples.Settings(), Token);

        _smtp.Sent.Should().HaveCount(2);

        var (endpoint, mail) = _smtp.Sent[0];
        endpoint.Host.Should().Be("smtp.example.com");
        endpoint.Port.Should().Be(587);
        endpoint.Security.Should().Be(SecureSocketOptions.StartTls);
        endpoint.Username.Should().Be("wondarr");
        endpoint.Password.Should().Be("app-password");
        endpoint.ToString().Should().NotContain("app-password");
        mail.From.Mailboxes.Single().Address.Should().Be("wondarr@example.com");
        mail.To.Mailboxes.Select(address => address.Address).Should().Equal("me@example.com", "you@example.com");
        _smtp.Sent[1].Message.Subject.Should().StartWith("Wondarr: failed");
    }

    [Theory]
    [InlineData("none", SecureSocketOptions.None)]
    [InlineData("sslOnConnect", SecureSocketOptions.SslOnConnect)]
    [InlineData("startTls", SecureSocketOptions.StartTls)]
    public async Task The_security_choice_picks_the_socket_option(string security, SecureSocketOptions expected)
    {
        await _provider.SendAsync(EmailSamples.Song(), EmailSamples.Settings(("security", security), ("username", null)), Token);

        _smtp.Sent.Single().Endpoint.Security.Should().Be(expected);
        _smtp.Sent.Single().Endpoint.Username.Should().BeNull();
    }

    [Fact]
    public async Task The_test_button_sends_a_short_test_email_and_reports_a_failure_without_the_password()
    {
        var draft = Draft(EmailSamples.Settings());

        var ok = await _service.TestAsync(draft, Token);

        ok.Success.Should().BeTrue();
        _smtp.Sent.Single().Message.Subject.Should().Be("Wondarr: Test notification from Wondarr");

        _smtp.Throw = new NotificationSendException("the mail server rejected the username or password.");

        var failed = await _service.TestAsync(draft, Token);

        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("rejected").And.NotContain("app-password");
    }

    [Fact]
    public async Task The_password_is_masked_on_read_and_kept_for_the_same_server()
    {
        var created = await _service.CreateAsync(Draft(EmailSamples.Settings()), Token);

        NotificationSecrets.Masked(NotificationSecrets.Read(created.Settings), _provider.Fields)
            .ToJsonString().Should().Contain(NotificationSecrets.Mask).And.NotContain("app-password");

        var updated = await _service.UpdateAsync(
            created.Id,
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), ("from", "other@example.com"))),
            Token);

        NotificationSecrets.Read(updated!.Settings).GetProperty("password").GetString().Should().Be("app-password");
    }

    [Theory]
    [InlineData("host", "attacker.example")]
    [InlineData("port", 25)]
    [InlineData("security", "none")]
    public async Task A_changed_target_requires_the_password_again(string key, object value)
    {
        var created = await _service.CreateAsync(Draft(EmailSamples.Settings()), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(() => _service.UpdateAsync(
            created.Id,
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), (key, value))),
            Token));

        failure.Field.Should().Be("password");
        failure.Detail.Should().EndWith("again: the address changed.");

        var test = await Assert.ThrowsAsync<NotificationValidationException>(() => _service.TestAsync(
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), (key, value)), created.Id),
            Token));

        test.Field.Should().Be("password");
        _smtp.Sent.Should().BeEmpty("the stored password must not go to another server");
    }

    [Fact]
    public async Task The_same_server_spelled_differently_keeps_the_password()
    {
        var created = await _service.CreateAsync(Draft(EmailSamples.Settings()), Token);

        var updated = await _service.UpdateAsync(
            created.Id,
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), ("host", "SMTP.Example.COM"))),
            Token);

        NotificationSecrets.Read(updated!.Settings).GetProperty("password").GetString().Should().Be("app-password");
    }

    [Fact]
    public void The_settings_text_never_shows_the_password()
    {
        var text = EmailSettings.Read(EmailSamples.Settings()).ToString();

        text.Should().NotContain("app-password").And.Contain("smtp.example.com");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("file:///etc/passwd")]
    public async Task A_wondarr_address_that_is_not_http_or_https_is_refused_on_save(string url)
    {
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(EmailSamples.Settings(("wondarrUrl", url))), Token));

        failure.Field.Should().Be("settings");
        failure.Detail.Should().Contain("http or https");
    }

    [Fact]
    public async Task A_send_registers_the_password_so_an_exception_naming_it_is_redacted_by_the_formatter()
    {
        var registry = new SecretRegistry();
        var provider = new EmailProvider(_smtp, registry, NullLogger<EmailProvider>.Instance);

        await provider.SendAsync(EmailSamples.Song(), EmailSamples.Settings(("password", "hunter2-hunter2")), Token);

        var output = new StringWriter();
        var formatter = new RedactingTextFormatter(new MessageTemplateTextFormatter("{Message}{NewLine}{Exception}"), registry);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            new InvalidOperationException("login failed for password hunter2-hunter2"),
            new MessageTemplateParser().Parse("failed with hunter2-hunter2"),
            []);

        formatter.Format(logEvent, output);

        output.ToString().Should().NotContain("hunter2-hunter2").And.Contain("(removed)");
    }

    [Fact]
    public async Task Saving_and_testing_register_the_password()
    {
        var created = await _service.CreateAsync(Draft(EmailSamples.Settings(("password", "saved-secret-1"))), Token);

        _secrets.Redact("x saved-secret-1 y").Should().NotContain("saved-secret-1");

        await _service.TestAsync(Draft(EmailSamples.Settings(("password", "tested-secret-2")), created.Id), Token);

        _secrets.Redact("x tested-secret-2 y").Should().NotContain("tested-secret-2");
        created.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task At_startup_the_stored_passwords_are_registered()
    {
        await _service.CreateAsync(Draft(EmailSamples.Settings(("password", "stored-secret-3"))), Token);

        var fresh = new SecretRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<Wondarr.Core.Persistence.WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());

        await using var provider = services.BuildServiceProvider();
        var registrar = new NotificationSecretRegistrar(
            provider.GetRequiredService<IServiceScopeFactory>(),
            [_provider],
            fresh);

        await registrar.StartAsync(Token);

        fresh.Redact("x stored-secret-3 y").Should().NotContain("stored-secret-3");
    }

    [Fact]
    public async Task Leaving_the_port_empty_is_the_same_target_as_typing_587()
    {
        var created = await _service.CreateAsync(Draft(EmailSamples.Settings(("port", null))), Token);

        var updated = await _service.UpdateAsync(
            created.Id,
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), ("port", 587))),
            Token);

        NotificationSecrets.Read(updated!.Settings).GetProperty("password").GetString().Should().Be("app-password");

        var other = await Assert.ThrowsAsync<NotificationValidationException>(() => _service.UpdateAsync(
            created.Id,
            Draft(EmailSamples.Settings(("password", NotificationSecrets.Mask), ("port", 465))),
            Token));

        other.Field.Should().Be("password");
    }

    private static NotificationDraft Draft(JsonElement settings, long? id = null) =>
        new("Mail", "Email", Enabled: true, [NotificationEventNames.Import], settings, id);
}
