using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Wondarr.Sources.Slskd;

/// <inheritdoc />
public sealed class ProcessInspector : IProcessInspector
{
    /// <inheritdoc />
    public string? GetExecutable(int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // argv[0] is what the process was started as; the exe link is the fallback when the
                // command line is empty (a zombie, or hidden from us).
                var commandLine = File.ReadAllBytes($"/proc/{processId}/cmdline");
                var end = Array.IndexOf(commandLine, (byte)0);
                var first = Encoding.UTF8.GetString(commandLine, 0, end < 0 ? commandLine.Length : end);

                return first.Length > 0 ? first : File.ResolveLinkTarget($"/proc/{processId}/exe", false)?.FullName;
            }

            using var process = Process.GetProcessById(processId);

            return process.HasExited ? null : process.MainModule?.FileName ?? process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException or Win32Exception)
        {
            // No such process, or not ours to look at: either way, there is nothing to identify.
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> KillTreeAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone already.
            return true;
        }
    }
}
