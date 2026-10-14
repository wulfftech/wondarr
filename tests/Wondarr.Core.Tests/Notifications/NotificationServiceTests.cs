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

    // R-038: a stored secret is reused only for the address it was saved for.
    [Fact]
    public async Task An_update_to_a_new_host_with_the_masked_secret_is_refused()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.UpdateAsync(created.Id, Draft(url: "http://attacker.example/hook", password: NotificationSecrets.Mask), Token));

        failure.Field.Should().Be("password");
        failure.Detail.Should().Be("Enter the Password again: the address changed.");

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("url").GetString().Should().Be("http://hooks.local/webhook");
    }

    [Fact]
    public async Task A_test_to_a_new_host_with_the_masked_secret_is_refused_and_sends_nothing()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.TestAsync(
                Draft(url: "http://attacker.example/hook", password: NotificationSecrets.Mask) with { Id = created.Id },
                Token));

        failure.Field.Should().Be("password");
        _provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_test_to_the_same_host_still_reuses_the_stored_secret()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        // Another path, scheme-less case and the explicit default port are the same target.
        var result = await _service.TestAsync(
            Draft(url: "http://HOOKS.local:80/other/path", password: NotificationSecrets.Mask) with { Id = created.Id },
            Token);

        result.Success.Should().BeTrue();
        _provider.SentSettings.Single().GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_new_host_with_the_secret_supplied_works()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        await _service.UpdateAsync(created.Id, Draft(url: "http://new.example/hook", password: "supernova"), Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);
        var stored = NotificationSecrets.Read(row.Settings);

        stored.GetProperty("url").GetString().Should().Be("http://new.example/hook");
        stored.GetProperty("password").GetString().Should().Be("supernova");
    }

    [Fact]
    public async Task A_discord_update_that_sends_the_masked_required_url_back_keeps_it()
    {
        var service = RealService();

        var created = await service.CreateAsync(Discord(DiscordUrl), Token);
        var updated = await service.UpdateAsync(created.Id, Discord(NotificationSecrets.Mask, name: "Renamed"), Token);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Renamed");
        NotificationSecrets.Read(updated.Settings).GetProperty("webHookUrl").GetString().Should().Be(DiscordUrl);
    }

    [Fact]
    public async Task An_apprise_update_that_sends_the_masked_stateless_urls_back_keeps_them()
    {
        var service = RealService();

        var created = await service.CreateAsync(Apprise("json://localhost/token-123"), Token);
        var updated = await service.UpdateAsync(created.Id, Apprise(NotificationSecrets.Mask), Token);

        NotificationSecrets.Read(updated!.Settings).GetProperty("statelessUrls").GetString()
            .Should().Be("json://localhost/token-123");
    }

    [Fact]
    public async Task A_blank_secret_means_keep_when_one_is_stored_and_stays_blank_when_none_is()
    {
        var service = RealService();

        // Stored: blank keeps it, exactly like the mask.
        var created = await service.CreateAsync(Discord(DiscordUrl), Token);
        var kept = await service.UpdateAsync(created.Id, Discord(string.Empty, name: "Blank"), Token);

        NotificationSecrets.Read(kept!.Settings).GetProperty("webHookUrl").GetString().Should().Be(DiscordUrl);

        // Nothing stored: the blank stays blank, and the required-field validation still refuses it.
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => service.CreateAsync(Discord(string.Empty, name: "Empty"), Token));

        failure.Field.Should().Be("settings");

        // Optional secret with nothing stored: blank is stored as blank.
        var plain = await _service.CreateAsync(Draft(name: "Plain", password: string.Empty), Token);

        NotificationSecrets.Read(plain.Settings).GetProperty("password").GetString().Should().BeEmpty();

        // Optional secret with a value stored: blank keeps it.
        var held = await _service.CreateAsync(Draft(name: "Held", password: "wonderwall"), Token);
        var after = await _service.UpdateAsync(held.Id, Draft(name: "Held", password: string.Empty), Token);

        NotificationSecrets.Read(after!.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_blank_secret_is_not_kept_for_a_new_address_and_a_cleared_one_is_removed()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        // Blank is "keep", so it obeys the target rule like the mask does.
        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.UpdateAsync(created.Id, Draft(url: "http://attacker.example/hook", password: string.Empty), Token));

        failure.Field.Should().Be("password");

        // The explicit way to clear is clearSecrets, with a blank alongside.
        await _service.UpdateAsync(created.Id, Draft(password: string.Empty) with { ClearSecrets = ["password"] }, Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).TryGetProperty("password", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Clearing_an_optional_secret_removes_it()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        // What the form sends for a field marked to be cleared: the mask (or nothing) plus the name.
        await _service.UpdateAsync(
            created.Id,
            Draft(password: NotificationSecrets.Mask) with { ClearSecrets = ["password"] },
            Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);
        var stored = NotificationSecrets.Read(row.Settings);

        stored.TryGetProperty("password", out _).Should().BeFalse();
        stored.GetProperty("url").GetString().Should().Be("http://hooks.local/webhook");
        NotificationSecrets.Masked(stored, _provider.Fields).AsObject().ContainsKey("password").Should().BeFalse("nothing is stored to mask");
    }

    [Fact]
    public async Task Clearing_a_secret_wins_over_a_value_sent_with_it_and_needs_no_target_rule()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        // A new host with the mask would be refused (the secret cannot follow it); clearing reuses nothing.
        await _service.UpdateAsync(
            created.Id,
            Draft(url: "http://new.example/hook", password: NotificationSecrets.Mask) with { ClearSecrets = ["password"] },
            Token);

        var other = await _service.CreateAsync(Draft(name: "Other", password: "supernova"), Token);

        await _service.UpdateAsync(
            other.Id,
            Draft(name: "Other", password: "typed-again") with { ClearSecrets = ["password"] },
            Token);

        await using var database = _database.CreateContext(_time);

        foreach (var row in await database.Notifications.ToListAsync(Token))
        {
            NotificationSecrets.Read(row.Settings).TryGetProperty("password", out _).Should().BeFalse(row.Name);
        }

        (await database.Notifications.SingleAsync(row => row.Id == created.Id, Token)).Settings
            .Should().Contain("http://new.example/hook");
    }

    [Fact]
    public async Task A_secret_not_named_in_clearSecrets_is_kept_whether_the_list_is_absent_or_empty()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        await _service.UpdateAsync(created.Id, Draft(password: NotificationSecrets.Mask) with { ClearSecrets = [] }, Token);
        await _service.UpdateAsync(created.Id, Draft(password: NotificationSecrets.Mask) with { ClearSecrets = null }, Token);

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_required_secret_cannot_be_cleared()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.UpdateAsync(created.Id, Draft() with { ClearSecrets = ["token"] }, Token));

        failure.Field.Should().Be("token");
        failure.Detail.Should().Contain("required").And.Contain("replaced");
    }

    [Fact]
    public async Task A_name_that_is_not_a_secret_field_cannot_be_cleared()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var unknown = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.UpdateAsync(created.Id, Draft() with { ClearSecrets = ["nonsense"] }, Token));

        unknown.Field.Should().Be("clearSecrets");

        var plain = await Assert.ThrowsAsync<NotificationValidationException>(
            () => _service.UpdateAsync(created.Id, Draft() with { ClearSecrets = ["url"] }, Token));

        plain.Field.Should().Be("url");

        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_create_ignores_clearSecrets()
    {
        var created = await _service.CreateAsync(
            Draft(password: "wonderwall") with { ClearSecrets = ["password", "token", "nonsense"] },
            Token);

        NotificationSecrets.Read(created.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_test_of_a_stored_row_sends_without_the_secret_marked_to_be_cleared()
    {
        var created = await _service.CreateAsync(Draft(password: "wonderwall"), Token);

        var result = await _service.TestAsync(
            Draft(password: NotificationSecrets.Mask) with { Id = created.Id, ClearSecrets = ["password"] },
            Token);

        result.Success.Should().BeTrue();
        _provider.SentSettings.Single().TryGetProperty("password", out _).Should().BeFalse();

        // Testing does not clear anything: that happens on save.
        await using var database = _database.CreateContext(_time);
        var row = await database.Notifications.SingleAsync(Token);

        NotificationSecrets.Read(row.Settings).GetProperty("password").GetString().Should().Be("wonderwall");
    }

    [Fact]
    public async Task A_cleared_secret_stays_in_the_redactor_until_a_restart_which_is_harmless()
    {
        var registry = new Wondarr.Core.Logging.SecretRegistry();
        var service = new NotificationService(
            _database.CreateContext(_time),
            [_provider],
            NullLogger<NotificationService>.Instance,
            registry);

        var created = await service.CreateAsync(Draft(password: "wonderwall"), Token);
        registry.Redact("secret wonderwall here").Should().NotContain("wonderwall");

        // The registry has no way to forget a value, so the old secret stays redacted until a restart:
        // that over-redacts a string nobody uses any more and cannot leak anything.
        var updated = await service.UpdateAsync(
            created.Id,
            Draft(password: NotificationSecrets.Mask) with { ClearSecrets = ["password"] },
            Token);

        updated.Should().NotBeNull();
        NotificationSecrets.Read(updated!.Settings).TryGetProperty("password", out _).Should().BeFalse();
        registry.Redact("secret wonderwall here").Should().NotContain("wonderwall");
    }

    [Fact]
    public void The_target_is_the_scheme_host_and_port_and_a_secret_url_is_never_part_of_it()
    {
        var plain = NotificationSecrets.Read("""{"url":"https://Example.com:443/a?b=c","password":"x"}""");
        var other = NotificationSecrets.Read("""{"url":"https://example.com/z","password":"y"}""");
        var elsewhere = NotificationSecrets.Read("""{"url":"https://example.com:8443/a","password":"x"}""");

        string Target(System.Text.Json.JsonElement settings, IReadOnlyList<NotificationField> fields) =>
            NotificationSecrets.TargetOf((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(settings.GetRawText())!, fields);

        Target(plain, _provider.Fields).Should().Be(Target(other, _provider.Fields));
        Target(plain, _provider.Fields).Should().NotBe(Target(elsewhere, _provider.Fields));

        // A Discord-style field whose value is the secret URL has no target to compare.
        IReadOnlyList<NotificationField> discord = [new("url", "Webhook URL", "url", Required: true, Secret: true)];
        Target(plain, discord).Should().Be(Target(elsewhere, discord));
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

    private const string DiscordUrl = "https://discord.com/api/webhooks/123/token";

    /// <summary>A service over the real Discord and Apprise providers, which validate their own settings.</summary>
    private NotificationService RealService()
    {
        var clients = NSubstitute.Substitute.For<IHttpClientFactory>();

        return new NotificationService(
            _database.CreateContext(_time),
            [
                new Wondarr.Core.Notifications.Discord.DiscordProvider(
                    clients,
                    _time,
                    NullLogger<Wondarr.Core.Notifications.Discord.DiscordProvider>.Instance),
                new Wondarr.Core.Notifications.Apprise.AppriseProvider(
                    clients,
                    _time,
                    NullLogger<Wondarr.Core.Notifications.Apprise.AppriseProvider>.Instance),
            ],
            NullLogger<NotificationService>.Instance);
    }

    private static NotificationDraft Discord(string webHookUrl, string name = "Discord") =>
        new(
            name,
            "Discord",
            Enabled: true,
            [NotificationEventNames.Import],
            JsonSerializer.Deserialize<JsonElement>($$"""{"webHookUrl":"{{webHookUrl}}"}"""));

    private static NotificationDraft Apprise(string statelessUrls) =>
        new(
            "Apprise",
            "Apprise",
            Enabled: true,
            [NotificationEventNames.Import],
            JsonSerializer.Deserialize<JsonElement>(
                $$"""{"serverUrl":"http://apprise.local:8000","statelessUrls":"{{statelessUrls}}"}"""));

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
            new("url", "URL", "url", Required: true, Target: true),
            new("password", "Password", "password", Required: false, Secret: true),
            new("token", "Token", "password", Required: true, Secret: true),
        ];

        /// <summary>Thrown by every send when set.</summary>
        public Exception? Throw { get; set; }

        /// <summary>What this provider was sent, in order.</summary>
        public IReadOnlyList<NotificationMessage> Sent => _sent;

        /// <summary>The settings each send arrived with.</summary>
        public List<JsonElement> SentSettings { get; } = [];

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
            SentSettings.Add(settings.Clone());

            if (Throw is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
    }
}
