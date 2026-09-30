using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Metadata;
using Xunit;

namespace Wondarr.Core.Tests.Lyrics;

/// <summary>
/// Contract tests for <see cref="LrclibClient"/> against the responses recorded on 2026-09-30 (see
/// <c>tests/fixtures/lrclib/README.md</c>), plus the registration behind it.
/// </summary>
public sealed class LrclibClientTests
{
    private const int GetLuckySeconds = 248;

    [Fact]
    public async Task Get_asks_for_the_track_and_length_only_and_reads_both_texts()
    {
        var handler = StubHttpMessageHandler.Serving("get-hit.json");
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Found);
        lookup.LrclibId.Should().Be(986804);
        lookup.PlainLyrics.Should().StartWith("Like the legend of the phoenix, huh");
        lookup.SyncedLyrics.Should().StartWith("[00:30.64] Like the legend of the phoenix, huh");

        // The ±2 s duration match is the discriminator, so the album title is deliberately not sent:
        // a different album name on the record would make LRCLIB miss.
        var asked = handler.Requests.Should().ContainSingle().Subject;
        asked.AbsolutePath.Should().Be("/api/get");
        asked.Query.Should().Be("?track_name=Get%20Lucky&artist_name=Daft%20Punk&duration=248");
        asked.Query.Should().NotContain("album_name");
    }

    [Fact]
    public async Task A_miss_falls_back_to_search_and_takes_the_first_record_within_two_seconds()
    {
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(Fixtures.Read("search.json")));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].AbsolutePath.Should().Be("/api/search");
        handler.Requests[1].Query.Should().Be("?track_name=Get%20Lucky&artist_name=Daft%20Punk");

        // The first two records are 370 s and 368 s long — different recordings — so the 248 s record
        // is the first one the length check accepts.
        lookup.Status.Should().Be(LyricsLookupStatus.Found);
        lookup.LrclibId.Should().Be(36883705);
        lookup.PlainLyrics.Should().NotBeNullOrWhiteSpace();
        lookup.SyncedLyrics.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Search_records_that_carry_no_lyrics_at_all_are_skipped()
    {
        // Nothing in this recording is within 2 s of 100.
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(Fixtures.Read("search.json")));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", 100, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.NotFound);
        lookup.PlainLyrics.Should().BeNull();
        lookup.SyncedLyrics.Should().BeNull();
        lookup.LrclibId.Should().BeNull();
    }

    [Fact]
    public async Task Search_records_without_lyrics_are_passed_over_for_the_next_length_match()
    {
        // The 321 s record is the only one within 2 s of 321, and it carries neither text: nothing is
        // left to report even though the search answered.
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(Fixtures.Read("search-instrumental.json")));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Aerodynamic", "Daft Punk", 321, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.NotFound);
    }

    [Fact]
    public async Task A_record_the_service_calls_instrumental_is_reported_as_instrumental()
    {
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(Fixtures.Read("search-instrumental.json")));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Aerodynamic", "Daft Punk", 226, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Instrumental);
        lookup.LrclibId.Should().Be(37440918);
        lookup.PlainLyrics.Should().BeNull();
        lookup.SyncedLyrics.Should().BeNull();
    }

    [Fact]
    public async Task Synced_lyrics_alone_are_also_read_as_plain_text()
    {
        var handler = StubHttpMessageHandler.ServingJson(
            """
            {"id":7,"duration":248.0,"instrumental":false,"plainLyrics":null,
             "syncedLyrics":"[ar:Daft Punk]\n[ti:Get Lucky]\n[00:30.64] Like the legend of the phoenix, huh\n[00:35.11][00:35.11] Our ends were beginnings\n[length:04:08]\n[01:13.87] We're up all night to get lucky\n[05:15.71] "}
            """);
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Found);
        lookup.SyncedLyrics.Should().StartWith("[ar:Daft Punk]");

        // The metadata lines go, every leading time tag goes — including the second one on a line that
        // carries two — and the order is kept. The trailing end-of-track timestamp leaves no blank line.
        lookup.PlainLyrics.Should().Be(
            "Like the legend of the phoenix, huh\nOur ends were beginnings\nWe're up all night to get lucky");
    }

    [Fact]
    public async Task A_record_with_neither_text_is_a_miss_even_though_get_answered()
    {
        var handler = StubHttpMessageHandler.ServingJson(
            """{"id":9,"duration":248.0,"instrumental":false,"plainLyrics":null,"syncedLyrics":null}""");
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.NotFound);
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_status_that_is_not_a_hit_is_unavailable_and_never_an_exception(HttpStatusCode status)
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("{}", status));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Unavailable);
        lookup.PlainLyrics.Should().BeNull();

        // A throttle is answered with "no lyrics this time": no retry, no second request.
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_unavailable()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>a proxy said hello</html>", Encoding.UTF8, "text/html"),
        });
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Unavailable);
    }

    [Fact]
    public async Task A_request_that_outlives_the_client_timeout_is_unavailable()
    {
        var handler = StubHttpMessageHandler.ScriptedAsync(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            return Json("{}");
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromMilliseconds(50));

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Unavailable);
    }

    [Fact]
    public async Task A_transport_failure_is_unavailable()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Unavailable);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_the_one_thing_that_propagates()
    {
        // The handler waits far longer than the caller does, so the token that fires is demonstrably the
        // caller's own and not the client's timeout.
        var handler = StubHttpMessageHandler.ScriptedAsync(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            return Json("{}");
        });
        var client = CreateClient(handler, timeout: TimeSpan.FromSeconds(30));

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var lookup = async () => await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, cancelled.Token);

        await lookup.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_registration_gives_the_client_the_host_the_timeout_and_the_identifying_user_agent()
    {
        var handler = StubHttpMessageHandler.Serving("get-hit.json");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHandlerFilter(handler));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ILrclibClient>();

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", GetLuckySeconds, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Found);
        handler.Requests.Should().ContainSingle().Subject.AbsoluteUri
            .Should().StartWith("https://lrclib.net/api/get?track_name=Get%20Lucky");

        // LRCLIB asks callers to identify themselves, exactly as MusicBrainz and AcoustID do.
        handler.UserAgents.Should().ContainSingle().Subject.Should().Contain("Wondarr/");
    }

    [Theory]
    [InlineData("https://lrclib.net/", 500, 10, true)]
    [InlineData("https://lrclib.net", 200, 2, true)]
    [InlineData("http://localhost:8080/", 5000, 60, true)]
    [InlineData("lrclib.net", 500, 10, false)]
    [InlineData("/api/", 500, 10, false)]
    [InlineData("https://lrclib.net/", 100, 10, false)]
    [InlineData("https://lrclib.net/", 9000, 10, false)]
    [InlineData("https://lrclib.net/", 500, 90, false)]
    public void The_validator_accepts_the_defaults_and_refuses_what_would_not_work(
        string baseUrl,
        int intervalMs,
        int timeoutSeconds,
        bool valid)
    {
        var options = new LyricsOptions
        {
            BaseUrl = baseUrl,
            RequestIntervalMs = intervalMs,
            TimeoutSeconds = timeoutSeconds,
        };

        var result = new LyricsOptionsValidator().Validate(null, options);

        result.Succeeded.Should().Be(valid, result.Failures is null ? null : string.Join("; ", result.Failures));
    }

    [Fact]
    public void The_validator_names_the_key_it_refused()
    {
        var result = new LyricsOptionsValidator().Validate(
            null,
            new LyricsOptions { BaseUrl = "not a url", RequestIntervalMs = 100 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.StartsWith("lyrics.base_url", StringComparison.Ordinal));
        result.Failures.Should().Contain(failure => failure.StartsWith("lyrics.request_interval_ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_stalls_after_its_headers_is_bounded_by_the_client_timeout()
    {
        var handler = StubHttpMessageHandler.Scripted(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() });
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(300));

        var lookup = await client
            .FindAsync("Get Lucky", "Daft Punk", 248, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        lookup.Status.Should().Be(LyricsLookupStatus.Unavailable);
    }

    [Fact]
    public async Task A_null_record_in_a_search_answer_is_passed_over()
    {
        var search = "[null," + Fixtures.Read("search.json").TrimStart()[1..];
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(search));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", 248, CancellationToken.None);

        lookup.Status.Should().Be(LyricsLookupStatus.Found);
        lookup.LrclibId.Should().Be(36883705);
    }

    [Theory]
    [InlineData(250, LyricsLookupStatus.Found)]
    [InlineData(251, LyricsLookupStatus.NotFound)]
    public async Task A_search_hit_counts_only_within_two_seconds(int duration, LyricsLookupStatus expected)
    {
        // The fixture's records are 370, 368, 248, 32 and 248 s long.
        var handler = StubHttpMessageHandler.Scripted(
            request => request.AbsolutePath == "/api/get"
                ? Json(Fixtures.Read("get-miss.json"), HttpStatusCode.NotFound)
                : Json(Fixtures.Read("search.json")));
        var client = CreateClient(handler);

        var lookup = await client.FindAsync("Get Lucky", "Daft Punk", duration, CancellationToken.None);

        lookup.Status.Should().Be(expected);
    }

    [Fact]
    public async Task Names_that_need_escaping_are_escaped()
    {
        var handler = StubHttpMessageHandler.Serving("get-hit.json");
        var client = CreateClient(handler);

        await client.FindAsync("Rock & Roll #1?", "AC/DC", 248, CancellationToken.None);

        handler.Requests[0].Query.Should().Contain("track_name=Rock%20%26%20Roll%20%231%3F")
            .And.Contain("artist_name=AC%2FDC");
    }

    private static LrclibClient CreateClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lrclib.net/", UriKind.Absolute),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };

        return new LrclibClient(http, NullLogger<LrclibClient>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Swaps the network out from under the registered clients, as the spacing tests do.</summary>
    private sealed class StubHandlerFilter : IHttpMessageHandlerBuilderFilter
    {
        private readonly HttpMessageHandler _handler;

        public StubHandlerFilter(HttpMessageHandler handler) => _handler = handler;

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                builder.PrimaryHandler = _handler;
            };
    }
}

/// <summary>The recorded LRCLIB responses, copied next to the test binaries.</summary>
internal static class Fixtures
{
    /// <summary>Reads one recorded response.</summary>
    /// <param name="name">The fixture's file name.</param>
    /// <returns>The fixture's text.</returns>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "lrclib", name));
}

/// <summary>Serves canned responses and records the URI and User-Agent of every request.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    private StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    /// <summary>The URIs asked for, in order.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>The User-Agent of each request, in order.</summary>
    public List<string> UserAgents { get; } = [];

    /// <summary>A handler that answers every request with one fixture.</summary>
    public static StubHttpMessageHandler Serving(string name, HttpStatusCode status = HttpStatusCode.OK) =>
        ServingJson(Fixtures.Read(name), status);

    /// <summary>A handler that answers every request with one body.</summary>
    public static StubHttpMessageHandler ServingJson(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Scripted(_ => Json(body, status));

    /// <summary>A handler with one answer per request.</summary>
    public static StubHttpMessageHandler Scripted(Func<Uri, HttpResponseMessage> responder) =>
        ScriptedAsync((request, _) => Task.FromResult(responder(request.RequestUri!)));

    /// <summary>A handler that may answer asynchronously, which is what a timeout needs.</summary>
    public static StubHttpMessageHandler ScriptedAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        new(responder);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        UserAgents.Add(string.Join(",", request.Headers.UserAgent.Select(value => value.ToString())));

        return _responder(request, cancellationToken);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A body that never finishes arriving: the headers came, the bytes never do.</summary>
internal sealed class StallingContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        System.Net.TransportContext? context,
        CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }
}
