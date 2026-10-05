using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.HealthCheck;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>The yt-dlp health probe against a fake process runner: version, degradation and caching.</summary>
public class YtDlpHealthCheckTests
{
    private static (YtDlpAvailability Availability, FakeProcessRunner Runner) Build()
    {
        var runner = new FakeProcessRunner();
        var availability = new YtDlpAvailability(
            runner,
            new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions()),
            NullLogger<YtDlpAvailability>.Instance);

        return (availability, runner);
    }

    [Fact]
    public async Task Reports_the_version_when_yt_dlp_answers()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").Enqueue("deno 2.9.7\n");

        var report = await new YtDlpHealthCheck(availability).CheckAsync(CancellationToken.None);

        report.Source.Should().Be("youtube");
        report.Type.Should().Be(HealthCheckResult.Ok);
        report.Message.Should().Contain("2025.01.15");
    }

    [Fact]
    public async Task Mentions_a_missing_js_runtime_when_deno_does_not_answer()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").EnqueueMissing("deno");

        var report = await new YtDlpHealthCheck(availability).CheckAsync(CancellationToken.None);

        report.Type.Should().Be(HealthCheckResult.Ok);
        report.Message.Should().Contain("JS runtime").And.Contain("Deno");
    }

    [Fact]
    public async Task Reports_degraded_when_yt_dlp_does_not_answer()
    {
        var (availability, runner) = Build();
        runner.EnqueueMissing("yt-dlp").Enqueue("deno 2.9.7\n");

        var report = await new YtDlpHealthCheck(availability).CheckAsync(CancellationToken.None);

        report.Source.Should().Be("youtube");
        report.Type.Should().Be(HealthCheckResult.Warning);
        report.Message.Should().Contain("yt-dlp");
    }

    [Fact]
    public async Task Caches_the_answer_across_checks()
    {
        var (availability, runner) = Build();
        runner.Enqueue("2025.01.15\n").Enqueue("deno 2.9.7\n");

        var check = new YtDlpHealthCheck(availability);

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
}
