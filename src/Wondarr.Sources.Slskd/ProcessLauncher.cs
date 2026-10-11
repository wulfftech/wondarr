using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wondarr.Sources.Slskd;

/// <inheritdoc />
public sealed partial class ProcessLauncher : IProcessLauncher
{
    /// <inheritdoc />
    public ILaunchedProcess Launch(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,

            // No shell: the child is a self-contained binary, and a shell would leave an extra
            // process between the supervisor and slskd that a kill would have to chase through.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            startInfo.Environment[name] = value;
        }

        return new LaunchedProcess(new Process { StartInfo = startInfo });
    }

    private const int SigTerm = 15;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);

    /// <summary>The real <see cref="Process"/>, wrapped so the supervisor never touches it directly.</summary>
    private sealed class LaunchedProcess : ILaunchedProcess
    {
        private readonly Process _process;

        internal LaunchedProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, args) => Raise(args.Data);
            _process.ErrorDataReceived += (_, args) => Raise(args.Data);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public event Action<string>? OutputLine;

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void RequestStop()
        {
            if (OperatingSystem.IsWindows() || _process.HasExited)
            {
                return;
            }

            // The result is ignored: a process that is already gone is the outcome that was asked for.
            _ = SendSignal(_process.Id, SigTerm);
        }

        public void Kill(bool entireProcessTree)
        {
            // Killing a process that has already gone races with its own exit; the supervisor only
            // cares that it is gone, so a redundant kill is simply skipped.
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree);
        }

        public void Dispose() => _process.Dispose();

        private void Raise(string? line)
        {
            if (line is not null)
            {
                OutputLine?.Invoke(line);
            }
        }
    }
}
