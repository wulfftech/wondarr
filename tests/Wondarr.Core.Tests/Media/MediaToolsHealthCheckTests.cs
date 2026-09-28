using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>Checks the health check's two answers, and that the versions are only read once.</summary>
public sealed class MediaToolsHealthCheckTests
{
    private static readonly string[] VersionArguments = ["-version"];
    [Fact]
    public async Task Reports_ok_with_the_fpcalc_version_when_all_three_answer()
    {
        var runner = Runner()
            .Enqueue("ffprobe version 9.0.2 Copyright (c) 2007-2026 the FFmpeg developers\n")
            .Enqueue("ffmpeg version 9.0.2 Copyright (c) 2007-2026 the FFmpeg developers\n")
            .Enqueue("fpcalc version 1.6.1\n");

        var result = await Check(runner).CheckAsync(CancellationToken.None);

        result.Source.Should().Be(nameof(MediaToolsHealthCheck));
        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("ffprobe, ffmpeg and fpcalc available (fpcalc 1.6.1)");

        runner.Calls.Should().HaveCount(3);
        runner.Calls.Should().OnlyContain(call => call.Arguments.SequenceEqual(VersionArguments));
    }

    [Fact]
    public async Task Warns_about_the_tools_that_are_missing()
    {
        var runner = Runner()
            .Enqueue("ffprobe version 9.0.2\n")
            .Enqueue("ffmpeg version 9.0.2\n")
            .EnqueueMissing("fpcalc");

        var result = await Check(runner).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be("Media tools missing: fpcalc — downloads cannot be verified");
    }

    [Fact]
    public async Task Warns_about_every_missing_tool()
    {
        var runner = Runner()
            .EnqueueMissing("ffprobe")
            .EnqueueMissing("ffmpeg")
            .EnqueueMissing("fpcalc");

        var result = await Check(runner).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be("Media tools missing: ffprobe, ffmpeg, fpcalc — downloads cannot be verified");
    }

    [Fact]
    public async Task Runs_the_tools_once_per_process()
    {
        var runner = Runner()
            .Enqueue("ffprobe version 9.0.2\n")
            .Enqueue("ffmpeg version 9.0.2\n")
            .Enqueue("fpcalc version 1.6.1\n")
            .Enqueue("ffprobe version 9.0.2\n");

        var availability = new MediaToolAvailability(
            runner,
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<MediaToolAvailability>.Instance);

        var check = new MediaToolsHealthCheck(availability);

        await check.CheckAsync(CancellationToken.None);
        var second = await check.CheckAsync(CancellationToken.None);

        second.Type.Should().Be(HealthCheckResult.Ok);
        runner.Calls.Should().HaveCount(3);
    }

    private static FakeProcessRunner Runner() => new();

    private static MediaToolsHealthCheck Check(FakeProcessRunner runner) =>
        new(new MediaToolAvailability(
            runner,
            new TestOptionsMonitor<MediaToolsOptions>(new MediaToolsOptions()),
            NullLogger<MediaToolAvailability>.Instance));
}