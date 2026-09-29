using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Media;

/// <summary>Runs one external process to completion and returns what it wrote.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for it to exit.
    /// </summary>
    /// <param name="fileName">The executable, resolved on <c>PATH</c> when it is a bare name.</param>
    /// <param name="arguments">One argument per item — never a joined command line (file names carry spaces, quotes and ellipses).</param>
    /// <param name="timeout">How long the process may run before it is killed.</param>
    /// <param name="cancellationToken">Cancels the wait; the process tree is killed and the exception propagates.</param>
    /// <returns>What the process wrote and how it exited.</returns>
    /// <exception cref="MediaToolMissingException">The executable could not be started because it is not there.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>The outcome of one external process.</summary>
/// <param name="ExitCode">The exit code, or <c>-1</c> when the process was killed on timeout.</param>
/// <param name="StandardOutput">Everything the process wrote to stdout.</param>
/// <param name="StandardError">Everything the process wrote to stderr.</param>
/// <param name="TimedOut">Whether the process was killed because <c>timeout</c> elapsed.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>An external tool Wondarr needs (ffprobe, ffmpeg, fpcalc) is not installed or not on the path.</summary>
public sealed class MediaToolMissingException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="MediaToolMissingException"/> class.</summary>
    /// <param name="tool">The executable that could not be started.</param>
    /// <param name="innerException">The underlying <see cref="Win32Exception"/>.</param>
    public MediaToolMissingException(string tool, Exception? innerException = null)
        : base(
            $"'{tool}' could not be started: it is not installed or not on PATH. "
            + "Install it or point the media section of config.yml at it.",
            innerException)
    {
        Tool = tool;
    }

    /// <summary>Gets the executable that could not be started.</summary>
    public string Tool { get; }
}

/// <summary>
/// Runs the bundled <c>ffprobe</c>, <c>ffmpeg</c> and <c>fpcalc</c> binaries. Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so a file name is never re-parsed by a shell, and both
/// pipes are drained while the process runs so a talkative tool cannot deadlock on a full pipe.
/// </summary>
public sealed partial class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    /// <summary>Initialises a new instance of the <see cref="ProcessRunner"/> class.</summary>
    /// <param name="logger">Logger for the timeout warnings; stdout is never logged above Debug.</param>
    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new MediaToolMissingException(fileName, exception);
        }

        // Started before the wait: a tool that fills one pipe while nobody reads it blocks forever.
        // The pipes are drained even when the run is cancelled, so they take no token of their own.
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            var killedOutput = await DrainAsync(standardOutput).ConfigureAwait(false);
            var killedError = await DrainAsync(standardError).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            LogTimedOut(_logger, fileName, timeout);

            return new ProcessResult(-1, killedOutput, killedError, TimedOut: true);
        }

        var output = await DrainAsync(standardOutput).ConfigureAwait(false);
        var error = await DrainAsync(standardError).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            LogExited(_logger, fileName, process.ExitCode, error);
        }

        return new ProcessResult(process.ExitCode, output, error, TimedOut: false);
    }

    // Debug on purpose: a tool's stderr can be long, and Information is for the import decision.
    [LoggerMessage(Level = LogLevel.Debug, Message = "'{Tool}' exited {ExitCode}. Stderr: {StandardError}")]
    private static partial void LogExited(ILogger logger, string tool, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Tool}' was killed after {Timeout}.")]
    private static partial void LogTimedOut(ILogger logger, string tool, TimeSpan timeout);

    /// <summary>Kills the process and everything it started, tolerating a process that already exited.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout and the kill.
        }
        catch (Win32Exception)
        {
            // The process exited between the timeout and the kill.
        }
    }

    /// <summary>Waits for a pipe to close, treating a stream disposed by the kill as empty.</summary>
    private static async Task<string> DrainAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
