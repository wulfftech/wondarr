using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Apprise;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// Apprise names its address <c>serverUrl</c>, not <c>url</c>: its stored stateless URLs and password must
/// still go only to the server they were saved for (R-038).
/// </summary>
public sealed class AppriseSecretsTests : IDisposable
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly NotificationService _service;

    public AppriseSecretsTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        var apprise = new AppriseProvider(Substitute.For<IHttpClientFactory>(), _time, NullLogger<AppriseProvider>.Instance);

        _service = new NotificationService(_database.CreateContext(_time), [apprise], NullLogger<NotificationService>.Instance);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task An_update_to_a_new_server_with_the_masked_secrets_is_refused()
    {
        var created = await _service.CreateAsync(Draft("http://apprise.local:8000", "discord://abc/def", "wonderwall"), Token);

        var failure = await Assert.ThrowsAsync<NotificationValidationException>(() => _service.UpdateAsync(
            created.Id,
            Draft("http://attacker.example", NotificationSecrets.Mask, NotificationSecrets.Mask),
            Token));

        failure.Field.Should().BeOneOf("statelessUrls", "authPassword");
        failure.Detail.Should().EndWith("again: the address changed.");

        await using var database = _database.CreateContext(_time);
        NotificationSecrets.Read((await database.Notifications.SingleAsync(Token)).Settings)
            .GetProperty("serverUrl").GetString().Should().Be("http://apprise.local:8000");
    }

    [Fact]
    public async Task A_test_aimed_at_a_new_server_with_the_masked_secrets_is_refused()
    {
        var created = await _service.CreateAsync(Draft("http://apprise.local:8000", "discord://abc/def", "wonderwall"), Token);

        var act = () => _service.TestAsync(
            Draft("http://attacker.example", NotificationSecrets.Mask, NotificationSecrets.Mask) with { Id = created.Id },
            Token);

        await Assert.ThrowsAsync<NotificationValidationException>(act);
    }

    [Fact]
    public async Task The_same_server_under_another_path_keeps_the_secrets()
    {
        var created = await _service.CreateAsync(Draft("http://apprise.local:8000", "discord://abc/def", "wonderwall"), Token);

        await _service.UpdateAsync(
            created.Id,
            Draft("http://APPRISE.local:8000/other", NotificationSecrets.Mask, NotificationSecrets.Mask),
            Token);

        await using var database = _database.CreateContext(_time);
        var stored = NotificationSecrets.Read((await database.Notifications.SingleAsync(Token)).Settings);

        stored.GetProperty("statelessUrls").GetString().Should().Be("discord://abc/def");
        stored.GetProperty("authPassword").GetString().Should().Be("wonderwall");
    }

    private static NotificationDraft Draft(string serverUrl, string statelessUrls, string password) =>
        new(
            "Apprise",
            "Apprise",
            Enabled: true,
            [NotificationEventNames.Import],
            JsonSerializer.Deserialize<JsonElement>(
                $$"""{"serverUrl":"{{serverUrl}}","statelessUrls":"{{statelessUrls}}","authUsername":"me","authPassword":"{{password}}"}"""));
}
