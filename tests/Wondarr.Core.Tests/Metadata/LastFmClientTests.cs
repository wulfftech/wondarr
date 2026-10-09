using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata.LastFm;
using Wondarr.Core.Tests.ImportLists;
using Wondarr.Core.Tests.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.Metadata;

/// <summary>
/// The Last.fm client against hand-written answers in the shape the 2.0 API documents: the mapping,
/// the 24-hour cache, the back-off after error 29 or a 429, the key check, and the rule that the key
/// never comes back in a message.
/// </summary>
public sealed class LastFmClientTests
{
    /// <summary>A key that is easy to spot in a message that should not carry it.</summary>
    private const string ApiKey = "k3y-0000-SECRET";

    private const string Mbid = "833f00e1-781f-4edd-90e4-e52712618862";

    private static readonly string BaseAddress = Wondarr.Core.Metadata.ServiceCollectionExtensions.LastFmBaseUrl;

    [Fact]
    public async Task A_track_is_mapped_with_five_tags_and_a_plain_text_wiki()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(null, "Daft Punk", "Get Lucky", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Ok);
        result.Value!.Url.Should().Be("https://www.last.fm/music/Daft+Punk/_/Get+Lucky");
        result.Value.Listeners.Should().Be(1_234_567);
        result.Value.Playcount.Should().Be(9_876_543);
        result.Value.Tags.Should().Equal("electronic", "disco", "funk", "dance", "pop");

        // Last.fm's markup and its trailing "Read more" anchor are gone; the entities are decoded.
        result.Value.WikiSummary.Should().Be("Get Lucky is a song by Daft Punk & Pharrell. It was released in 2013.");

        var asked = handler.Requests.Should().ContainSingle().Subject;
        asked.Query.Should().Contain("method=track.getInfo");
        asked.Query.Should().Contain("artist=Daft%20Punk");
        asked.Query.Should().Contain("track=Get%20Lucky");
        asked.Query.Should().Contain("autocorrect=1");
        asked.Query.Should().Contain("format=json");
    }

    [Fact]
    public async Task A_track_with_a_recording_id_is_asked_for_by_mbid()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var client = Client(handler, out _);

        await client.GetTrackAsync(Mbid, "Daft Punk", "Get Lucky", CancellationToken.None);

        var asked = handler.Requests.Should().ContainSingle().Subject;
        asked.Query.Should().Contain($"mbid={Mbid}");
        asked.Query.Should().NotContain("artist=");
    }

    [Fact]
    public async Task A_recording_id_last_fm_does_not_know_falls_back_to_artist_and_title()
    {
        var handler = StubHttpMessageHandler.Scripted(uri => uri.Query.Contains("mbid=", StringComparison.Ordinal)
            ? Json("""{"error":6,"message":"Track not found"}""")
            : Json(TrackAnswer));
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(Mbid, "Daft Punk", "Get Lucky", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Ok);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_lone_tag_that_last_fm_sends_as_an_object_is_still_read()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(
            """{"track":{"url":"u","listeners":"5","playcount":"6","toptags":{"tag":{"name":"rock"}}}}"""));
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        result.Value!.Tags.Should().Equal("rock");
        result.Value.WikiSummary.Should().BeNull();
    }

    [Fact]
    public async Task An_artist_is_mapped_with_a_plain_text_bio()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(ArtistAnswer));
        var client = Client(handler, out _);

        var result = await client.GetArtistAsync("Daft Punk", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Ok);
        result.Value!.Name.Should().Be("Daft Punk");
        result.Value.Url.Should().Be("https://www.last.fm/music/Daft+Punk");
        result.Value.Listeners.Should().Be(3_000_000);
        result.Value.BioSummary.Should().Be("Daft Punk are a French duo.");
        handler.Requests.Should().ContainSingle().Which.Query.Should().Contain("method=artist.getInfo");
    }

    [Fact]
    public async Task Similar_tracks_are_mapped_and_limited_to_ten()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(SimilarAnswer));
        var client = Client(handler, out _);

        var result = await client.GetSimilarAsync(null, "Daft Punk", "Get Lucky", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Ok);
        result.Value.Should().HaveCount(2);
        result.Value![0].Artist.Should().Be("Chic");
        result.Value[0].Title.Should().Be("Good Times");
        result.Value[0].Match.Should().BeApproximately(0.85, 0.0001);
        result.Value[0].Mbid.Should().Be(Mbid);
        result.Value[1].Mbid.Should().BeNull();
        handler.Requests.Should().ContainSingle().Which.Query.Should().Contain("method=track.getSimilar").And.Contain("limit=10");
    }

    [Fact]
    public async Task Two_lookups_make_one_call_and_the_answer_is_kept_for_a_day()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var client = Client(handler, out var time);

        await client.GetTrackAsync(null, "Daft Punk", "Get Lucky", CancellationToken.None);
        time.Advance(TimeSpan.FromHours(23));
        await client.GetTrackAsync(null, "Daft Punk", "Get Lucky", CancellationToken.None);

        handler.Requests.Should().ContainSingle("the second lookup is answered from memory");

        // Spelled differently, it is the same question.
        await client.GetTrackAsync(null, "daft punk", "get lucky", CancellationToken.None);

        handler.Requests.Should().ContainSingle();

        time.Advance(TimeSpan.FromHours(2));
        await client.GetTrackAsync(null, "Daft Punk", "Get Lucky", CancellationToken.None);

        handler.Requests.Should().HaveCount(2, "a day later the answer is asked for again");
    }

    [Fact]
    public async Task A_track_last_fm_does_not_know_is_remembered_too()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":6,"message":"Track not found"}"""));
        var client = Client(handler, out _);

        var first = await client.GetTrackAsync(null, "Nobody", "Nothing", CancellationToken.None);
        await client.GetTrackAsync(null, "Nobody", "Nothing", CancellationToken.None);

        first.Status.Should().Be(LastFmStatus.NotFound);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Error_29_holds_every_call_back_until_the_wait_is_over()
    {
        var calls = 0;
        var handler = StubHttpMessageHandler.Scripted(_ =>
        {
            calls++;

            return calls == 1
                ? Json("""{"error":29,"message":"Rate limit exceeded - Your IP has made too many requests in a short period"}""")
                : Json(TrackAnswer);
        });
        var client = Client(handler, out var time);

        var limited = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        limited.Status.Should().Be(LastFmStatus.RateLimited);

        // Another question, inside the wait: no request is made at all.
        time.Advance(TimeSpan.FromSeconds(30));
        var held = await client.GetArtistAsync("A", CancellationToken.None);

        held.Status.Should().Be(LastFmStatus.RateLimited);
        handler.Requests.Should().ContainSingle();

        // The default back-off is a minute; after it the next call goes through.
        time.Advance(TimeSpan.FromSeconds(31));
        var again = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        again.Status.Should().Be(LastFmStatus.Ok);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_429_is_waited_out_for_as_long_as_its_retry_after_says()
    {
        var calls = 0;
        var handler = StubHttpMessageHandler.Scripted(_ =>
        {
            calls++;

            if (calls > 1)
            {
                return Json(ArtistAnswer);
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));

            return response;
        });
        var client = Client(handler, out var time);

        (await client.GetArtistAsync("A", CancellationToken.None)).Status.Should().Be(LastFmStatus.RateLimited);

        time.Advance(TimeSpan.FromSeconds(100));
        (await client.GetArtistAsync("A", CancellationToken.None)).Status.Should().Be(LastFmStatus.RateLimited);
        handler.Requests.Should().ContainSingle("Retry-After said two minutes");

        time.Advance(TimeSpan.FromSeconds(21));
        (await client.GetArtistAsync("A", CancellationToken.None)).Status.Should().Be(LastFmStatus.Ok);
    }

    [Fact]
    public async Task A_key_last_fm_rejects_is_reported_as_invalid()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"error":10,"message":"Invalid API key - You must be granted a valid key by last.fm"}"""));
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.InvalidKey);
        result.Message.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task Checking_a_key_makes_one_call_with_that_key_and_is_not_cached()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var client = Client(handler, out _, apiKey: null);

        var first = await client.CheckKeyAsync("typed-key-123", CancellationToken.None);
        await client.CheckKeyAsync("typed-key-123", CancellationToken.None);

        first.Accepted.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Query.Should().Contain("method=track.getInfo").And.Contain("api_key=typed-key-123");
    }

    [Fact]
    public async Task Checking_a_key_reports_an_invalid_key_and_a_rate_limit()
    {
        var invalid = Client(
            StubHttpMessageHandler.Scripted(_ => Json("""{"error":10,"message":"Invalid API key"}""")),
            out _);
        var limited = Client(
            StubHttpMessageHandler.Scripted(_ => Json("""{"error":29,"message":"Rate limit exceeded"}""")),
            out _);

        var rejected = await invalid.CheckKeyAsync("bad-key-123", CancellationToken.None);
        var throttled = await limited.CheckKeyAsync("some-key-123", CancellationToken.None);

        rejected.Accepted.Should().BeFalse();
        rejected.Status.Should().Be(LastFmStatus.InvalidKey);
        rejected.Message.Should().Contain("rejected");
        throttled.Accepted.Should().BeFalse();
        throttled.Status.Should().Be(LastFmStatus.RateLimited);
        throttled.Message.Should().Contain("rate limiting");
    }

    [Fact]
    public async Task A_key_that_is_only_being_tried_is_handed_to_the_secret_registry()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var secrets = new SecretRegistry();
        var client = Client(handler, out _, apiKey: null, secrets: secrets);

        await client.CheckKeyAsync("typed-key-123", CancellationToken.None);

        secrets.Redact("GET /?api_key=typed-key-123").Should().NotContain("typed-key-123");
    }

    [Fact]
    public async Task Without_a_key_nothing_is_asked()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(TrackAnswer));
        var client = Client(handler, out _, apiKey: null);

        client.IsConfigured.Should().BeFalse();
        (await client.GetTrackAsync(null, "A", "B", CancellationToken.None)).Status.Should().Be(LastFmStatus.NotConfigured);
        (await client.GetArtistAsync("A", CancellationToken.None)).Status.Should().Be(LastFmStatus.NotConfigured);
        (await client.GetSimilarAsync(null, "A", "B", CancellationToken.None)).Status.Should().Be(LastFmStatus.NotConfigured);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_failure_never_carries_the_key()
    {
        var handler = StubHttpMessageHandler.ScriptedAsync((request, _) =>
            throw new HttpRequestException($"Connection refused for {request.RequestUri}"));
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Unavailable);
        result.Message.Should().NotContain(ApiKey).And.NotContain("api_key");
    }

    [Fact]
    public async Task A_page_that_is_not_json_is_an_error_and_not_a_crash()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>check your browser</html>", Encoding.UTF8, "text/html"),
        });
        var client = Client(handler, out _);

        var result = await client.GetTrackAsync(null, "A", "B", CancellationToken.None);

        result.Status.Should().Be(LastFmStatus.Error);
    }

    [Fact]
    public void The_wiki_text_loses_markup_the_read_more_link_and_extra_whitespace()
    {
        LastFmClient.PlainText("<b>Hello</b>  &amp; <i>goodbye</i>.\n<a href=\"https://www.last.fm/x\">Read more on Last.fm</a>")
            .Should().Be("Hello & goodbye.");
        LastFmClient.PlainText("<a href=\"https://www.last.fm/x\">Read more on Last.fm</a>").Should().BeNull();
        LastFmClient.PlainText("   ").Should().BeNull();
    }

    private static LastFmClient Client(
        StubHttpMessageHandler handler,
        out FakeTimeProvider time,
        string? apiKey = ApiKey,
        ISecretRegistry? secrets = null)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        return new LastFmClient(
            new ListHttpClientFactory(handler, BaseAddress),
            new StaticOptionsMonitor<LastFmOptions>(new LastFmOptions { ApiKey = apiKey }),
            time,
            secrets ?? new SecretRegistry(),
            NullLogger<LastFmClient>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string TrackAnswer = """
        {"track":{"name":"Get Lucky","mbid":"833f00e1-781f-4edd-90e4-e52712618862",
        "url":"https://www.last.fm/music/Daft+Punk/_/Get+Lucky","listeners":"1234567","playcount":"9876543",
        "artist":{"name":"Daft Punk","url":"https://www.last.fm/music/Daft+Punk"},
        "toptags":{"tag":[{"name":"electronic"},{"name":"disco"},{"name":"funk"},{"name":"dance"},{"name":"pop"},{"name":"french"},{"name":"2013"}]},
        "wiki":{"summary":"Get Lucky is a song by <a href=\"https://www.last.fm/music/Daft+Punk\">Daft Punk</a> &amp; Pharrell. It was released in 2013. <a href=\"https://www.last.fm/music/Daft+Punk/_/Get+Lucky\">Read more on Last.fm</a>","content":"long"}}}
        """;

    private const string ArtistAnswer = """
        {"artist":{"name":"Daft Punk","url":"https://www.last.fm/music/Daft+Punk","stats":{"listeners":"3000000","playcount":"99"},
        "bio":{"summary":"Daft Punk are a <b>French</b> duo. <a href=\"https://www.last.fm/music/Daft+Punk\">Read more on Last.fm</a>"}}}
        """;

    private const string SimilarAnswer = """
        {"similartracks":{"track":[
        {"name":"Good Times","mbid":"833f00e1-781f-4edd-90e4-e52712618862","match":0.85,"url":"https://www.last.fm/music/Chic/_/Good+Times","artist":{"name":"Chic","url":"x"}},
        {"name":"Lose Yourself to Dance","mbid":"","match":"0.5","url":"https://www.last.fm/music/Daft+Punk/_/Lose+Yourself+to+Dance","artist":{"name":"Daft Punk","url":"x"}}
        ]}}
        """;
}

/// <summary>An <see cref="IOptionsMonitor{T}"/> over one instance.</summary>
/// <typeparam name="T">The options type.</typeparam>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
