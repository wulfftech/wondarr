using System.Net;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Messaging;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests.Slskd;

/// <summary>
/// The endpoint slskd calls when a download finishes. It is anonymous — slskd has no API key — and
/// is authenticated by the token Wondarr generated for it instead, so a wrong or missing token must
/// be indistinguishable from any other 401.
/// </summary>
public sealed class SlskdWebhookTests : IDisposable
{
    private const string Loopback = "127.0.0.1";
    private const string Route = "/api/v1/slskd/webhook";

    private static readonly Guid TransferId = Guid.Parse("2f1c4a44-1de0-4a54-8a2a-6f1d2f4a6b0c");

    private readonly RecordingHandler _handler = new();
    private readonly WondarrAppFactory _factory;

    public SlskdWebhookTests() =>
        _factory = new WondarrAppFactory(configureServices: services => services.AddSingleton<IHandle<SlskdDownloadCompletedEvent>>(_handler));

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Publishes_a_completed_download_when_the_token_matches()
    {
        var token = await TokenAsync();

        using var client = _factory.CreateClient(Loopback);
        using var request = CompletedRequest(token);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var published = _handler.Events.Should().ContainSingle().Subject;
        published.Username.Should().Be("DJ Snake");
        published.TransferId.Should().Be(TransferId);
        published.RemoteFilename.Should().Be(@"Music\Singles\Get Lucky.mp3");
        published.LocalFilename.Should().Be("/data/downloads/slskd/wondarr/17/Get Lucky.mp3");
    }

    [Fact]
    public async Task Ignores_any_other_event_without_publishing_one()
    {
        var token = await TokenAsync();

        using var client = _factory.CreateClient(Loopback);
        using var request = CompletedRequest(token, type: "DownloadDirectoryComplete");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _handler.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_a_missing_token_without_an_event()
    {
        using var client = _factory.CreateClient(Loopback);
        using var request = CompletedRequest(token: null);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The only thing a rejected caller may learn is that it was rejected.
        (await response.Content.ReadAsStringAsync()).Should().NotContain(SlskdConfigRenderer.WebhookHeaderName);
        _handler.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_a_wrong_token_without_saying_anything_about_the_right_one()
    {
        var token = await TokenAsync();
        var wrong = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        using var client = _factory.CreateClient(Loopback);
        using var request = CompletedRequest(wrong);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(token);
        body.Should().NotContain(SlskdConfigRenderer.WebhookHeaderName);
        _handler.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_any_request_that_did_not_come_from_loopback()
    {
        var token = await TokenAsync();

        using var client = _factory.CreateClient("203.0.113.7");
        using var request = CompletedRequest(token);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _handler.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_a_forwarded_request_even_from_loopback()
    {
        var token = await TokenAsync();

        using var client = _factory.CreateClient(Loopback);
        using var request = CompletedRequest(token);
        request.Headers.Add("X-Forwarded-For", "198.51.100.4");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _handler.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Needs_no_api_key_even_when_authentication_is_enabled()
    {
        using var factory = new WondarrAppFactory(
            TestConfiguration.Of(("Server:AuthRequired", "Enabled")),
            services => services.AddSingleton<IHandle<SlskdDownloadCompletedEvent>>(_handler));

        // No X-Api-Key and no cookie: the webhook token is the only credential.
        using var client = factory.CreateClient(Loopback);
        using var request = CompletedRequest(await TokenAsync(factory));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _handler.Events.Should().ContainSingle();
    }

    /// <summary>The webhook token the app generated and wrote into its slskd configuration.</summary>
    private async Task<string> TokenAsync(WondarrAppFactory? factory = null)
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SlskdSecretsStore>();
        var secrets = await store.GetOrCreateAsync(CancellationToken.None);

        return secrets.WebhookToken!;
    }

    private static HttpRequestMessage CompletedRequest(string? token, string type = "DownloadFileComplete")
    {
        var body = new StringBuilder()
            .Append('{')
            .Append("\"type\":").Append(JsonSerializer.Serialize(type)).Append(',')
            .Append("\"localFilename\":\"/data/downloads/slskd/wondarr/17/Get Lucky.mp3\",")
            .Append("\"remoteFilename\":\"Music\\\\Singles\\\\Get Lucky.mp3\",")
            .Append("\"transfer\":{\"id\":\"").Append(TransferId.ToString("D"))
            .Append("\",\"username\":\"DJ Snake\",\"direction\":\"Download\"}")
            .Append('}')
            .ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Route, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Add(SlskdConfigRenderer.WebhookHeaderName, token);
        }

        return request;
    }

    /// <summary>Records every completed download the app published.</summary>
    private sealed class RecordingHandler : IHandle<SlskdDownloadCompletedEvent>
    {
        private readonly List<SlskdDownloadCompletedEvent> _events = [];

        public IReadOnlyList<SlskdDownloadCompletedEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task HandleAsync(SlskdDownloadCompletedEvent message, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add(message);
            }

            return Task.CompletedTask;
        }
    }
}
