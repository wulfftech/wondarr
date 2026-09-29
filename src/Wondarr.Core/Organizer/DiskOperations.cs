namespace Wondarr.Core.Organizer;

/// <summary>
/// Every file-system call the organizer makes. It exists so the tests can drive placements against
/// real temporary directories through one seam, and so the destructive calls sit in one file.
/// </summary>
public interface IDiskOperations
{
    /// <summary>Whether a regular file is at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Whether a directory is at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates <paramref name="path"/> and any missing parent, an existing directory being fine.</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Creates an empty file at <paramref name="path"/>, doing nothing when one is already there.
    /// This is how a Wondarr-owned directory marks itself as its own.
    /// </summary>
    void CreateEmptyFile(string path);

    /// <summary>The size of the file at <paramref name="path"/> in bytes.</summary>
    long GetFileSize(string path);

    /// <summary>Moves a file, and never overwrites: an existing target throws.</summary>
    void MoveFile(string source, string target);

    /// <summary>
    /// Copies a file through a <c>.partial</c> neighbour and renames it into place, so a half-written
    /// file never carries the final name.
    /// </summary>
    void CopyFile(string source, string target);

    /// <summary>
    /// Creates a hard link at <paramref name="target"/> pointing at <paramref name="source"/>.
    /// False whenever the platform or the file system refuses (another device, for instance).
    /// </summary>
    bool TryCreateHardLink(string source, string target);

    /// <summary>Deletes the file at <paramref name="path"/>.</summary>
    void DeleteFile(string path);

    /// <summary>Sets the Unix mode of <paramref name="path"/>; a no-op on Windows.</summary>
    void SetUnixFileMode(string path, UnixFileMode mode);

    /// <summary>Whether two paths are the same file, hard links included.</summary>
    bool AreSameFile(string first, string second);

    /// <summary>Every file under <paramref name="directory"/>, recursively, without following links.</summary>
    IEnumerable<string> EnumerateFiles(string directory);

    /// <summary>When the file at <paramref name="path"/> was last written, in UTC.</summary>
    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>Stamps the file at <paramref name="path"/> as written at <paramref name="utc"/>.</summary>
    void SetLastWriteTimeUtc(string path, DateTime utc);

    /// <summary>Removes <paramref name="path"/> when it is a directory and holds nothing.</summary>
    void DeleteEmptyDirectory(string path);
}

/// <summary>
/// The one implementation, and the only place in Wondarr that touches placed files: it never
/// overwrites an existing target, and it never deletes a file it did not itself put somewhere.
/// </summary>
public sealed class DiskOperations : IDiskOperations
{
    /// <summary>The suffix a copy carries until it is complete.</summary>
    internal const string PartialSuffix = ".partial";

    /// <summary>
    /// How a directory walk behaves: every file below it, and symlinked directories left alone so a
    /// link cannot lead a cleanup out of the tree it was told to look at.
    /// </summary>
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void CreateEmptyFile(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        // CreateNew rather than Create: a marker that appeared between the check and now is left as
        // it is, never truncated.
        using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <inheritdoc />
    public long GetFileSize(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public void MoveFile(string source, string target) => File.Move(source, target, overwrite: false);

    /// <inheritdoc />
    public void CopyFile(string source, string target)
    {
        // A name of its own per attempt, so two placements into one folder never share a
        // half-written file and a second copy never picks up a stranger's leftovers.
        var partial = $"{target}.{Guid.NewGuid():N}{PartialSuffix}";

        try
        {
            File.Copy(source, partial, overwrite: false);
            File.Move(partial, target, overwrite: false);
        }
        catch (IOException)
        {
            RemovePartial(partial);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            RemovePartial(partial);
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryCreateHardLink(string source, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return NativeMethods.CreateHardLinkW(target, source, IntPtr.Zero);
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                return NativeMethods.Link(source, target) == 0;
            }

            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Full paths first, then the device and inode pair on Linux. Elsewhere only the path is
    /// compared: two hard links to one file at different paths read as different files there, which
    /// at worst costs a collision suffix.
    /// </remarks>
    public bool AreSameFile(string first, string second)
    {
        var firstFull = Path.GetFullPath(first);
        var secondFull = Path.GetFullPath(second);

        if (string.Equals(firstFull, secondFull, PathRules.Comparison))
        {
            return true;
        }

        if (!OperatingSystem.IsLinux() || !File.Exists(firstFull) || !File.Exists(secondFull))
        {
            return false;
        }

        try
        {
            return NativeMethods.Stat(firstFull, out var firstStat) == 0
                && NativeMethods.Stat(secondFull, out var secondStat) == 0
                && firstStat.Device == secondStat.Device
                && firstStat.Inode == secondStat.Inode;
        }
        catch (DllNotFoundException)
        {
            // A platform that cannot answer: the path comparison above is the answer.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", Walk);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public void SetLastWriteTimeUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    /// <inheritdoc />
    public void DeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    /// <summary>Drops the half of a failed copy. The copy's own failure is the one that surfaces.</summary>
    private static void RemovePartial(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Path rules that differ between the platforms Wondarr runs on.</summary>
public static class PathRules
{
    /// <summary>
    /// How paths are compared: case-insensitively on Windows and macOS, whose file systems usually
    /// are, and case-sensitively elsewhere.
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> or sits inside it, comparing
    /// full paths and ignoring a trailing separator. A sibling that merely shares a prefix
    /// (<c>/data/music2</c> under the root <c>/data/music</c>) does not count.
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);

        if (string.Equals(normalizedCandidate, normalizedRoot, Comparison))
        {
            return true;
        }

        // A root that is itself a separator ("/", "C:\") already ends with one.
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Sits inside <paramref name="root"/> and is not the root itself. This is what a caller with
    /// something to delete or replace wants: the tree's own root is never the thing being acted on.
    /// </summary>
    public static bool IsStrictlyInside(string root, string candidate) =>
        !AreEqual(root, candidate) && IsInside(root, candidate);

    /// <summary>Whether two paths name the same place, a trailing separator aside.</summary>
    public static bool AreEqual(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), Comparison);

    /// <summary>An absolute path without a trailing separator, so comparisons line up.</summary>
    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}