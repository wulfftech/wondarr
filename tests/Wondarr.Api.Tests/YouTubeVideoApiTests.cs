using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/youtube/video</c>, the details read behind the Match queue's "Add to MusicBrainz" form
/// (DECISIONS build session 14 #33). yt-dlp is substituted: what it is asked and what each failure becomes
/// is the point here.
/// </summary>
public sealed class YouTubeVideoApiTests
{
    private const string Endpoint = "/api/v1/youtube/video";
    private const string VideoId = "aBcDeFgHiJk";

    [Fact]
    public async Task Reads_the_details_of_the_video_a_link_names()
    {
        var ytDlp = Substitute.For<IYtDlpRunner>();
        ytDlp.ReadVideoAsync(VideoId, Arg.Any<CancellationToken>())
            .Returns(new YtDlpVideo(VideoId, "Yeah the Boys (Official Video)", "Hype Duo", new DateOnly(2019, 3, 12), 196_320));

        using var factory = Factory(ytDlp, enabled: true);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await GetAsync(client, "https://youtu.be/aBcDeFgHiJk?si=tracking");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var video = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        video["videoId"]!.GetValue<string>().Should().Be(VideoId);
        video["url"]!.GetValue<string>().Should().Be("https://www.youtube.com/watch?v=aBcDeFgHiJk");
        video["title"]!.GetValue<string>().Should().Be("Yeah the Boys (Official Video)");
        video["channel"]!.GetValue<string>().Should().Be("Hype Duo");
        video["uploadDate"]!.GetValue<string>().Should().Be("2019-03-12");
        video["durationMs"]!.GetValue<int>().Should().Be(196_320);
    }

    [Fact]
    public async Task A_link_that_is_not_one_youtube_video_is_a_validation_problem_and_nothing_runs()
    {
        var ytDlp = Substitute.For<IYtDlpRunner>();

        using var factory = Factory(ytDlp, enabled: true);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await GetAsync(client, "https://evil.example/watch?v=aBcDeFgHiJk");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await ytDlp.DidNotReceiveWithAnyArgs().ReadVideoAsync(default!, default);
    }

    [Fact]
    public async Task While_the_youtube_source_is_off_wondarr_does_not_read_youtube()
    {
        var ytDlp = Substitute.For<IYtDlpRunner>();

        using var factory = Factory(ytDlp, enabled: false);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await GetAsync(client, "https://www.youtube.com/watch?v=aBcDeFgHiJk");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        problem["title"]!.GetValue<string>().Should().Be("The YouTube source is off");
        await ytDlp.DidNotReceiveWithAnyArgs().ReadVideoAsync(default!, default);
    }

    [Theory]
    [InlineData(YtDlpErrorKind.PrivateOrUnavailable, HttpStatusCode.NotFound)]
    [InlineData(YtDlpErrorKind.TimedOut, HttpStatusCode.ServiceUnavailable)]
    [InlineData(YtDlpErrorKind.ToolMissing, HttpStatusCode.ServiceUnavailable)]
    [InlineData(YtDlpErrorKind.BotCheck, HttpStatusCode.ServiceUnavailable)]
    [InlineData(YtDlpErrorKind.Unknown, HttpStatusCode.BadGateway)]
    public async Task A_failed_read_says_why(YtDlpErrorKind kind, HttpStatusCode status)
    {
        var ytDlp = Substitute.For<IYtDlpRunner>();
        ytDlp.ReadVideoAsync(VideoId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new YtDlpException(VideoId, kind, "the reason"));

        using var factory = Factory(ytDlp, enabled: true);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await GetAsync(client, "https://www.youtube.com/watch?v=aBcDeFgHiJk");

        response.StatusCode.Should().Be(status);
        var problem = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        problem["detail"]!.GetValue<string>().Should().Be("the reason");
    }

    [Fact]
    public async Task Reading_a_video_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, "https://www.youtube.com/watch?v=aBcDeFgHiJk");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static WondarrAppFactory Factory(IYtDlpRunner ytDlp, bool enabled) =>
        new(configureServices: services =>
        {
            services.AddSingleton(ytDlp);
            services.PostConfigure<YouTubeOptions>(options => options.Enabled = enabled);
        });

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string link) =>
        client.GetAsync(new Uri($"{Endpoint}?url={Uri.EscapeDataString(link)}", UriKind.Relative));
}
