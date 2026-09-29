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

    [Fact]
    public async Task A_retry_after_beyond_the_cap_defers_without_waiting()
    {
        // An hour-long Retry-After must not hold the import: the file is deferred and retried later.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHttpMessageHandler(_ => Throttled(TimeSpan.FromHours(1)));
        var (client, _) = CreateClient(handler, timeProvider: time);

        var started = time.GetUtcNow();
        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.RateLimited);
        handler.Requests.Should().ContainSingle();
        time.GetUtcNow().Should().Be(started);
    }

    [Fact]
    public async Task A_body_that_cannot_be_read_defers()
    {
        // A connection dropped mid-response is an outage, not an exception out of LookupAsync.
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingContent(),
        });

        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Unavailable);
        result.Error.Should().NotBeNullOrWhiteSpace();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(5, "internal error")]
    [InlineData(13, "temporarily unavailable")]
    public async Task Server_side_error_codes_are_unavailable(int code, string message)
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"status\":\"error\",\"error\":{\"code\":" + code
                    + ",\"message\":\"" + message + "\"}}",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.Unavailable);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_rate_limit_code_in_a_200_body_is_retried_and_then_defers()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10)) },
            Content = new StringContent(
                """{"status":"error","error":{"code":14,"message":"rate limit exceeded"}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var (client, _) = CreateClient(handler);

        var result = await client.LookupAsync(AcoustIdFixtures.Fingerprint, ExampleDurationSeconds, CancellationToken.None);

        result.Status.Should().Be(AcoustIdStatus.RateLimited);
        handler.Requests.Should().HaveCount(3);
    }

    private static HttpResponseMessage Throttled(TimeSpan retryAfter) =>
        new(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
        };

    /// <summary>The client under test, wired to the handler and the fixtures' client key.</summary>
    private static (AcoustIdClient Client, CapturingLogger<AcoustIdClient> Logger) CreateClient(
        HttpMessageHandler handler,
        AcoustIdOptions? options = null,
        TimeProvider? timeProvider = null)
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

        return (
            new AcoustIdClient(
                http,
                new StaticOptionsMonitor<AcoustIdOptions>(configured),
                timeProvider ?? TimeProvider.System,
                logger),
            logger);
    }
}

/// <summary>The config rules that keep a mis-typed <c>acoustid:</c> section from reaching the service.</summary>
public sealed class AcoustIdOptionsValidatorTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions())
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_review_score_above_the_accept_score_is_refused()
    {
        var result = new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions { AcceptScore = 0.6, ReviewScore = 0.8 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("acoustid.review_score", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void A_score_outside_zero_to_one_is_refused(double score)
    {
        var result = new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions { AcceptScore = score });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("acoustid.accept_score", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void One_to_three_requests_per_second_is_allowed(int requestsPerSecond)
    {
        new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions { RequestsPerSecond = requestsPerSecond })
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void More_than_three_requests_per_second_is_refused(int requestsPerSecond)
    {
        var result = new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions { RequestsPerSecond = requestsPerSecond });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("acoustid.requests_per_second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_base_url_is_refused()
    {
        var result = new AcoustIdOptionsValidator()
            .Validate(null, new AcoustIdOptions { BaseUrl = "/v2/" });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("acoustid.base_url", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://api.acoustid.org/v2", "https://api.acoustid.org/v2/")]
    [InlineData("https://api.acoustid.org/v2/", "https://api.acoustid.org/v2/")]
    [InlineData("  https://api.acoustid.org/v2  ", "https://api.acoustid.org/v2/")]
    public void The_post_configure_leaves_exactly_one_trailing_slash(string configured, string expected)
    {
        var options = new AcoustIdOptions { BaseUrl = configured };

        new AcoustIdOptionsPostConfigure(new SecretRegistry()).PostConfigure(null, options);

        options.BaseUrl.Should().Be(expected);
    }
}

/// <summary>A response body that fails while it is being read, the way a dropped connection does.</summary>
internal sealed class ThrowingContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new IOException("the connection was closed while the body was being read");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }
}
