using System.Runtime.InteropServices;

namespace Wondarr.Core.Organizer;

/// <summary>
/// The two file-system calls the BCL does not expose: hard links, and the device/inode pair that
/// says whether two paths are the same file (a hard link at a second path is not a copy).
/// Every caller treats a failure as "not possible here" and falls back to copying.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Creates a hard link at <paramref name="newLink"/> pointing at <paramref name="existing"/>.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(
        string newLink,
        string existing,
        IntPtr securityAttributes);

    /// <summary>POSIX <c>link(2)</c>: returns zero on success, -1 with <c>errno</c> set otherwise.</summary>
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    internal static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newLink);

    /// <summary>POSIX <c>stat(2)</c>: returns zero on success, -1 with <c>errno</c> set otherwise.</summary>
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    internal static extern int Stat(
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
