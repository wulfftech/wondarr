namespace Wondarr.Sources.Slskd;

/// <summary>Everything needed to start the bundled slskd process.</summary>
/// <param name="FileName">Path of the executable to start.</param>
/// <param name="Arguments">Arguments, passed one by one so nothing needs quoting or escaping.</param>
/// <param name="WorkingDirectory">Directory the process runs in — the binary's own directory.</param>
/// <param name="Environment">Extra environment variables, merged over the current environment.</param>
public sealed record ProcessLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A process that has been started and can be observed and killed.</summary>
public interface ILaunchedProcess : IDisposable
{
    /// <summary>Operating-system process id.</summary>
    int Id { get; }

    /// <summary>Whether the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>The exit code, or <see langword="null"/> while the process is still running.</summary>
    int? ExitCode { get; }

    /// <summary>Completes when the process exits.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Raised for every line the process writes to standard output or standard error. Redaction
    /// happens in the logging pipeline, so the handler must not log the line itself.
    /// </summary>
    event Action<string>? OutputLine;

    /// <summary>Kills the process, optionally together with the children it started.</summary>
    void Kill(bool entireProcessTree);
}

/// <summary>
/// Starts processes. The seam the supervisor is tested through: tests supply a fake launcher and
/// never start a real slskd.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>Starts the process described by <paramref name="request"/>.</summary>
    ILaunchedProcess Launch(ProcessLaunchRequest request);
}
