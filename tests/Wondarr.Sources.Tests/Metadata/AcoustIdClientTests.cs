using System.Net;
using System.Net.Http.Headers;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata.AcoustId;
using Wondarr.Core.Metadata.Http;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// Contract tests for <see cref="AcoustIdClient"/> against the responses recorded on 2026-09-29 (see
/// <c>tests/fixtures/acoustid/README.md</c>).
/// </summary>
public sealed class AcoustIdClientTests
{
    private const int ExampleDurationSeconds = 641;

    [Fact]
    public async Task Lookup_posts_a_gzip_form_with_exactly_the_five_documented_fields()
    {
        var handler = RecordingHttpMessageHandler.Serving("lookup-empty.json");
        var (client, _) = CreateClient(handler);

        await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        handler.Methods.Should().ContainSingle().Subject.Should().Be(HttpMethod.Post);
        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/v2/lookup");
        handler.Encodings.Should().ContainSingle().Subject.Should().Be("gzip");
        handler.ContentTypes.Should().ContainSingle().Subject.Should().Be("application/x-www-form-urlencoded");

        handler.Fields().Should().Equal("client", "duration", "fingerprint", "meta", "format");
        handler.Form("client").Should().Be(AcoustIdFixtures.ClientKey);
        handler.Form("fingerprint").Should().Be(AcoustIdFixtures.Fingerprint);
        handler.Form("meta").Should().Be("recordings");
        handler.Form("format").Should().Be("json");
        RecordingHttpMessageHandler.Integer(handler.Form("duration")).Should().Be(ExampleDurationSeconds);

        // AcoustID asks every caller for a descriptive User-Agent, like MusicBrainz does.
        handler.UserAgents.Should().ContainSingle().Subject.Should().Contain("Wondarr/");
    }

    [Fact]
    public async Task Lookup_parses_the_documented_example()
    {
        var (client, _) = CreateClient(RecordingHttpMessageHandler.Serving("lookup-docs-example.json"));

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Ok);
        result.Error.Should().BeNull();
        result.Results.Should().HaveCount(2);
        result.Results.Should().OnlyContain(acoustId => acoustId.Score == 1.0);

        var recording = result.Results[0].Recordings.Should().ContainSingle().Subject;
        recording.Id.Should().Be("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff");
        recording.Title.Should().Be("Lower Your Eyelids to Die With the Sun");

        // AcoustID sends the recording's length as a float.
        recording.DurationSeconds.Should().Be(637.333);
        recording.ArtistNames.Should().Equal("M83");
    }

    [Fact]
    public async Task Lookup_of_a_known_fingerprint_without_recordings_is_an_empty_list()
    {
        var (client, _) = CreateClient(RecordingHttpMessageHandler.Serving("lookup-no-recordings.json"));

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Ok);
        result.Results.Should().ContainSingle().Subject.Recordings.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_of_an_unknown_fingerprint_has_no_results()
    {
        var (client, _) = CreateClient(RecordingHttpMessageHandler.Serving("lookup-tone-unknown.json"));

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Ok);
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_maps_a_rejected_client_key()
    {
        var handler = RecordingHttpMessageHandler.Serving("lookup-invalid-key.json");
        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.InvalidKey);
        result.Error.Should().Be("invalid API key");

        // A rejected key is not something a retry fixes.
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Lookup_maps_a_rejected_fingerprint()
    {
        var handler = RecordingHttpMessageHandler.Serving("lookup-invalid-fingerprint.json", HttpStatusCode.BadRequest);
        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.InvalidFingerprint);
        result.Error.Should().Be("invalid fingerprint");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Lookup_maps_a_server_error_to_unavailable()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Unavailable);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Lookup_without_a_key_makes_no_request()
    {
        var handler = RecordingHttpMessageHandler.Serving("lookup-docs-example.json");
        var (client, _) = CreateClient(handler, new AcoustIdOptions { ClientKey = null });

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.NotConfigured);
        result.Results.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_waits_the_retry_after_and_tries_again()
    {
        var calls = 0;

        var handler = new RecordingHttpMessageHandler(_ =>
            Interlocked.Increment(ref calls) == 1
                ? Throttled(TimeSpan.FromSeconds(1))
                : AcoustIdFixtures.Response("lookup-docs-example.json"));

        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Ok);
        result.Results.Should().HaveCount(2);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Lookup_gives_up_after_three_rate_limited_attempts()
    {
        // A short wait keeps the test quick; the client honours whatever the header says.
        var handler = new RecordingHttpMessageHandler(_ => Throttled(TimeSpan.FromMilliseconds(10)));
        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.RateLimited);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_client_key_never_reaches_an_error_or_a_log_line()
    {
        var (client, logger) = CreateClient(RecordingHttpMessageHandler.Serving("lookup-invalid-key.json"));

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.InvalidKey);
        result.Error.Should().NotContain(AcoustIdFixtures.ClientKey);
        logger.Lines.Should().NotContain(line => line.Contains(AcoustIdFixtures.ClientKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throttle_never_logs_the_key_either()
    {
        var handler = new RecordingHttpMessageHandler(_ => Throttled(TimeSpan.FromMilliseconds(10)));
        var (client, logger) = CreateClient(handler);

        await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().OnlyContain(line => !line.Contains(AcoustIdFixtures.ClientKey, StringComparison.Ordinal));
    }

    [Fact]
    public void The_post_configure_registers_the_key_so_the_redactor_removes_it()
    {
        var registry = new SecretRegistry();
        var options = new AcoustIdOptions
        {
            ClientKey = "  " + AcoustIdFixtures.ClientKey,
            BaseUrl = "https://api.acoustid.org/v2",
        };

        new AcoustIdOptionsPostConfigure(registry).PostConfigure(null, options);

        options.BaseUrl.Should().Be("https://api.acoustid.org/v2/");
        options.ClientKey.Should().Be(AcoustIdFixtures.ClientKey);
        registry.Redact($"client={AcoustIdFixtures.ClientKey}").Should().Be("client=(removed)");
    }

    [Fact]
    public async Task Lookup_spaces_seven_calls_at_three_per_second()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromMilliseconds(1000.0 / 3), time);
        var inner = new RecordingHttpMessageHandler(_ => AcoustIdFixtures.Response("lookup-empty.json"));
        var (client, _) = CreateClient(new RequestSpacingHandler(gate) { InnerHandler = inner });

        var started = time.GetUtcNow();
        var lookups = Enumerable
            .Range(0, 7)
            .Select(_ => client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None))
            .ToArray();

        var all = Task.WhenAll(lookups);

        // The gate waits on the fake clock, so the test has to move it: six gaps of 333 ms are the two
        // seconds that seven calls at three per second take. Each turn gives the waiters' continuations
        // a chance to run before the clock moves on.
        for (var tick = 0; tick < 1_000 && !all.IsCompleted; tick++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }

        (await all).Should().OnlyContain(result => result.Status == AcoustIdStatus.Ok);
        inner.Requests.Should().HaveCount(7);
        (time.GetUtcNow() - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    private static HttpResponseMessage Throttled(TimeSpan retryAfter) =>
        new(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
        };

    /// <summary>The client under test, wired to the handler and the fixtures' client key.</summary>
    private static (AcoustIdClient Client, CapturingLogger<AcoustIdClient> Logger) CreateClient(
        HttpMessageHandler handler,
        AcoustIdOptions? options = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.acoustid.org/v2/"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Wondarr/0.0.0 ( https://github.com/wulfftech/wondarr )");

        var logger = new CapturingLogger<AcoustIdClient>();
        var configured = options ?? new AcoustIdOptions { ClientKey = AcoustIdFixtures.ClientKey };

        return (new AcoustIdClient(http, new StaticOptionsMonitor<AcoustIdOptions>(configured), logger), logger);
    }
}