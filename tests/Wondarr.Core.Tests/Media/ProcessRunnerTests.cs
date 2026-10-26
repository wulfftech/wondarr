using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    public async Task Gives_up_on_a_pipe_a_leftover_grandchild_keeps_open()
    {
        // The shell exits at once; the backgrounded grandchild outlives it and keeps the inherited
        // stdout pipe open, so reading to the end of the pipe would take as long as the grandchild runs.
        // The grandchild is a real process that nothing here kills: it runs 10 s (long enough to be
        // clearly later than the 300 ms drain bound below, short enough not to linger after the run)
        // and then exits on its own.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "start /b ping -n 10 127.0.0.1" })
            : ("sh", new[] { "-c", "sleep 10 &" });

        var logger = new RecordingLogger<ProcessRunner>();
        var runner = new ProcessRunner(logger) { DrainTimeout = TimeSpan.FromMilliseconds(300) };

        var result = await runner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(60), CancellationToken.None);

        // The runner's own word that it gave up on the pipe, not the wall clock: how long the shell takes to
        // start on a loaded machine says nothing about the drain. It also shows the pipe was really held.
        logger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("left a pipe open", StringComparison.Ordinal),
            "the drain is bounded, not as long as the grandchild");
        result.TimedOut.Should().BeFalse();
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
