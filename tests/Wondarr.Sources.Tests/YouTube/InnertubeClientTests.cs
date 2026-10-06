using System.Net;
using System.Text.Json;
using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The search call against the responses recorded from the live InnerTube on 2026-10-05. The request
/// carries the WEB_REMIX context and nothing that identifies Wondarr: no API key, no cookies beyond
/// the consent one.
/// </summary>
public class InnertubeClientTests
{
    [Fact]
    public async Task Posts_one_search_with_the_WEB_REMIX_context_and_no_api_key()
    {
        var handler = StubHttpMessageHandler.Ok(YouTubeTestData.ReadFixture("search-songs.json"));

        var search = await YouTubeTestData.Client(
            handler,
            timeProvider: FakeTime(YouTubeTestData.Recorded)).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Songs),
            CancellationToken.None);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be("https://music.youtube.com/youtubei/v1/search?alt=json");
        sent.Headers.GetValues("Origin").Should().ContainSingle().Which.Should().Be("https://music.youtube.com");
        // HttpRequestHeaders splits a non-validated User-Agent on its spaces; the sent value is the join.
        string.Join(" ", sent.Headers.GetValues("User-Agent")).Should().Be(InnertubeClient.UserAgent);
        sent.Headers.GetValues("Cookie").Should().ContainSingle().Which.Should().Be("SOCS=CAI");
        sent.Headers.GetValues("Accept").Should().ContainSingle().Which.Should().Be("*/*");
        sent.Headers.Contains("x-goog-api-key").Should().BeFalse();
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        var json = body.RootElement;
        json.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString()
            .Should().Be("WEB_REMIX");
        json.GetProperty("context").GetProperty("client").GetProperty("clientVersion").GetString()
            .Should().Be("1.20261005.01.00");
        json.GetProperty("context").GetProperty("client").GetProperty("hl").GetString().Should().Be("en");
        json.GetProperty("context").GetProperty("user").EnumerateObject().Should().BeEmpty();
        json.GetProperty("query").GetString().Should().Be("daft punk get lucky");
        json.GetProperty("params").GetString().Should().Be(InnertubeSearchRequest.SongsParams);
        json.TryGetProperty("key", out _).Should().BeFalse();
        handler.Bodies[0].Should().NotContain("AIza");

        search.Query.Should().Be("daft punk get lucky");
        search.Filter.Should().Be(InnertubeSearchFilter.Songs);
    }

    [Fact]
    public async Task Sends_no_params_for_the_unfiltered_ISRC_query()
    {
        var handler = StubHttpMessageHandler.Ok(YouTubeTestData.ReadFixture("search-isrc.json"));

        await YouTubeTestData.Client(handler).SearchAsync(
            new InnertubeSearchRequest("USWB19901214", InnertubeSearchFilter.None),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        body.RootElement.TryGetProperty("params", out _).Should().BeFalse();
        body.RootElement.GetProperty("query").GetString().Should().Be("USWB19901214");
    }

    [Fact]
    public async Task Sends_the_videos_params_for_the_videos_filter()
    {
        var handler = StubHttpMessageHandler.Ok(YouTubeTestData.ReadFixture("search-videos.json"));

        await YouTubeTestData.Client(handler).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Videos),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        body.RootElement.GetProperty("params").GetString().Should().Be(InnertubeSearchRequest.VideosParams);
    }

    [Fact]
    public async Task Points_the_request_at_the_configured_base_address()
    {
        var handler = StubHttpMessageHandler.Ok(YouTubeTestData.ReadFixture("search-songs.json"));
        var options = new YouTubeOptions { BaseUrl = "http://127.0.0.1:5099/" };

        await YouTubeTestData.Client(handler, options).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Songs),
            CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.RequestUri!.ToString()
            .Should().Be("http://127.0.0.1:5099/youtubei/v1/search?alt=json");
    }

    [Fact]
    public async Task Parses_the_recorded_songs_response()
    {
        var handler = StubHttpMessageHandler.Ok(YouTubeTestData.ReadFixture("search-songs.json"));

        var search = await YouTubeTestData.Client(handler).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Songs),
            CancellationToken.None);

        search.TopResult.Should().BeNull();
        search.Results.Should().HaveCount(6);

        var first = search.Results[0];
        first.VideoId.Should().Be("4D7u5KF7SP8");
        first.Title.Should().Be("Get Lucky (feat. Pharrell Williams and Nile Rodgers)");
        first.Artists.Should().Equal("Daft Punk", "Pharrell Williams", "Nile Rodgers");
        first.Album.Should().Be("Random Access Memories");
        first.DurationMs.Should().Be(370_000);
        first.MusicVideoType.Should().Be(InnertubeVideoTypes.ArtTrack);
        first.IsExplicit.Should().BeFalse();

        search.Results[1].DurationMs.Should().Be(249_000);
        search.Results[5].Title.Should().Be("Get Lucky");
    }

    [Fact]
    public async Task Throws_an_InnertubeException_carrying_the_status_and_the_error_message()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.BadRequest, YouTubeTestData.ReadFixture("search-error.json"));

        var act = () => YouTubeTestData.Client(handler).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Songs),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InnertubeException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("Invalid JSON payload received");
    }

    [Fact]
    public async Task Answers_an_empty_result_for_a_response_without_shelves()
    {
        var handler = StubHttpMessageHandler.Ok("{}");

        var search = await YouTubeTestData.Client(handler).SearchAsync(
            new InnertubeSearchRequest("daft punk get lucky", InnertubeSearchFilter.Songs),
            CancellationToken.None);

        search.TopResult.Should().BeNull();
        search.Results.Should().BeEmpty();
    }

    private static FakeTimeProvider FakeTime(DateTimeOffset now)
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(now);
        return provider;
    }
}
