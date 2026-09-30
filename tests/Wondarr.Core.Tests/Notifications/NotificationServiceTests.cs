using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The rules a notification is stored under, and the one thing that must never come back out of the
/// API: a stored secret.
/// </summary>
public sealed class NotificationServiceTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeProvider _provider = new();
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        // One context for the whole test: the service is scoped to a request, and a test is one.
        var context = _database.CreateContext(_time);

        _service = new NotificationService(context, [_provider], NullLogger<NotificationService>.Instance);
    }

    [Fact]
    public async Task An_unknown_type_is_rejected_on_the_implementation()
    {
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(implementation: "Carrier pigeon"), Token));

        failure.Field.Should().Be("implementation");
        failure.Detail.Should().Contain("Carrier pigeon");
    }

    [Fact]
    public async Task A_blank_name_is_rejected_on_the_name()
    {
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(name: "   "), Token));

        failure.Field.Should().Be("name");
    }

    [Fact]
    public async Task A_duplicate_name_is_rejected_on_the_name()
    {
        await _service.CreateAsync(Draft(name: "Hooks"), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(name: "Hooks"), Token));

        failure.Field.Should().Be("name");
    }

    [Fact]
    public async Task An_event_Wondarr_does_not_send_is_rejected_on_the_events()
    {
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(events: [NotificationEventNames.Import, "renamed"]), Token));

        failure.Field.Should().Be("events");
        failure.Detail.Should().Contain("renamed");
    }

    [Fact]
    public async Task A_url_the_provider_refuses_is_rejected_on_the_settings()
    {
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.CreateAsync(Draft(url: "ftp://hooks.local"), Token));

        failure.Field.Should().Be("settings");
        failure.Detail.Should().Contain("http");
    }

    [Fact]
    public async Task A_secret_created_once_is_kept_when_the_update_sends_the_mask_back()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var stored = NotificationSecrets.Read(created.Settings);
        stored.GetProperty("password").GetString().Should().Be("wonderwall");

        // What the API hands out, and what a UI that did not touch the password sends back.
        var handedOut = NotificationSecrets.Masked(stored, _provider.Fields);
        handedOut["password"]!.GetValue<string>().Should().Be(NotificationSecrets.Mask);
        handedOut["url"]!.GetValue<string>().Should().Be("http://hooks.local/webhook");

        await _service.UpdateAsync(created.Id, Draft(password: NotificationSecrets.Mask), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_secret_the_update_replaces_is_the_new_one()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        await _service.UpdateAsync(created.Id, Draft(password: "champagne-supernova"), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("champagne-supernova");
    }

    [Fact]
    public async Task An_empty_secret_is_not_masked()
    {
        var masked = NotificationSecrets.Masked(
            NotificationSecrets.Read("""{"url":"http://hooks.local","password":""}"""),
            _provider.Fields);

        masked["password"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_test_reports_the_failure_without_naming_the_endpoint()
    {
        _provider.Throw = new NotificationSendException("the endpoint answered HTTP 500 (Internal Server Error).");

        var result = await _service.TestAsync(Draft(), Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("500");
        result.Error.Should().NotContain("hooks.local");
        _provider.Sent.Single().Title.Should().Be("Test notification from Wondarr");
        _provider.Sent.Single().Event.Should().Be(NotificationEventNames.Test);
    }

    [Fact]
    public async Task A_test_that_works_reports_success()
    {
        var result = await _service.TestAsync(Draft(), Token);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    /// <summary>Deleting a notification that is not there is not an error, it is a miss.</summary>
    [Fact]
    public async Task Deleting_a_missing_notification_reports_a_miss()
    {
        (await _service.DeleteAsync(404, Token)).Should().BeFalse();

        var created = await _service.CreateAsync(Draft(name: "Hooks"), Token);

        (await _service.DeleteAsync(created.Id, Token)).Should().BeTrue();
        (await _service.ListAsync(Token)).Should().BeEmpty();
    }

    /// <inheritdoc />
    public void Dispose() => _database.Dispose();

    private static NotificationDraft Draft(
        string name = "Hooks",
        string implementation = "Test",
        string url = "http://hooks.local/webhook",
        string password = "",
        IReadOnlyList<string>? events = null) =>
        new(
            name,
            implementation,
            Enabled: true,
            events ?? [NotificationEventNames.Import],
            JsonSerializer.Deserialize<JsonElement>(
                $$"""{"url":"{{url}}","password":"{{password}}"}"""));

    /// <summary>A provider with one plain field and one secret, which is all the rules need.</summary>
    private sealed class FakeProvider : INotificationProvider
    {
        private readonly List<NotificationMessage> _sent = [];

        public string Implementation => "Test";

        public IReadOnlyList<NotificationField> Fields { get; } =
        [
            new("url", "URL", "url", Required: true),
            new("password", "Password", "password", Required: false, Secret: true),
        ];

        /// <summary>Thrown by every send when set.</summary>
        public Exception? Throw { get; set; }

        /// <summary>What this provider was sent, in order.</summary>
        public IReadOnlyList<NotificationMessage> Sent => _sent;

        public IReadOnlyList<string> Validate(JsonElement settings)
        {
            var url = settings.TryGetProperty("url", out var value) ? value.GetString() : null;

            return url is not null && url.StartsWith("http", StringComparison.Ordinal)
                ? []
                : ["The URL must be an absolute http or https address."];
        }

        public Task SendAsync(
            NotificationMessage message,
            JsonElement settings,
            CancellationToken cancellationToken)
        {
            _sent.Add(message);

            if (Throw is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
    }
}
