using Xunit;

namespace Wondarr.Testing;

// Compiled into both Wondarr.Core.Tests and Wondarr.Sources.Tests (a linked file, like TrackingTimeProvider),
// so a platform-dependent test reports Skipped in either project instead of returning early and passing.

/// <summary>
/// A fact that only runs on Linux. Elsewhere it is reported as skipped, not as a pass that never
/// tested anything.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="LinuxFactAttribute"/> class.</summary>
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux (case-sensitive paths, a backslash that is part of a file name, inode identity).";
        }
    }
}

/// <summary>
/// A fact that needs Unix file modes (<c>File.GetUnixFileMode</c>, umask) or hard-link identity. Windows has
/// neither, so there the test is reported as skipped rather than passing without checking a mode.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="UnixFactAttribute"/> class.</summary>
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs Unix file modes or hard links, which Windows does not have.";
        }
    }
}

/// <summary>
/// A fact that needs to create symbolic links. Windows only allows that to administrators or with
/// Developer Mode on, so where it cannot the test is reported as skipped rather than passing without
/// ever exercising the link guard.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> CanCreate = new(ProbeSymlinks);

    /// <summary>Initialises a new instance of the <see cref="SymlinkFactAttribute"/> class.</summary>
    public SymlinkFactAttribute()
    {
        if (!CanCreate.Value)
        {
            Skip = "Cannot create symbolic links here (Windows needs administrator rights or Developer Mode).";
        }
    }

    private static bool ProbeSymlinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wondarr-symlink-probe", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateSymbolicLink(Path.Combine(directory, "link"), directory);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }
}
