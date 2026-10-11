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

    // "libc" alone resolves to the linker script libc.so, which only exists with libc6-dev installed;
    // the runtime library is libc.so.6. Where it cannot be loaded, RequestStop falls back to kill(1).
    [LibraryImport("libc.so.6", EntryPoint = "kill", SetLastError = true)]
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

            try
            {
                // The result is ignored: a process that is already gone is the outcome that was asked for.
                _ = SendSignal(_process.Id, SigTerm);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                // No libc to call into: ask the kill utility instead. If that is missing too, no signal
                // was sent, and the caller's tree kill is what stops slskd.
                SignalWithKillUtility(_process.Id);
            }
        }

        private static void SignalWithKillUtility(int processId)
        {
            try
            {
                using var kill = Process.Start(new ProcessStartInfo("kill")
                {
                    ArgumentList = { "-TERM", processId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                kill?.WaitForExit(1000);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No signal sent.
            }
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
