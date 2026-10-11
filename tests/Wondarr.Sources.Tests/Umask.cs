using System.Runtime.InteropServices;

namespace Wondarr.Sources.Tests;

/// <summary>
/// Sets the process umask for the length of a test, so a file-mode test runs under the same
/// <c>UMASK=002</c> the container does. Linux only: callers return early elsewhere, because Unix
/// modes do not exist on Windows. The umask is process-wide, so the scope is short and restores
/// whatever was there.
/// </summary>
internal sealed class Umask : IDisposable
{
    private readonly uint _previous;

    /// <summary>Initialises a new instance of the <see cref="Umask"/> class.</summary>
    /// <param name="mask">The umask to apply, for example <c>0x2</c> (octal 002).</param>
    public Umask(uint mask) => _previous = umask(mask);

    /// <summary>Puts the earlier umask back.</summary>
    public void Dispose() => _ = umask(_previous);

    // libc.so.6 is the glibc soname the CI runner has; the plain "libc" name is not resolvable everywhere.
#pragma warning disable SYSLIB1054 // A blittable uint needs no marshalling stub.
    [DllImport("libc.so.6", EntryPoint = "umask")]
    private static extern uint umask(uint mask);
#pragma warning restore SYSLIB1054
}
