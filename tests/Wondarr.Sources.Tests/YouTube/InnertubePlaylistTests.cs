using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
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

        var rows = await Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None);

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
        var handler = Scripted((HttpStatusCode.OK, "{}"));

        await Client(handler).BrowsePlaylistAsync("VL" + PlaylistId, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        body.RootElement.GetProperty("browseId").GetString().Should().Be("VL" + PlaylistId);
    }

    [Fact]
    public async Task Answers_no_rows_for_a_response_without_a_shelf()
    {
        var handler = Scripted((HttpStatusCode.OK, "{}"));

        var rows = await Client(handler).BrowsePlaylistAsync(PlaylistId, CancellationToken.None);

        rows.Should().BeEmpty();
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
        innertube.BrowsePlaylistAsync(PlaylistId, Arg.Any<CancellationToken>()).Returns(rows);

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

    /// <summary>The answer a private or unknown playlist gets, as a method group so NSubstitute's <c>Returns</c> overloads stay unambiguous.</summary>
    private static Task<IReadOnlyList<InnertubePlaylistRow>> NotFound(NSubstitute.Core.CallInfo call) =>
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
