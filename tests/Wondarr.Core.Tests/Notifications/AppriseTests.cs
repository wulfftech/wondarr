using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Apprise;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The Apprise provider: the path and body each mode sends, and the one-of rule the settings obey. The
/// Apprise server is a fake handler, so nothing here touches the network.
/// </summary>
public class AppriseTests
{
    private const string Server = "http://apprise.local:8000";

    private static readonly JsonElement KeyedSettings = Parse(
        $$"""{"serverUrl":"{{Server}}","configurationKey":"wondarr"}""");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_configuration_key_posts_to_the_keyed_notify_path()
    {
        var (provider, handler) = CreateProvider();

        await provider.SendAsync(TestMessage(), KeyedSettings, CancellationToken.None);

        var request = handler.Requests.Single();

        request.Method.Should().Be(HttpMethod.Post);
        request.Url.Should().Be("http://apprise.local:8000/notify/wondarr");
        request.ContentType.Should().Be("application/json");

        var payload = handler.Payloads.Single();

        payload.GetProperty("title").GetString().Should().Be("Test notification from Wondarr");
        payload.GetProperty("body").GetString().Should().Be("nothing else was sent");
        payload.GetProperty("type").GetString().Should().Be("info");
        payload.GetProperty("format").GetString().Should().Be("text");
        payload.TryGetProperty("urls", out _).Should().BeFalse();
        payload.TryGetProperty("tag", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Stateless_urls_post_to_notify_with_the_urls_in_the_body()
    {
        var (provider, handler) = CreateProvider();
        var settings = Parse(
            $$"""{"serverUrl":"{{Server}}","statelessUrls":"mailto://user:pw@example.com,json://localhost"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        handler.Requests.Single().Url.Should().Be("http://apprise.local:8000/notify");
        handler.Payloads.Single().GetProperty("urls").GetString()
            .Should().Be("mailto://user:pw@example.com,json://localhost");
    }

    [Fact]
    public async Task Tags_are_joined_with_commas_and_the_type_is_the_configured_one()
    {
        var (provider, handler) = CreateProvider();
        var settings = Parse(
            $$"""{"serverUrl":"{{Server}}","configurationKey":"wondarr","notificationType":"failure","tags":"music, home"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("tag").GetString().Should().Be("music,home");
        payload.GetProperty("type").GetString().Should().Be("failure");
    }

    [Fact]
    public async Task A_username_and_password_become_a_basic_auth_header()
    {
        var (provider, handler) = CreateProvider();
        var settings = Parse(
            $$"""{"serverUrl":"{{Server}}","configurationKey":"wondarr","authUsername":"oasis","authPassword":"wonderwall"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        var authorization = handler.Requests.Single().Authorization;

        authorization.Should().NotBeNull();
        authorization!.Scheme.Should().Be("Basic");
        authorization.Parameter.Should().Be(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("oasis:wonderwall")));
    }

    [Fact]
    public void Exactly_one_of_the_configuration_key_and_the_stateless_urls_is_required()
    {
        var (provider, _) = CreateProvider();

        provider.Validate(KeyedSettings).Should().BeEmpty();
        provider.Validate(Parse($$"""{"serverUrl":"{{Server}}","statelessUrls":"json://localhost"}""")).Should().BeEmpty();

        provider.Validate(Parse($$"""{"serverUrl":"{{Server}}"}""")).Should().ContainSingle()
            .Which.Should().Contain("Configuration Key");
        provider.Validate(Parse($$"""{"serverUrl":"{{Server}}","configurationKey":"wondarr","statelessUrls":"json://localhost"}"""))
            .Should().ContainSingle().Which.Should().Contain("Configuration Key");
    }

    [Fact]
    public void A_server_url_that_is_not_http_is_a_validation_message()
    {
        var (provider, _) = CreateProvider();

        provider.Validate(Parse("""{"serverUrl":"ftp://apprise.local","configurationKey":"wondarr"}"""))
            .Should().ContainSingle().Which.Should().Contain("http");
        provider.Validate(Parse("""{"serverUrl":"not a url","configurationKey":"wondarr"}""")).Should().NotBeEmpty();
        provider.Validate(Parse("""{"serverUrl":"http://user:secret@apprise.local","configurationKey":"wondarr"}"""))
            .Should().ContainSingle().Which.Should().NotContain("secret");
    }

    [Fact]
    public void A_notification_type_that_is_not_offered_is_a_validation_message()
    {
        var (provider, _) = CreateProvider();

        provider.Validate(Parse($$"""{"serverUrl":"{{Server}}","configurationKey":"wondarr","notificationType":"critical"}"""))
            .Should().ContainSingle().Which.Should().Contain("notification type");
    }

    [Fact]
    public async Task A_five_hundred_throws_a_failure_that_does_not_name_the_server_or_the_urls()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var settings = Parse(
            $$"""{"serverUrl":"{{Server}}","statelessUrls":"mailto://oasis:wonderwall@example.com"}""");

        var thrown = await Assert.ThrowsAsync<NotificationSendException>(
            () => provider.SendAsync(TestMessage(), settings, CancellationToken.None));

        thrown.Message.Should().Contain("500");
        thrown.Message.Should().NotContain("apprise.local");
        thrown.Message.Should().NotContain("wonderwall");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void The_stateless_urls_and_the_password_are_secrets()
    {
        var (provider, _) = CreateProvider();

        provider.Implementation.Should().Be("Apprise");
        provider.Fields.Single(field => field.Name == "serverUrl").Required.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "statelessUrls").Secret.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "authPassword").Secret.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "authUsername").Secret.Should().BeFalse();
        provider.Fields.Single(field => field.Name == "notificationType").Options
            .Should().Equal("info", "success", "warning", "failure");
    }

    private static NotificationMessage TestMessage() =>
        new(NotificationEventNames.Test, "Test notification from Wondarr", "nothing else was sent");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private (AppriseProvider Provider, NotificationTestHandler Handler) CreateProvider()
    {
        var handler = new NotificationTestHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NotificationHttp.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return (new AppriseProvider(factory, _time, NullLogger<AppriseProvider>.Instance), handler);
    }
}
