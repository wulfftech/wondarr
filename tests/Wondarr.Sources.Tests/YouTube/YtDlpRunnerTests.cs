using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The yt-dlp runner against a fake <see cref="IProcessRunner"/> serving the canned fixtures under
/// <c>tests/fixtures/ytdlp</c> — no network, no real yt-dlp.
/// </summary>
public class YtDlpRunnerTests
{
    private static readonly string[] OutputTemplateArguments = ["-o", "/grabs/queue-1/%(id)s.%(ext)s"];

    private static readonly string[] CookiesArguments = ["--cookies", "/config/cookies.txt"];

    private static readonly string[] PoTokenArguments =
        ["--extractor-args", "youtubepot-bgutilhttp:base_url=http://localhost:4416"];

    private static YtDlpRunner Build(FakeProcessRunner runner, YouTubeOptions? options = null) =>
        new(
            runner,
            new TestOptionsMonitor<YouTubeOptions>(options ?? new YouTubeOptions()),
            NullLogger<YtDlpRunner>.Instance);

    private static YouTubeOptions OptionsWith(string? cookiesPath, string? poTokenBaseUrl) => new()
    {
        CookiesPath = cookiesPath,
        PoTokenBaseUrl = poTokenBaseUrl,
    };

    [Fact]
    public async Task Downloads_the_opus_remux_and_reads_the_final_path()
    {
        var runner = new FakeProcessRunner().Enqueue(YtDlpFixtures.Read("download-success.stdout"));

        var result = await Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        result.FilePath.Should().Be("/grabs/queue-1/abc123.opus");
        result.Extension.Should().Be("opus");
        result.VideoId.Should().Be("abc123");

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("yt-dlp");
        call.Timeout.Should().Be(TimeSpan.FromSeconds(600));
        call.Arguments.Should().ContainInOrder(
            "-f",
            "bestaudio[acodec=opus]/bestaudio/best",
            "-x",
            "--audio-format",
            "opus",
            "--retries",
            "5",
            "--sleep-requests",
            "0.75",
            "--sleep-interval",
            "10",
            "--max-sleep-interval",
            "20",
            "--print",
            "after_move:filepath",
            "--",
            "https://music.youtube.com/watch?v=abc123");
        call.Arguments.Should().Contain(OutputTemplateArguments);
        call.Arguments.Should().NotContain("--cookies");
        call.Arguments.Should().NotContain("--extractor-args");
    }

    [Fact]
    public async Task Refuses_a_webm_result()
    {
        var runner = new FakeProcessRunner().Enqueue(YtDlpFixtures.Read("download-webm.stdout"));

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.Unknown);
        exception.Which.Message.Should().Contain(".webm");
    }

    [Theory]
    [InlineData("bot-check.stderr", YtDlpErrorKind.BotCheck, YtDlpErrorAction.RetryLater)]
    [InlineData("rate-limited-page.stderr", YtDlpErrorKind.RateLimited, YtDlpErrorAction.RetryLater)]
    [InlineData("rate-limited-429.stderr", YtDlpErrorKind.RateLimited, YtDlpErrorAction.RetryLater)]
    [InlineData("geo-restricted.stderr", YtDlpErrorKind.GeoRestricted, YtDlpErrorAction.Blocklist)]
    [InlineData("age-gated.stderr", YtDlpErrorKind.AgeGated, YtDlpErrorAction.Blocklist)]
    [InlineData("age-gated-login.stderr", YtDlpErrorKind.AgeGated, YtDlpErrorAction.Blocklist)]
    [InlineData("private.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("unavailable.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("not-exist.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("garbage.stderr", YtDlpErrorKind.Unknown, YtDlpErrorAction.RetryLater)]
    public async Task Classifies_every_error_kind(
        string fixture,
        YtDlpErrorKind kind,
        YtDlpErrorAction action)
    {
        var runner = new FakeProcessRunner().Enqueue(1, YtDlpFixtures.Read(fixture));

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(kind);
        exception.Which.Action.Should().Be(action);
        exception.Which.VideoId.Should().Be("abc123");
    }

    [Fact]
    public async Task Classifies_a_missing_binary_as_tool_missing()
    {
        var runner = new FakeProcessRunner().EnqueueMissing("yt-dlp");

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.ToolMissing);
        exception.Which.Action.Should().Be(YtDlpErrorAction.RetryLater);
    }

    [Fact]
    public async Task Classifies_a_timeout()
    {
        var runner = new FakeProcessRunner()
            .Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.TimedOut);
        exception.Which.Action.Should().Be(YtDlpErrorAction.RetryLater);
    }

    [Fact]
    public async Task Rethrows_exit_101_as_cancellation()
    {
        var runner = new FakeProcessRunner().Enqueue(101, "yt-dlp: cancelled");

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reports_a_usage_error_as_unknown_with_the_stderr()
    {
        var runner = new FakeProcessRunner().Enqueue(2, "usage: yt-dlp [options]");

        var act = () => Build(runner).DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.Unknown);
        exception.Which.Message.Should().Contain("usage: yt-dlp [options]");
    }

    [Fact]
    public async Task Serialises_downloads()
    {
        var firstStarted = new TaskCompletionSource();
        var firstFinished = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner()
            .Enqueue(_ =>
            {
                firstStarted.SetResult();

                return firstFinished.Task;
            })
            .Enqueue(YtDlpFixtures.Read("download-success.stdout"));

        var subject = Build(runner);

        var first = subject.DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);
        await firstStarted.Task;

        // The first download holds the gate: the second must not reach the process.
        var second = subject.DownloadAsync("def456", "/grabs/queue-1", CancellationToken.None);
        await Task.Delay(50);
        runner.Calls.Should().HaveCount(1);

        firstFinished.SetResult(new ProcessResult(0, YtDlpFixtures.Read("download-success.stdout"), string.Empty, false));

        var results = await Task.WhenAll(first, second);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Arguments.Should().Contain("https://music.youtube.com/watch?v=abc123");
        runner.Calls[1].Arguments.Should().Contain("https://music.youtube.com/watch?v=def456");
        // Both downloads share the one fixture, so both paths name the fixture's id.
        results[0].FilePath.Should().Contain("abc123.opus");
        results[1].FilePath.Should().Contain("abc123.opus");
    }

    [Fact]
    public async Task Releases_the_gate_when_a_waiting_download_is_cancelled()
    {
        var firstStarted = new TaskCompletionSource();
        var firstFinished = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner()
            .Enqueue(_ =>
            {
                firstStarted.SetResult();

                return firstFinished.Task;
            })
            .Enqueue(YtDlpFixtures.Read("download-success.stdout"));

        var subject = Build(runner);

        var first = subject.DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);
        await firstStarted.Task;

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var act = () => subject.DownloadAsync("def456", "/grabs/queue-1", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        firstFinished.SetResult(new ProcessResult(0, YtDlpFixtures.Read("download-success.stdout"), string.Empty, false));

        (await first).FilePath.Should().Contain("abc123.opus");
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Passes_cookies_and_po_token_when_configured()
    {
        var runner = new FakeProcessRunner().Enqueue(YtDlpFixtures.Read("download-success.stdout"));

        await Build(runner, OptionsWith("/config/cookies.txt", "http://localhost:4416"))
            .DownloadAsync("abc123", "/grabs/queue-1", CancellationToken.None);

        var arguments = runner.Calls.Single().Arguments;
        arguments.Should().Contain(CookiesArguments);
        arguments.Should().Contain(PoTokenArguments);
    }

    [Fact]
    public async Task Probes_formats_and_reports_the_js_runtime()
    {
        var runner = new FakeProcessRunner().Enqueue(
            new ProcessResult(0, YtDlpFixtures.Read("formats-success.stdout"), string.Empty, TimedOut: false));

        var formats = await Build(runner).ProbeFormatsAsync("abc123", CancellationToken.None);

        formats.HasJsRuntime.Should().BeTrue();
        formats.StandardOutput.Should().Contain("251");
        runner.Calls.Single().Arguments.Should().ContainInOrder("-F", "--", "https://music.youtube.com/watch?v=abc123");
    }

    [Fact]
    public async Task Warns_when_the_formats_probe_ran_without_a_js_runtime()
    {
        // Without a JS runtime extraction fails: yt-dlp exits 1 with the deprecation warning on
        // stderr and no format rows on stdout.
        var runner = new FakeProcessRunner().Enqueue(
            new ProcessResult(
                1,
                YtDlpFixtures.Read("formats-no-js.stdout"),
                YtDlpFixtures.Read("formats-no-js.stderr"),
                TimedOut: false));

        var act = () => Build(runner).ProbeFormatsAsync("abc123", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<YtDlpException>();
        exception.Which.Kind.Should().Be(YtDlpErrorKind.BotCheck);
    }
}
