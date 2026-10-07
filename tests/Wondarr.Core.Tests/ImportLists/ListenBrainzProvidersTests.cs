using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists.ListenBrainz;
using Wondarr.Core.Tests.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The ListenBrainz import lists: the loved and playlist reads against the responses recorded on
/// 2026-10-07 (see <c>tests/fixtures/listenbrainz/README.md</c>), the anti-bot page that must never
/// read as an empty list, and the rate-limit rules.
/// </summary>
public sealed class ListenBrainzProvidersTests
{
    private const string User = "mr_monkey";

    private const string PlaylistMbid = "34c1bb9f-ef5f-4b5a-9305-1be299e86bb0";

    /// <summary>The base address the named <c>listenbrainz</c> client is registered with.</summary>
    private static readonly string BaseAddress = Wondarr.Core.Metadata.ServiceCollectionExtensions.ListenBrainzBaseUrl;

    [Fact]
    public async Task Loved_reads_the_recorded_page_and_stops_when_the_total_is_reached()
    {
        var handler = StubHttpMessageHandler.Scripted(request => Json(
            request.Query.Contains("offset=0", StringComparison.Ordinal)
                ? Fixture("feedback-loved-p0.json")
                : """{"count":0,"feedback":[],"offset":100,"total_count":3}"""));
        var provider = new ListenBrainzLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(Loved(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(3);
        result.Entries[0].ExternalId.Should().Be("lb:e53766ad-b80e-40ab-b28c-a2f0a1aa414e");
        result.Entries[0].MbRecordingId.Should().Be("e53766ad-b80e-40ab-b28c-a2f0a1aa414e");
        result.Entries[0].Title.Should().Be("Y4R4");
        result.Entries[0].Artist.Should().Be("Seren Saraç");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].AbsolutePath.Should().Be("/1/feedback/user/mr_monkey/get-feedback");
        handler.Requests[0].Query.Should().Contain("score=1");
        handler.Requests[0].Query.Should().Contain("count=100");
        handler.Requests[0].Query.Should().Contain("metadata=true");
        handler.Requests[1].Query.Should().Contain("offset=100");
    }

    [Fact]
    public async Task A_row_without_a_recording_mbid_uses_its_msid_and_carries_no_mbid()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(string.Concat(
            @"{""count"":1,""total_count"":1,""offset"":0,""feedback"":[{""recording_mbid"":"""",",
            @"""recording_msid"":""9cfc7ab2-c887-48af-a90c-56f5ec9efd85"",""track_metadata"":",
            @"{""track_name"":""Y4R4"",""artist_name"":""Seren Saraç""}}]}")));
        var provider = new ListenBrainzLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(Loved(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().ContainSingle();
        result.Entries[0].ExternalId.Should().Be("lb:9cfc7ab2-c887-48af-a90c-56f5ec9efd85");
        result.Entries[0].MbRecordingId.Should().BeNull();
        result.Entries[0].Title.Should().Be("Y4R4");
        result.Entries[0].Artist.Should().Be("Seren Saraç");
    }

    [Fact]
    public async Task Loved_fails_when_the_user_is_unknown()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"code":404,"message":"User not found"}""", HttpStatusCode.NotFound));
        var provider = new ListenBrainzLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(Loved(), CancellationToken.None);

        result.Error.Should().Be($"ListenBrainz has no user named '{User}'.");
    }

    [Fact]
    public async Task Playlist_reads_the_recorded_jspf()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(Fixture("playlist-34c1bb9f.json")));
        var provider = new ListenBrainzPlaylistProvider(new ListHttpClientFactory(handler, BaseAddress));

        var result = await provider.FetchAsync(Playlist(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(3);
        result.Entries[0].ExternalId.Should().Be("lb:f99c04dc-9f75-4af8-bdf8-465c40c18822");
        result.Entries[0].MbRecordingId.Should().Be("f99c04dc-9f75-4af8-bdf8-465c40c18822");
        result.Entries[0].Title.Should().Be("Imagine");
        result.Entries[0].Artist.Should().Be("A Perfect Circle");
        result.Entries[0].Album.Should().Be("eMOTIVe");
        result.Entries[0].DurationMs.Should().Be(296_000);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].AbsolutePath.Should().Be($"/1/playlist/{PlaylistMbid}");
    }

    [Fact]
    public async Task A_track_without_a_recording_identifier_keeps_its_place()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json(string.Concat(
            @"{""playlist"":{""title"":""Throwback"",""track"":[",
            @"{""title"":""Imagine"",""creator"":""A Perfect Circle"",""album"":""eMOTIVe"",""duration"":296000,",
            @"""identifier"":[""https://musicbrainz.org/recording/f99c04dc-9f75-4af8-bdf8-465c40c18822""]},",
            @"{""title"":""Mystery"",""creator"":""Nobody"",""duration"":1000}]}}")));
        var provider = new ListenBrainzPlaylistProvider(new ListHttpClientFactory(handler, BaseAddress));

        var result = await provider.FetchAsync(Playlist(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(2);
        result.Entries[1].ExternalId.Should().Be("lb:1");
        result.Entries[1].MbRecordingId.Should().BeNull();
    }

    [Fact]
    public async Task A_browser_check_page_fails_the_read()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Html(Fixture("browser-check.html")));
        var loved = new ListenBrainzLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);
        var playlist = new ListenBrainzPlaylistProvider(new ListHttpClientFactory(handler, BaseAddress));

        (await loved.FetchAsync(Loved(), CancellationToken.None)).Error.Should()
            .Be("ListenBrainz asked for a browser check; the list will be read again later.");
        (await playlist.FetchAsync(Playlist(), CancellationToken.None)).Error.Should()
            .Be("ListenBrainz asked for a browser check; the list will be read again later.");
    }

    [Fact]
    public async Task A_rate_limit_fails_the_read_with_a_try_later_message()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("{}", HttpStatusCode.TooManyRequests));
        var provider = new ListenBrainzLovedProvider(new ListHttpClientFactory(handler, BaseAddress), TimeProvider.System);

        var result = await provider.FetchAsync(Loved(), CancellationToken.None);

        result.Error.Should().Be("ListenBrainz is rate limiting Wondarr; the list will be read again later.");
    }

    [Fact]
    public async Task A_spent_quota_waits_for_the_reset_before_the_next_page()
    {
        var handler = StubHttpMessageHandler.Scripted(request =>
        {
            var answer = Json(
                request.Query.Contains("offset=0", StringComparison.Ordinal)
                    ? Fixture("feedback-loved-p0.json")
                    : """{"count":0,"feedback":[],"offset":100,"total_count":3}""");

            answer.Headers.Add("X-RateLimit-Limit", "30");
            answer.Headers.Add("X-RateLimit-Remaining", "0");
            answer.Headers.Add("X-RateLimit-Reset-In", "2");

            return answer;
        });

        var waits = new List<TimeSpan>();
        var provider = new ListenBrainzLovedProvider(
            new ListHttpClientFactory(handler, BaseAddress),
            (delay, _) =>
            {
                waits.Add(delay);

                return Task.CompletedTask;
            });

        var result = await provider.FetchAsync(Loved(), CancellationToken.None);

        result.Success.Should().BeTrue();
        waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_playlist_link_is_parsed_to_its_mbid()
    {
        var provider = new ListenBrainzPlaylistProvider(new ListHttpClientFactory(Empty(), BaseAddress));

        provider.Validate(Settings($$"""{"playlist":"https://listenbrainz.org/playlist/{{PlaylistMbid}}/"}"""), null)
            .Should().BeEmpty();
        provider.Validate(Settings($$"""{"playlist":"{{PlaylistMbid}}"}"""), null).Should().BeEmpty();
        provider.Validate(Settings("{}"), null).Should().ContainSingle().Which.Should().Contain("playlist");
        provider.Validate(Settings("""{"playlist":"https://listenbrainz.org/playlist/nope"}"""), null)
            .Should().ContainSingle().Which.Should().Contain("does not look like");
    }

    [Fact]
    public async Task A_missing_playlist_fails_with_the_not_found_message()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => Json("""{"code":404}""", HttpStatusCode.NotFound));
        var provider = new ListenBrainzPlaylistProvider(new ListHttpClientFactory(handler, BaseAddress));

        var result = await provider.FetchAsync(Playlist(), CancellationToken.None);

        result.Error.Should().Be("The ListenBrainz playlist was not found, or it is private.");
    }

    private static ImportList Loved(string? settings = null) => new()
    {
        Type = ListenBrainzLovedProvider.ListenBrainzLovedType,
        Settings = settings ?? $$"""{"user":"{{User}}"}""",
    };

    private static ImportList Playlist(string? settings = null) => new()
    {
        Type = ListenBrainzPlaylistProvider.ListenBrainzPlaylistType,
        Settings = settings ?? $$"""{"playlist":"https://listenbrainz.org/playlist/{{PlaylistMbid}}"}""",
    };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "listenbrainz", name));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static StubHttpMessageHandler Empty() => StubHttpMessageHandler.Scripted(_ => Json("{}"));

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
