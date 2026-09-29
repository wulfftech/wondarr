using System.Runtime.InteropServices;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The two file-system calls the BCL does not expose: hard links, and the device/inode pair that
/// says whether two paths are the same file (a hard link at a second path is not a copy).
/// Every caller treats a failure as "not possible here" and falls back to copying.
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// The C library to call on Linux. Plain <c>libc</c> resolves to a linker script there, which
    /// has no functions in it, so the versioned shared object is asked for by name.
    /// </summary>
    private const string LinuxLibc = "libc.so.6";

    /// <summary>The C library to call on macOS and the BSDs, where <c>libc</c> is a real object.</summary>
    private const string UnixLibc = "libc";

    /// <summary>Creates a hard link at <paramref name="newLink"/> pointing at <paramref name="existing"/>.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(
        string newLink,
        string existing,
        IntPtr securityAttributes);

    /// <summary>POSIX <c>link(2)</c>: zero on success, -1 with <c>errno</c> set otherwise.</summary>
    internal static int Link(string existing, string newLink) =>
        OperatingSystem.IsLinux() ? LinkLinux(existing, newLink) : LinkUnix(existing, newLink);

    /// <summary>POSIX <c>stat(2)</c>: zero on success, -1 with <c>errno</c> set otherwise.</summary>
    internal static int Stat(string path, out StatBuffer buffer)
    {
        if (OperatingSystem.IsLinux())
        {
            return StatLinux(path, out buffer);
        }

        return StatUnix(path, out buffer);
    }

    [DllImport(LinuxLibc, EntryPoint = "link", SetLastError = true)]
    private static extern int LinkLinux(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newLink);

    [DllImport(UnixLibc, EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newLink);

    [DllImport(LinuxLibc, EntryPoint = "stat", SetLastError = true)]
    private static extern int StatLinux(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out StatBuffer buffer);

    [DllImport(UnixLibc, EntryPoint = "stat", SetLastError = true)]
    private static extern int StatUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out StatBuffer buffer);

    /// <summary>
    /// The head of a native <c>struct stat</c>: <c>st_dev</c> then <c>st_ino</c>, which is all we
    /// read. The size leaves room for the whole struct on the platforms .NET runs on, so the native
    /// call never writes past this buffer; the remaining fields are never touched.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    internal struct StatBuffer
    {
        /// <summary>The device the file lives on.</summary>
        internal ulong Device;

        /// <summary>The file's inode, unique on that device.</summary>
        internal ulong Inode;
    }
}
