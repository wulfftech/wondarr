using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.HealthCheck;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>The yt-dlp health probe against a fake process runner: version, degradation and caching.</summary>
public class YtDlpHealthCheckTests
{
    private static (YtDlpAvailability Availability, FakeProcessRunner Runner) Build(IYtDlpBinary? binary = null)
    {
        var runner = new FakeProcessRunner();
        var availability = new YtDlpAvailability(
            runner,
            binary ?? new FixedYtDlpBinary(),
            new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions()),
            NullLogger<YtDlpAvailability>.Instance);

        return (availability, runner);
    }

    private static YtDlpHealthCheck Check(
        YtDlpAvailability availability,
        TestOptionsMonitor<YouTubeOptions> options,
        IYtDlpBinary? binary = null,
        TimeProvider? time = null) =>
        new(availability, binary ?? new FixedYtDlpBinary(), options, time ?? TimeProvider.System);

    private static readonly TestOptionsMonitor<YouTubeOptions> Enabled = new(new YouTubeOptions { Enabled = true });

    [Fact]
    public async Task Is_quiet_when_the_source_is_off_even_without_yt_dlp()
    {
        var (availability, runner) = Build();
        runner.EnqueueMissing("yt-dlp").Enqueue("deno 2.9.7\n");

        var report = await Check(availability, new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions()))
            .CheckAsync(CancellationToken.None);

        report.Type.Should().Be(HealthCheckResult.Ok);
        report.Message.Should().Contain("off");
    }

    [Fact]
    public async Task Reports_the_version_when_yt_dlp_answers()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").Enqueue("deno 2.9.7\n");

        var report = await Check(availability, Enabled).CheckAsync(CancellationToken.None);

        report.Source.Should().Be("youtube");
        report.Type.Should().Be(HealthCheckResult.Ok);
        report.Message.Should().Contain("2025.01.15");
    }

    [Fact]
    public async Task Mentions_a_missing_js_runtime_when_deno_does_not_answer()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").EnqueueMissing("deno");

        var report = await Check(availability, Enabled).CheckAsync(CancellationToken.None);

        report.Type.Should().Be(HealthCheckResult.Ok);
        report.Message.Should().Contain("JS runtime").And.Contain("Deno");
    }

    [Fact]
    public async Task Reports_degraded_when_yt_dlp_does_not_answer()
    {
        var (availability, runner) = Build();
        runner.EnqueueMissing("yt-dlp").Enqueue("deno 2.9.7\n");

        var report = await Check(availability, Enabled).CheckAsync(CancellationToken.None);

        report.Source.Should().Be("youtube");
        report.Type.Should().Be(HealthCheckResult.Warning);
        report.Message.Should().Contain("yt-dlp");
    }

    [Fact]
    public async Task Caches_the_answer_across_checks()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").Enqueue("deno 2.9.7\n");

        var check = Check(availability, Enabled);

        await check.CheckAsync(CancellationToken.None);
        await check.CheckAsync(CancellationToken.None);

        // Two probes per first check (yt-dlp and deno), none for the second.
        runner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Exposes_the_js_runtime_on_the_status()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").EnqueueMissing("deno");

        var status = await availability.GetStatusAsync(CancellationToken.None);

        status.Version.Should().Be("2025.01.15");
        status.BinaryAvailable.Should().BeTrue();
        status.HasJsRuntime.Should().BeFalse();
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    private static YtDlpUpdateStatus UpdateStatus(
        DateTimeOffset? lastSuccess,
        string? latest,
        bool active = true,
        string? jsWarning = null) =>
        new(
            YtDlpBinarySource.Managed,
            null,
            AutoUpdate: true,
            YtDlpOptions.StableChannel,
            UpdateActive: active,
            LastCheckedAt: lastSuccess,
            LastCheckSucceeded: lastSuccess is not null,
            LastCheckMessage: null,
            LastSuccessAt: lastSuccess,
            LatestVersion: latest,
            StartedAt: Now.AddHours(-1),
            JsRuntimeWarning: jsWarning,
            FallbackReason: null);

    private static async Task<Wondarr.Core.HealthCheck.HealthCheck> RunStaleCheckAsync(string inUse, YtDlpUpdateStatus update)
    {
        var binary = new FixedYtDlpBinary(status: update);
        var (availability, runner) = Build(binary);
        runner.Enqueue(inUse + "\n").Enqueue("deno 2.9.7\n");

        return await Check(availability, Enabled, binary, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now))
            .CheckAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warns_when_the_yt_dlp_in_use_is_more_than_60_days_older_than_the_newest_release()
    {
        // 2026.08.19 to 2026.10.26 is 68 days.
        var report = await RunStaleCheckAsync("2026.08.19", UpdateStatus(Now.AddHours(-2), "2026.10.26"));

        report.Type.Should().Be(HealthCheckResult.Warning);
        report.Message.Should().Contain("2026.08.19").And.Contain("68 days").And.Contain("2026.10.26");
    }

    [Fact]
    public async Task Stays_quiet_at_exactly_60_days_behind()
    {
        // 2026.08.27 to 2026.10.26 is 60 days.
        var report = await RunStaleCheckAsync("2026.08.27", UpdateStatus(Now.AddHours(-2), "2026.10.26"));

        report.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Warns_about_a_missing_check_instead_when_none_succeeded_for_a_week()
    {
        var report = await RunStaleCheckAsync("2026.08.19", UpdateStatus(Now.AddDays(-8), "2026.10.26"));

        report.Type.Should().Be(HealthCheckResult.Warning);
        report.Message.Should().Contain("not been checked").And.Contain("8 days");
    }

    [Fact]
    public async Task Does_not_judge_the_age_when_the_update_is_not_active()
    {
        var report = await RunStaleCheckAsync("2026.08.19", UpdateStatus(Now.AddDays(-30), "2026.10.26", active: false));

        report.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Warns_when_yt_dlp_says_the_js_runtime_is_too_old()
    {
        var report = await RunStaleCheckAsync(
            "2026.10.26",
            UpdateStatus(Now.AddHours(-2), "2026.10.26", jsWarning: "WARNING: Deno 2.0 is unsupported"));

        report.Type.Should().Be(HealthCheckResult.Warning);
        report.Message.Should().Contain("Deno 2.0 is unsupported");
    }
}
