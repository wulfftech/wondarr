using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Webhook;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The webhook provider: the Lidarr-shaped payload it posts, and how it sends it. The endpoint is a
/// fake handler, so nothing here touches the network.
/// </summary>
public class WebhookTests
{
    private static readonly JsonElement GrabSettings = Settings("""{"url":"http://hooks.local/wondarr"}""");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_grab_posts_the_song_and_the_release()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("grab", "Grabbed: Oasis – Wonderwall", "Wonderwall.flac from soulseek (FLAC)")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", "(What's the Story) Morning Glory?", "mb-1"),
            Release = new NotificationRelease("Wonderwall.flac", "soulseek", "FLAC", 30_000_000),
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Grab");
        payload.GetProperty("instanceName").GetString().Should().Be("Wondarr");
        payload.GetProperty("applicationUrl").ValueKind.Should().Be(JsonValueKind.Null);

        var song = payload.GetProperty("song");
        song.GetProperty("id").GetInt64().Should().Be(7);
        song.GetProperty("title").GetString().Should().Be("Wonderwall");
        song.GetProperty("artistCredit").GetString().Should().Be("Oasis");
        song.GetProperty("albumTitle").GetString().Should().Be("(What's the Story) Morning Glory?");
        song.GetProperty("mbRecordingId").GetString().Should().Be("mb-1");

        var release = payload.GetProperty("release");
        release.GetProperty("title").GetString().Should().Be("Wonderwall.flac");
        release.GetProperty("sourceType").GetString().Should().Be("soulseek");
        release.GetProperty("quality").GetString().Should().Be("FLAC");
        release.GetProperty("size").GetInt64().Should().Be(30_000_000);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
        handler.Requests.Single().ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task An_import_posts_a_download_with_the_file()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("import", "Imported: Oasis – Wonderwall", "FLAC → /music/Oasis/Wonderwall.flac")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
            File = new NotificationFile("/music/Oasis/Wonderwall.flac", "FLAC"),
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Download");
        payload.GetProperty("isUpgrade").GetBoolean().Should().BeFalse();
        payload.GetProperty("trackFile").GetProperty("path").GetString().Should().Be("/music/Oasis/Wonderwall.flac");
        payload.GetProperty("trackFile").GetProperty("quality").GetString().Should().Be("FLAC");
        payload.TryGetProperty("release", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_upgrade_posts_a_download_marked_as_one()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("upgrade", "Upgraded: Oasis – Wonderwall", "FLAC → /music/Oasis/Wonderwall.flac")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
            File = new NotificationFile("/music/Oasis/Wonderwall.flac", "FLAC"),
            IsUpgrade = true,
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Download");
        payload.GetProperty("isUpgrade").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_posts_a_download_failure_with_its_reason()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("failure", "Failed: Oasis – Wonderwall", "the peer went offline")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("DownloadFailure");
        payload.GetProperty("message").GetString().Should().Be("the peer went offline");
        payload.GetProperty("song").GetProperty("title").GetString().Should().Be("Wonderwall");
    }

    [Fact]
    public async Task A_health_issue_posts_lidarrs_health_shape()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("health", "Health: DownloadFolder", "the download folder is gone")
        {
            Health = new NotificationHealth("DownloadFolder", "error", "the download folder is gone", "https://wiki.local/health"),
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Health");
        payload.GetProperty("level").GetString().Should().Be("error");
        payload.GetProperty("message").GetString().Should().Be("the download folder is gone");
        payload.GetProperty("type").GetString().Should().Be("DownloadFolder");
        payload.GetProperty("wikiUrl").GetString().Should().Be("https://wiki.local/health");
    }

    [Fact]
    public async Task An_update_posts_the_versions_and_the_release_url()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("update", "Update available: Wondarr 0.2.0", "Wondarr 0.2.0 is available (you have 0.1.0)")
        {
            Update = new NotificationUpdate("0.1.0", "0.2.0", "https://github.com/wulfftech/wondarr/releases/tag/v0.2.0"),
        };

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Update");
        payload.GetProperty("instanceName").GetString().Should().Be("Wondarr");

        var update = payload.GetProperty("update");
        update.GetProperty("currentVersion").GetString().Should().Be("0.1.0");
        update.GetProperty("latestVersion").GetString().Should().Be("0.2.0");
        update.GetProperty("releaseUrl").GetString().Should().Be("https://github.com/wulfftech/wondarr/releases/tag/v0.2.0");
    }

    [Fact]
    public async Task A_test_post_is_marked_as_one_and_carries_a_sample_song()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("test", "Test notification from Wondarr", "nothing else was sent");

        await provider.SendAsync(message, GrabSettings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("eventType").GetString().Should().Be("Test");
        payload.GetProperty("song").GetProperty("title").GetString().Should().Be("Test Title");
    }

    [Fact]
    public async Task A_configured_PUT_is_used_instead_of_POST()
    {
        var (provider, handler) = CreateProvider();
        var settings = Settings("""{"url":"http://hooks.local/wondarr","method":"PUT"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task A_username_and_password_become_a_basic_auth_header()
    {
        var (provider, handler) = CreateProvider();
        var settings = Settings("""{"url":"http://hooks.local/wondarr","username":"oasis","password":"wonderwall"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        var authorization = handler.Requests.Single().Authorization;

        authorization.Should().NotBeNull();
        authorization!.Scheme.Should().Be("Basic");
        authorization.Parameter.Should().Be(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("oasis:wonderwall")));
    }

    [Fact]
    public async Task Custom_headers_are_added_as_given()
    {
        var (provider, handler) = CreateProvider();
        var settings = Settings(
            """{"url":"http://hooks.local/wondarr","headers":[{"key":"X-Token","value":"abc"},{"key":"X-Show","value":"all"}]}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        handler.Requests.Single().TryGetHeader("X-Token").Should().Be("abc");
        handler.Requests.Single().TryGetHeader("X-Show").Should().Be("all");
    }

    [Fact]
    public async Task A_429_with_a_short_Retry_After_is_waited_out_and_sent_once_more()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = attempt => attempt == 1
            ? Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1))
            : new HttpResponseMessage(HttpStatusCode.OK);

        var send = provider.SendAsync(TestMessage(), GrabSettings, CancellationToken.None);

        while (!send.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await send;

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_429_with_a_long_Retry_After_is_not_retried()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = _ => Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(120));

        var send = provider.SendAsync(TestMessage(), GrabSettings, CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<NotificationSendException>(() => send);

        handler.Requests.Should().ContainSingle();
        thrown.Message.Should().Contain("429");
    }

    [Fact]
    public async Task A_five_hundred_throws_a_send_failure()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var thrown = await Assert.ThrowsAsync<NotificationSendException>(
            () => provider.SendAsync(TestMessage(), GrabSettings, CancellationToken.None));

        thrown.Message.Should().Contain("500");
    }

    [Fact]
    public void A_url_that_is_not_http_is_a_validation_message()
    {
        var (provider, _) = CreateProvider();

        provider.Validate(Settings("""{"url":"ftp://hooks.local/wondarr"}""")).Should().NotBeEmpty();
        provider.Validate(Settings("""{"url":"not a url"}""")).Should().NotBeEmpty();
        provider.Validate(GrabSettings).Should().BeEmpty();
    }

    [Fact]
    public void An_invalid_header_name_is_a_validation_message()
    {
        var (provider, _) = CreateProvider();

        provider
            .Validate(Settings("""{"url":"http://hooks.local/wondarr","headers":[{"key":"bad header","value":"x"}]}"""))
            .Should().NotBeEmpty();
    }

    [Fact]
    public void The_password_field_is_a_secret_and_the_url_field_is_required()
    {
        var (provider, _) = CreateProvider();

        provider.Implementation.Should().Be("Webhook");
        provider.Fields.Single(field => field.Name == "password").Secret.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "url").Required.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "method").Options.Should().Equal("POST", "PUT");
    }

    private static NotificationMessage TestMessage() =>
        new(NotificationEventNames.Test, "Test notification from Wondarr", "nothing else was sent");

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Retry(HttpStatusCode status, TimeSpan wait)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);

        return response;
    }

    private (WebhookProvider Provider, RecordingHandler Handler) CreateProvider()
    {
        var handler = new RecordingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NotificationHttp.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return (new WebhookProvider(factory, _time, NullLogger<WebhookProvider>.Instance), handler);
    }

    /// <summary>One request as it went out, copied before the client disposes it.</summary>
    private sealed record RecordedRequest(
        HttpMethod Method,
        string? ContentType,
        AuthenticationHeaderValue? Authorization,
        IReadOnlyDictionary<string, string> Headers,
        string? Body)
    {
        /// <summary>The value of one header, or <see langword="null"/>.</summary>
        public string? TryGetHeader(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Answers with whatever the test asks for and keeps every request, body included: a
    /// <see cref="HttpRequestMessage"/> cannot be read after it has been sent.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        /// <summary>Builds the answer to the nth attempt, 1-based.</summary>
        public Func<int, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        /// <summary>Every request that went out, in order.</summary>
        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        /// <summary>The parsed bodies that went out, in order.</summary>
        public IReadOnlyList<JsonElement> Payloads =>
            [.. Requests.Select(request => JsonDocument.Parse(request.Body ?? "{}").RootElement.Clone())];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            int attempt;

            lock (_requests)
            {
                _requests.Add(new RecordedRequest(
                    request.Method,
                    request.Content?.Headers.ContentType?.MediaType,
                    request.Headers.Authorization,
                    headers,
                    body));

                attempt = _requests.Count;
            }

            return Respond(attempt);
        }
    }
}
