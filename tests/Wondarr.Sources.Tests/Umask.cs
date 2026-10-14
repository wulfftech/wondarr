using System.Runtime.InteropServices;

namespace Wondarr.Sources.Tests;

/// <summary>
/// Sets the process umask for the length of a test, so a file-mode test runs under the same
/// <c>UMASK=002</c> the container does. Linux only: callers use [UnixFact], because Unix
/// modes do not exist on Windows. The umask is process-wide, so the scope is short and restores
/// whatever was there.
/// </summary>
internal sealed class Umask : IDisposable
{
    // The umask is process-wide, so two tests that set it at once would see each other's mask. A semaphore
    // rather than a monitor: the scope often spans an await, and may be left on another thread.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly uint _previous;

    /// <summary>Initialises a new instance of the <see cref="Umask"/> class.</summary>
    /// <param name="mask">The umask to apply, for example <c>0x2</c> (octal 002).</param>
    public Umask(uint mask)
    {
        Gate.Wait();
        _previous = umask(mask);
    }

    /// <summary>Puts the earlier umask back.</summary>
    public void Dispose()
    {
        _ = umask(_previous);
        Gate.Release();
    }

    // "libc" resolves on glibc and on musl (Alpine) alike.
#pragma warning disable SYSLIB1054 // A blittable uint needs no marshalling stub.
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint umask(uint mask);
#pragma warning restore SYSLIB1054
}
