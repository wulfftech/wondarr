using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// M-70 item 2: the "is there a better copy?" check of <see cref="YtDlpRunner"/> against a fake process runner: no network
/// and no real yt-dlp.
/// </summary>
public sealed class YtDlpBestHeightTests
{
    private static YtDlpRunner Build(FakeProcessRunner runner, YouTubeOptions? options = null) =>
        new(
            runner,
            new FixedYtDlpBinary(),
            new TestOptionsMonitor<YouTubeOptions>(options ?? new YouTubeOptions()),
            NullLogger<YtDlpRunner>.Instance);

    [Fact]
    public async Task It_asks_for_the_height_under_the_downloads_format_rule_and_downloads_nothing()
    {
        var runner = new FakeProcessRunner().Enqueue("360\n");

        var height = await Build(runner).ReadBestHeightAsync("VMnjF1O4eH0", 1080, CancellationToken.None);

        height.Should().Be(360);
        var call = runner.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().ContainInOrder("-f", "bv*[height<=1080]+ba/b[height<=1080]");
        call.Arguments.Should().Contain("--skip-download").And.ContainInOrder("--print", "%(height)s");
        call.Arguments.Should().NotContain("--write-thumbnail").And.NotContain("--merge-output-format");
        call.Arguments.Should().ContainInOrder("--", "https://www.youtube.com/watch?v=VMnjF1O4eH0");
    }

    [Fact]
    public async Task Without_a_cap_it_uses_the_uncapped_rule_and_sends_the_cookies()
    {
        var runner = new FakeProcessRunner().Enqueue("720\n");

        await Build(runner, new YouTubeOptions { CookiesPath = "/config/cookies.txt" }).ReadBestHeightAsync("abc", null, CancellationToken.None);

        runner.Calls.Single().Arguments.Should().ContainInOrder("-f", "bv*+ba/b").And.ContainInOrder("--cookies", "/config/cookies.txt");
    }

    [Theory]
    [InlineData("NA\n", null)]
    [InlineData("", null)]
    [InlineData("NA\n480\n", 480)]
    [InlineData("0\n", null)]
    public async Task A_height_yt_dlp_did_not_print_is_unknown(string output, int? expected)
    {
        var runner = new FakeProcessRunner().Enqueue(output);

        var height = await Build(runner).ReadBestHeightAsync("abc", 1080, CancellationToken.None);

        height.Should().Be(expected);
    }

    [Fact]
    public async Task A_failed_run_throws_a_classified_error()
    {
        var runner = new FakeProcessRunner().Enqueue(1, "ERROR: [youtube] abc: Video unavailable");

        var act = () => Build(runner).ReadBestHeightAsync("abc", 1080, CancellationToken.None);

        (await act.Should().ThrowAsync<YtDlpException>()).Which.Message.Should().Contain("Video unavailable");
    }

    [Fact]
    public async Task It_waits_for_the_shared_gate()
    {
        var release = new TaskCompletionSource<Wondarr.Core.Media.ProcessResult>();
        var runner = new FakeProcessRunner()
            .Enqueue(_ => release.Task)
            .Enqueue("360\n");
        using var yt = Build(runner);

        var first = yt.DownloadVideoAsync("abc", "/grabs/video-1", null, CancellationToken.None);
        await Task.Delay(100);
        var check = yt.ReadBestHeightAsync("abc", 1080, CancellationToken.None);
        await Task.Delay(100);

        check.IsCompleted.Should().BeFalse("the download holds the gate");
        runner.Calls.Should().ContainSingle();

        release.SetResult(new Wondarr.Core.Media.ProcessResult(0, "/grabs/video-1/abc.mkv\n10\n", string.Empty, TimedOut: false));
        await first;

        (await check).Should().Be(360);
    }
}
