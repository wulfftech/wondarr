using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The playlist browse against the responses recorded from the live InnerTube on 2026-10-07: page 1
/// under the playlist shelf, the continuation answer under its append action, and the provider's
/// reading of both.
/// </summary>
public class InnertubePlaylistTests
{
    private const string PlaylistId = "PL15B1E77BB5708555";

    [Fact]
    public async Task Reads_both_pages_in_order_and_stops_at_the_last_one()
    {
        var handler = Scripted(
            (HttpStatusCode.OK, YouTubeTestData.ReadFixture("browse-playlist-PL15B1E77BB5708555-page1.json")),
            (HttpStatusCode.OK, YouTubeTestData.ReadFixture("browse-playlist-PL15B1E77BB5708555-page2-last.json")));

        var playlist = await Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None);
        var rows = playlist.Rows;

        playlist.Truncated.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.ToString().Should().Be("https://music.youtube.com/youtubei/v1/browse?alt=json");
        handler.Requests[1].RequestUri!.ToString().Should().Be("https://music.youtube.com/youtubei/v1/browse?alt=json");

        // The first request names the playlist; the second carries only the continuation token.
        using (var first = JsonDocument.Parse(handler.Bodies[0]))
        {
            first.RootElement.GetProperty("browseId").GetString().Should().Be("VL" + PlaylistId);
            first.RootElement.TryGetProperty("continuation", out _).Should().BeFalse();
        }

        using (var second = JsonDocument.Parse(handler.Bodies[1]))
        {
            second.RootElement.GetProperty("continuation").GetString().Should().StartWith("4qmFsg");
            second.RootElement.TryGetProperty("browseId", out _).Should().BeFalse();
        }

        rows.Select(row => row.VideoId).Should().Equal(
            "kJQP7kiw5Fk",
            "RgKAFK5djSk",
            "JGwWNGJdvx8",
            "sGIm0-dQd8M",
            "wfWkmURBNv8");

        var despacito = rows[0];
        despacito.Title.Should().Be("Despacito");
        despacito.Artist.Should().Be("Luis Fonsi");
        despacito.ArtistCredit.Should().Be("Luis Fonsi & Daddy Yankee");
        despacito.Album.Should().BeNull();
        despacito.DurationMs.Should().Be(282_000);

        rows[1].Title.Should().Be("See You Again (feat. Charlie Puth)");
        rows[1].Artist.Should().Be("Wiz Khalifa");
        rows[1].DurationMs.Should().Be(238_000);

        rows[2].Title.Should().Be("Shape of You");
        rows[2].Artist.Should().Be("Ed Sheeran");
        rows[2].DurationMs.Should().Be(264_000);

        rows[3].Title.Should().Be("Dura");
        rows[3].Artist.Should().Be("Daddy Yankee");
        rows[3].DurationMs.Should().Be(218_000);

        rows[4].Title.Should().Be("El Farsante (Remix)");
        rows[4].Artist.Should().Be("Ozuna");
        rows[4].ArtistCredit.Should().Be("Ozuna & Romeo Santos");
        rows[4].DurationMs.Should().Be(302_000);
    }

    [Fact]
    public async Task Accepts_an_id_that_already_carries_the_vl_prefix()
    {
        var handler = Scripted((HttpStatusCode.OK, EmptyShelf));

        await Client(handler).BrowsePlaylistAsync("VL" + PlaylistId, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        body.RootElement.GetProperty("browseId").GetString().Should().Be("VL" + PlaylistId);
    }

    [Fact]
    public async Task A_playlist_shelf_with_no_rows_is_an_empty_playlist()
    {
        var handler = Scripted((HttpStatusCode.OK, EmptyShelf));

        var playlist = await Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None);

        playlist.Rows.Should().BeEmpty();
        playlist.Truncated.Should().BeFalse();
    }

    // R-007: a layout YouTube changed is a failed read, never an empty playlist.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"contents":{"somethingNew":{}}}""")]
    public async Task A_response_without_the_playlist_shelf_is_a_layout_failure(string body)
    {
        var handler = Scripted((HttpStatusCode.OK, body));

        await FluentActions.Awaiting(() => Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None))
            .Should().ThrowAsync<InnertubeLayoutException>().WithMessage("*the first page*");
    }

    [Fact]
    public async Task A_continuation_page_that_does_not_parse_is_a_layout_failure_not_the_end_of_the_list()
    {
        var handler = Scripted(
            (HttpStatusCode.OK, YouTubeTestData.ReadFixture("browse-playlist-PL15B1E77BB5708555-page1.json")),
            (HttpStatusCode.OK, "{}"));

        await FluentActions.Awaiting(() => Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None))
            .Should().ThrowAsync<InnertubeLayoutException>().WithMessage("*a continuation page*");
    }

    [Fact]
    public async Task A_read_that_runs_out_of_pages_before_the_playlist_does_is_truncated()
    {
        var first = YouTubeTestData.ReadFixture("browse-playlist-PL15B1E77BB5708555-page1.json");

        // Every continuation answers one row and a token for yet another page.
        const string more =
            """{"onResponseReceivedActions":[{"appendContinuationItemsAction":{"continuationItems":[{"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"abcdefghijk"}}},{"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"next"}}}}]}}]}""";
        var handler = Scripted(
            [(HttpStatusCode.OK, first), .. Enumerable.Repeat((HttpStatusCode.OK, more), InnertubeClient.MaxPlaylistPages - 1)]);

        var playlist = await Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None);

        handler.Requests.Should().HaveCount(InnertubeClient.MaxPlaylistPages);
        playlist.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Throws_an_InnertubeException_naming_the_browse_call()
    {
        var handler = Scripted((HttpStatusCode.NotFound, YouTubeTestData.ReadFixture("search-error.json")));

        var thrown = (await FluentActions.Awaiting(
            () => Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None))
            .Should().ThrowAsync<InnertubeException>()).Which;

        thrown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        thrown.Message.Should().StartWith("InnerTube browse failed with HTTP 404");
    }

    [Theory]
    [InlineData("PL15B1E77BB5708555")]
    [InlineData("https://music.youtube.com/playlist?list=PL15B1E77BB5708555")]
    [InlineData("https://www.youtube.com/playlist?list=PL15B1E77BB5708555")]
    [InlineData("https://www.youtube.com/watch?v=kJQP7kiw5Fk&list=PL15B1E77BB5708555")]
    [InlineData("music.youtube.com/playlist?list=PL15B1E77BB5708555")]
    public void Accepts_a_bare_id_and_the_playlist_links_shapes(string value)
    {
        var provider = new YouTubeMusicPlaylistProvider(Substitute.For<IInnertubeClient>());

        provider.Validate(Settings(("playlist", value)), null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://www.deezer.com/playlist/908622995")]
    [InlineData("short")]
    [InlineData("https://www.youtube.com/watch?v=kJQP7kiw5Fk")]
    public void Refuses_something_that_is_not_a_playlist_link_or_id(string value)
    {
        var provider = new YouTubeMusicPlaylistProvider(Substitute.For<IInnertubeClient>());

        provider.Validate(Settings(("playlist", value)), null).Should().ContainSingle();
    }

    [Fact]
    public void Refuses_a_missing_playlist()
    {
        var provider = new YouTubeMusicPlaylistProvider(Substitute.For<IInnertubeClient>());

        provider.Validate(Settings(), null).Should().ContainSingle().Which.Should().Contain("Enter a YouTube Music");
    }

    [Fact]
    public async Task Entries_carry_the_text_the_sync_resolves_by()
    {
        var rows = new InnertubePlaylistRow[]
        {
            new("kJQP7kiw5Fk", "Despacito", "Luis Fonsi", "Luis Fonsi & Daddy Yankee", null, 282_000),
            new("RgKAFK5djSk", "See You Again (feat. Charlie Puth)", "Wiz Khalifa", "Wiz Khalifa", "Furious 7", 238_000),
        };
        var innertube = Substitute.For<IInnertubeClient>();
        innertube.BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>()).Returns(new InnertubePlaylist(rows));

        var result = await new YouTubeMusicPlaylistProvider(innertube).FetchAsync(
            List(("playlist", PlaylistId)),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(2);

        var first = result.Entries[0];
        first.ExternalId.Should().Be("ytm:kJQP7kiw5Fk");
        first.Artist.Should().Be("Luis Fonsi");
        first.Title.Should().Be("Despacito");
        first.Album.Should().BeNull();
        first.DurationMs.Should().Be(282_000);
        first.Isrc.Should().BeNull();

        result.Entries[1].Album.Should().Be("Furious 7");
    }

    [Fact]
    public async Task A_not_found_or_private_playlist_fails_with_its_message()
    {
        var innertube = Substitute.For<IInnertubeClient>();
        innertube
            .BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>())
            .Returns(NotFound);

        var result = await new YouTubeMusicPlaylistProvider(innertube).FetchAsync(
            List(("playlist", PlaylistId)),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Entries.Should().BeEmpty();
        result.Error.Should().Be("The YouTube Music playlist was not found, or it is private.");
    }

    // R-076: a 429 or a 5xx takes the failure path (a failed read the sync records), not an exception.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_rate_limited_or_failing_youtube_is_a_failed_read(HttpStatusCode status)
    {
        var innertube = Substitute.For<IInnertubeClient>();
        innertube
            .BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>())
            .Returns<Task<InnertubePlaylist>>(_ => throw new InnertubeException(status, "slow down", "browse"));

        var result = await new YouTubeMusicPlaylistProvider(innertube).FetchAsync(
            List(("playlist", PlaylistId)),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Status.Should().Be(ImportListReadStatus.Failed);
        result.Error.Should().Be(string.Create(CultureInfo.InvariantCulture, $"YouTube Music answered HTTP {(int)status}."));
    }

    [Fact]
    public async Task A_layout_failure_or_unparseable_body_is_a_failed_read_not_an_empty_list()
    {
        var innertube = Substitute.For<IInnertubeClient>();
        innertube
            .BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>())
            .Returns<Task<InnertubePlaylist>>(_ => throw new InnertubeLayoutException("the first page"));

        var result = await new YouTubeMusicPlaylistProvider(innertube).FetchAsync(
            List(("playlist", PlaylistId)),
            CancellationToken.None);

        result.Status.Should().Be(ImportListReadStatus.Failed);
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_playlist_read_cut_at_the_page_cap_is_a_truncated_read()
    {
        var innertube = Substitute.For<IInnertubeClient>();
        innertube
            .BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>())
            .Returns(new InnertubePlaylist([new InnertubePlaylistRow("kJQP7kiw5Fk", "Despacito", "Luis Fonsi", null, null, null)], Truncated: true));

        var result = await new YouTubeMusicPlaylistProvider(innertube).FetchAsync(
            List(("playlist", PlaylistId)),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Status.Should().Be(ImportListReadStatus.Truncated);
    }

    private const string EmptyShelf =
        """{"contents":{"twoColumnBrowseResultsRenderer":{"secondaryContents":{"sectionListRenderer":{"contents":[{"musicPlaylistShelfRenderer":{}}]}}}}}""";

    /// <summary>The answer a private or unknown playlist gets, as a method group so NSubstitute's <c>Returns</c> overloads stay unambiguous.</summary>
    private static Task<InnertubePlaylist> NotFound(NSubstitute.Core.CallInfo call) =>
        throw new InnertubeException(HttpStatusCode.NotFound, "The requested item was not found.");

    private static InnertubeClient Client(StubbedInnertubeHandler handler) =>
        new(
            new HttpClient(handler),
            YouTubeTestData.Monitor(new YouTubeOptions()),
            FakeTime(YouTubeTestData.Recorded));

    private static FakeTimeProvider FakeTime(DateTimeOffset now)
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(now);
        return provider;
    }

    private static StubbedInnertubeHandler Scripted(params (HttpStatusCode Status, string Body)[] responses) =>
        new(responses);

    private static ImportList List(params (string Name, string Value)[] fields)
    {
        var json = string.Concat(
            "{",
            string.Join(",", fields.Select(field => string.Create(
                CultureInfo.InvariantCulture,
                $"\"{field.Name}\":\"{field.Value}\""))),
            "}");

        return new ImportList { Type = YouTubeMusicPlaylistProvider.YouTubeMusicPlaylistType, Settings = json };
    }

    private static JsonElement Settings(params (string Name, string Value)[] fields)
    {
        var json = string.Concat(
            "{",
            string.Join(",", fields.Select(field => string.Create(
                CultureInfo.InvariantCulture,
                $"\"{field.Name}\":\"{field.Value}\""))),
            "}");

        // Clone: the document is disposed as soon as the expression ends, and the element outlives it.
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}

/// <summary>
/// Serves a scripted sequence of responses and records each request's body while the request is still
/// alive — the two-page playlist read needs one body per page.
/// </summary>
internal sealed class StubbedInnertubeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

    public StubbedInnertubeHandler(params (HttpStatusCode Status, string Body)[] responses) =>
        _responses = new Queue<(HttpStatusCode Status, string Body)>(responses);

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());

        var (status, body) = _responses.Dequeue();

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
