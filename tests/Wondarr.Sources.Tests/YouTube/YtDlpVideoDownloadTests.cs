using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The music video download of <see cref="YtDlpRunner"/> against a fake <see cref="IProcessRunner"/>: no network and no
/// real yt-dlp.
/// </summary>
public sealed class YtDlpVideoDownloadTests
{
    private static readonly string[] OutputTemplateArguments = ["-o", "/grabs/video-1/%(id)s.%(ext)s"];

    private static YtDlpRunner Build(FakeProcessRunner runner, YouTubeOptions? options = null) =>
        new(
            runner,
            new TestOptionsMonitor<YouTubeOptions>(options ?? new YouTubeOptions()),
            NullLogger<YtDlpRunner>.Instance);

    [Fact]
    public async Task Without_a_height_cap_it_takes_the_best_video_and_audio_and_merges_to_mkv()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/video-1/u7K72X4eo_s.mkv\n284.0\n");

        var result = await Build(runner).DownloadVideoAsync("u7K72X4eo_s", "/grabs/video-1", maxHeight: null, CancellationToken.None);

        result.VideoId.Should().Be("u7K72X4eo_s");
        result.FilePath.Should().Be("/grabs/video-1/u7K72X4eo_s.mkv");
        result.ThumbnailPath.Should().Be("/grabs/video-1/u7K72X4eo_s.jpg");
        result.DurationMs.Should().Be(284_000);

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("yt-dlp");
        call.Timeout.Should().Be(TimeSpan.FromSeconds(600));
        call.Arguments.Should().ContainInOrder(
            "-f",
            "bv*+ba/b",
            "--merge-output-format",
            "mkv",
            "--remux-video",
            "mkv",
            "--write-thumbnail",
            "--convert-thumbnails",
            "jpg",
            "--no-playlist");
        call.Arguments.Should().Contain("--print").And.Contain("after_move:filepath");
        call.Arguments.Should().Contain(OutputTemplateArguments);
        call.Arguments.Should().ContainInOrder("--", "https://www.youtube.com/watch?v=u7K72X4eo_s");
    }

    [Fact]
    public async Task With_a_height_cap_the_format_selector_is_capped_on_both_branches()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/video-1/u7K72X4eo_s.mkv\n284.0\n");

        await Build(runner).DownloadVideoAsync("u7K72X4eo_s", "/grabs/video-1", maxHeight: 1080, CancellationToken.None);

        runner.Calls.Single().Arguments.Should().ContainInOrder("-f", "bv*[height<=1080]+ba/b[height<=1080]");
        runner.Calls.Single().Arguments.Should().NotContain("bv*+ba/b");
    }

    [Fact]
    public async Task It_uses_the_audio_downloads_retries_and_sleeps()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/video-1/abc.mkv\n10\n");

        await Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        runner.Calls.Single().Arguments.Should().ContainInOrder(
            "--retries",
            "5",
            "--sleep-requests",
            "0.75",
            "--sleep-interval",
            "10",
            "--max-sleep-interval",
            "20");
    }

    [Fact]
    public async Task It_sends_the_cookies_and_the_po_token_provider_like_the_audio_download()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/video-1/abc.mkv\n10\n");
        var options = new YouTubeOptions { CookiesPath = "/config/cookies.txt", PoTokenBaseUrl = "http://localhost:4416" };

        await Build(runner, options).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        var arguments = runner.Calls.Single().Arguments;
        arguments.Should().ContainInOrder("--cookies", "/config/cookies.txt");
        arguments.Should().ContainInOrder("--extractor-args", "youtubepot-bgutilhttp:base_url=http://localhost:4416");
    }

    [Theory]
    [InlineData("/grabs/video-1/abc.webm")]
    [InlineData("/grabs/video-1/abc.mp4")]
    [InlineData("/grabs/video-1/abc.opus")]
    public async Task A_result_that_is_not_an_mkv_throws(string printedPath)
    {
        var runner = new FakeProcessRunner().Enqueue($"{printedPath}\n284.0\n");

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.Unknown);
        exception.Which.Message.Should().Contain("not the expected .mkv");
    }

    [Fact]
    public async Task A_run_that_prints_no_path_throws()
    {
        var runner = new FakeProcessRunner().Enqueue("284.0\n");

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        (await act.Should().ThrowAsync<YtDlpException>()).Which.Message.Should().Contain("no file path");
    }

    [Fact]
    public async Task A_missing_length_is_null()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/video-1/abc.mkv\nNA\n");

        var result = await Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        result.DurationMs.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_run_is_classified()
    {
        var runner = new FakeProcessRunner().Enqueue(1, "ERROR: [youtube] abc: Video unavailable");

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Message.Should().Contain("Video unavailable");
    }

    [Fact]
    public async Task A_timed_out_run_is_a_timeout()
    {
        var runner = new FakeProcessRunner().Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        (await act.Should().ThrowAsync<YtDlpException>()).Which.Kind.Should().Be(YtDlpErrorKind.TimedOut);
    }

    [Fact]
    public async Task A_missing_tool_is_reported_as_such()
    {
        var runner = new FakeProcessRunner().EnqueueMissing("yt-dlp");

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);

        (await act.Should().ThrowAsync<YtDlpException>()).Which.Kind.Should().Be(YtDlpErrorKind.ToolMissing);
    }

    [Fact]
    public async Task A_height_cap_of_zero_is_refused_before_yt_dlp_runs()
    {
        var runner = new FakeProcessRunner();

        var act = () => Build(runner).DownloadVideoAsync("abc", "/grabs/video-1", 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task It_shares_the_audio_downloads_one_at_a_time_gate()
    {
        var release = new TaskCompletionSource<ProcessResult>();
        var runner = new FakeProcessRunner()
            .Enqueue(_ => release.Task)
            .Enqueue("/grabs/audio/abc.opus\n");
        using var yt = Build(runner);

        var video = yt.DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);
        var audio = yt.DownloadAsync("def", "/grabs/audio", CancellationToken.None);

        await Task.Delay(100);
        runner.Calls.Should().ContainSingle("the audio download waits for the video download's gate");

        release.SetResult(new ProcessResult(0, "/grabs/video-1/abc.mkv\n10\n", string.Empty, TimedOut: false));
        await video;
        await audio;

        runner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_audio_download_still_builds_exactly_its_own_arguments()
    {
        var runner = new FakeProcessRunner().Enqueue("/grabs/queue-1/abc123.opus\n");

        await Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var arguments = runner.Calls.Single().Arguments;
        arguments.Should().ContainInOrder("-f", "bestaudio[acodec=opus]/bestaudio/best", "-x", "--audio-format", "opus");
        arguments.Should().NotContain("--merge-output-format").And.NotContain("--remux-video").And.NotContain("--write-thumbnail");
    }
}
