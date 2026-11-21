using FluentAssertions;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// The strings of yt-dlp's updater (yt_dlp/update.py at tag 2026.10.26), the version arithmetic, and the scheduled
/// task that runs the update.
/// </summary>
public class YtDlpUpdateOutputTests
{
    private static ProcessResult Ok(string stdout, string stderr = "") => new(0, stdout, stderr, TimedOut: false);

    [Fact]
    public void Reads_updated_to_as_an_update_with_the_new_version()
    {
        var outcome = YtDlpUpdateOutput.Parse(Ok(
            "Current version: stable@2026.08.19 from yt-dlp/yt-dlp\n"
            + "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\n"
            + "Current Build Hash: 8f2a\n"
            + "Updating to stable@2026.10.26 from yt-dlp/yt-dlp ...\n"
            + "Updated yt-dlp to stable@2026.10.26 from yt-dlp/yt-dlp\n"));

        outcome.Kind.Should().Be(YtDlpUpdateKind.Updated);
        outcome.Version.Should().Be("2026.10.26");
        outcome.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public void Reads_up_to_date_with_the_newest_release()
    {
        var outcome = YtDlpUpdateOutput.Parse(Ok(
            "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\nyt-dlp is up to date (stable@2026.10.26 from yt-dlp/yt-dlp)\r\n"));

        outcome.Kind.Should().Be(YtDlpUpdateKind.UpToDate);
        outcome.Version.Should().Be("2026.10.26");
        outcome.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public void A_build_newer_than_the_stable_channel_is_still_up_to_date_and_the_newest_release_is_the_channels()
    {
        var outcome = YtDlpUpdateOutput.Parse(Ok(
            "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\nyt-dlp is up to date (nightly@2026.10.27.232859 from yt-dlp/yt-dlp-nightly-builds)\n"));

        outcome.Kind.Should().Be(YtDlpUpdateKind.UpToDate);
        outcome.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public void Reads_an_error_line_from_stderr_as_a_failure_with_the_updaters_reason()
    {
        var outcome = YtDlpUpdateOutput.Parse(new ProcessResult(
            100,
            string.Empty,
            "ERROR: Unable to obtain version info (HTTP Error 403: rate limit exceeded); Please try again later or visit  https://github.com/yt-dlp/yt-dlp/releases/latest\n",
            TimedOut: false));

        outcome.Kind.Should().Be(YtDlpUpdateKind.Failed);
        outcome.Message.Should().StartWith("Unable to obtain version info").And.Contain("rate limit");
    }

    [Fact]
    public void Reads_a_permission_error_as_a_failure()
    {
        var outcome = YtDlpUpdateOutput.Parse(new ProcessResult(
            100,
            "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\nCurrent Build Hash: 8f2a\nUpdating to stable@2026.10.26 from yt-dlp/yt-dlp ...\n",
            "ERROR: Insufficient permissions to write to /config/tools/yt-dlp/yt-dlp\n",
            TimedOut: false));

        outcome.Kind.Should().Be(YtDlpUpdateKind.Failed);
        outcome.Message.Should().Contain("Insufficient permissions");
        outcome.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public void A_nonzero_exit_without_an_error_line_is_a_failure_that_reports_the_last_stderr_line()
    {
        var outcome = YtDlpUpdateOutput.Parse(new ProcessResult(
            1,
            string.Empty,
            "Traceback (most recent call last):\n  File \"update.py\", line 1, in <module>\nValueError: boom\n",
            TimedOut: false));

        outcome.Kind.Should().Be(YtDlpUpdateKind.Failed);
        outcome.Message.Should().Be("ValueError: boom");
    }

    [Fact]
    public void A_timeout_is_a_failure()
    {
        YtDlpUpdateOutput.Parse(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true)).Kind
            .Should().Be(YtDlpUpdateKind.Failed);
    }

    [Fact]
    public void Exit_zero_with_nothing_recognised_is_up_to_date()
    {
        // The _update_spec lock rules (update.py query_update) return without printing anything.
        YtDlpUpdateOutput.Parse(Ok(string.Empty)).Kind.Should().Be(YtDlpUpdateKind.UpToDate);
    }

    [Theory]
    [InlineData("WARNING: [youtube] Deno 2.0.0 is unsupported; please upgrade", true)]
    [InlineData("WARNING: [youtube] JS runtime is too old for the EJS solver", true)]
    [InlineData("WARNING: [youtube] Deno is installed", false)]
    [InlineData("WARNING: Some other thing is unsupported", false)]
    [InlineData("", false)]
    public void Finds_a_js_runtime_complaint(string text, bool expected)
    {
        (YtDlpUpdateOutput.FindJsRuntimeProblem(text) is not null).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026.08.19", "2026.10.26", -1)]
    [InlineData("2026.10.26", "2026.10.26", 0)]
    [InlineData("2026.10.26.1", "2026.10.26", 1)]
    [InlineData("2026.10.27.232859", "2026.10.26", 1)]
    [InlineData("2026.9.30", "2026.10.1", -1)]
    public void Compares_versions_as_numbers_part_by_part(string left, string right, int expected)
    {
        Math.Sign(YtDlpVersion.Compare(left, right)!.Value).Should().Be(expected);
    }

    [Fact]
    public void A_string_that_is_not_a_version_compares_to_nothing()
    {
        YtDlpVersion.Compare("yt-dlp", "2026.10.26").Should().BeNull();
        YtDlpVersion.Compare(null, "2026.10.26").Should().BeNull();
        YtDlpVersion.DateOf("2026.13.40").Should().BeNull();
        YtDlpVersion.DateOf("2026.10.26.2").Should().Be(new DateOnly(2026, 10, 26));
    }

    [Fact]
    public void The_update_is_a_daily_scheduled_task_that_first_runs_a_few_minutes_after_startup()
    {
        var task = new YtDlpScheduledTasks().Tasks.Should().ContainSingle().Subject;

        task.CommandName.Should().Be("UpdateYtDlp");
        task.Interval.Should().Be(TimeSpan.FromHours(24));
        task.FirstRunDelay.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task The_task_reports_what_the_update_did()
    {
        var handler = new UpdateYtDlpCommandHandler(new FixedYtDlpBinary());

        handler.Name.Should().Be("UpdateYtDlp");
        (await handler.ExecuteAsync(new CommandContext(1, null, CommandTrigger.Manual, _ => Task.CompletedTask), CancellationToken.None)).Should().Be("fixed");
    }

    [Fact]
    public void An_unknown_update_channel_is_refused_with_the_yaml_key_first()
    {
        var result = new YtDlpOptionsValidator().Validate(null, new YouTubeOptions { Ytdlp = new YtDlpOptions { UpdateChannel = "beta" } });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("youtube.ytdlp.update_channel:");
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("nightly")]
    public void The_two_known_channels_are_accepted(string channel)
    {
        new YtDlpOptionsValidator().Validate(null, new YouTubeOptions { Ytdlp = new YtDlpOptions { UpdateChannel = channel } }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Auto_update_is_on_and_stable_by_default()
    {
        var options = new YtDlpOptions();

        options.AutoUpdate.Should().BeTrue();
        options.UpdateChannel.Should().Be("stable");
        options.HasCustomBinaryPath.Should().BeFalse();
        new YtDlpOptions { BinaryPath = "/usr/local/bin/yt-dlp" }.HasCustomBinaryPath.Should().BeTrue();
    }
}
