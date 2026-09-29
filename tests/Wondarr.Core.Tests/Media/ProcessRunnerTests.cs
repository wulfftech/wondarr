using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Media;
using Xunit;

namespace Wondarr.Core.Tests.Media;

/// <summary>
/// Exercises <see cref="ProcessRunner"/> against real processes: a trivial success, a run that has to
/// be killed, a cancelled wait, and an executable that is not there.
/// </summary>
public sealed class ProcessRunnerTests
{
    /// <summary>A command every machine has, which runs long enough to be killed.</summary>
    private static readonly (string FileName, string[] Arguments) SlowCommand = OperatingSystem.IsWindows()
        ? ("ping", ["-n", "30", "127.0.0.1"])
        : ("sleep", ["30"]);

    [Fact]
    public async Task Runs_a_process_and_reads_its_output()
    {
        var result = await Runner().RunAsync(
            "dotnet",
            ["--version"],
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        result.StandardOutput.Trim().Should().NotBeNullOrEmpty();
        result.StandardError.Should().BeEmpty();
    }

    [Fact]
    public async Task Kills_a_process_that_outruns_its_timeout()
    {
        var result = await Runner().RunAsync(
            SlowCommand.FileName,
            SlowCommand.Arguments,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task Throws_when_the_wait_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => Runner().RunAsync(
            SlowCommand.FileName,
            SlowCommand.Arguments,
            TimeSpan.FromSeconds(60),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Throws_when_the_executable_is_missing()
    {
        var act = () => Runner().RunAsync(
            "wondarr-no-such-media-tool-9f3a",
            ["-version"],
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<MediaToolMissingException>();
        exception.Which.Tool.Should().Be("wondarr-no-such-media-tool-9f3a");
        exception.Which.Message.Should().Contain("wondarr-no-such-media-tool-9f3a");
    }

    private static ProcessRunner Runner() => new(NullLogger<ProcessRunner>.Instance);
}
