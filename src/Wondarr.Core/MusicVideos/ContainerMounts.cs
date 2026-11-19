using System.Text.RegularExpressions;

namespace Wondarr.Core.MusicVideos;

/// <summary>
/// Tells whether a folder sits on the container's own root filesystem, where it would be lost when the container
/// is recreated. It reads <c>/proc/self/mountinfo</c>: the longest mount point that is the root or an ancestor of
/// the folder says what the folder lives on, and a mount point of <c>/</c> means nothing of its own is mounted there.
/// The rule applies only on Linux inside a container (<c>/.dockerenv</c> or <c>/run/.containerenv</c> exists).
/// </summary>
public sealed partial class ContainerMounts
{
    private static readonly string[] KernelMounts = ["/proc", "/sys", "/dev"];

    private static readonly string[] MarkerFiles = ["/.dockerenv", "/run/.containerenv"];

    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<string>?> _readMountInfo;
    private readonly bool _isLinux;
    private readonly Func<string, bool> _directoryExists;

    /// <summary>Initialises a new instance of the <see cref="ContainerMounts"/> class.</summary>
    /// <param name="fileExists">Whether a marker file exists.</param>
    /// <param name="readMountInfo">The lines of <c>/proc/self/mountinfo</c>, or <see langword="null"/> when unreadable.</param>
    /// <param name="isLinux">Whether the process runs on Linux.</param>
    /// <param name="directoryExists">Whether a mount point is a directory (a file bind mount is not a place to browse); every mount point counts when omitted.</param>
    public ContainerMounts(
        Func<string, bool> fileExists,
        Func<IReadOnlyList<string>?> readMountInfo,
        bool isLinux = true,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readMountInfo);

        _fileExists = fileExists;
        _readMountInfo = readMountInfo;
        _isLinux = isLinux;
        _directoryExists = directoryExists ?? (_ => true);
    }

    /// <summary>The detector for this process, reading the real files.</summary>
    public static ContainerMounts ForThisProcess() => new(File.Exists, ReadSystemMountInfo, OperatingSystem.IsLinux(), Directory.Exists);

    /// <summary>Whether the process runs in a container.</summary>
    public bool InContainer => _isLinux && MarkerFiles.Any(_fileExists);

    /// <summary>
    /// Whether <paramref name="path"/> is on the container's root filesystem. <see langword="false"/> outside a
    /// container, on Windows, and when the mount table cannot be read (the rule then stays out of the way).
    /// </summary>
    /// <param name="path">The folder.</param>
    public bool IsOnContainerRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!InContainer)
        {
            return false;
        }

        var lines = _readMountInfo();

        if (lines is null)
        {
            return false;
        }

        var target = Normalise(path);
        string? best = null;

        foreach (var line in lines)
        {
            var mountPoint = ParseMountPoint(line);

            if (mountPoint is null)
            {
                continue;
            }

            mountPoint = Normalise(mountPoint);

            if (IsSameOrAncestor(mountPoint, target) && (best is null || mountPoint.Length > best.Length))
            {
                best = mountPoint;
            }
        }

        return best == "/";
    }

    /// <summary>
    /// The mount points of real data mounts: every directory mounted in the container except <c>/</c>, the kernel's
    /// <c>/proc</c>, <c>/sys</c> and <c>/dev</c> (and what is under them), and the files Docker binds in
    /// (<c>/etc/hosts</c>, <c>/etc/hostname</c>, <c>/etc/resolv.conf</c>). Empty outside a container.
    /// Asks the disk about each mount point, so a caller bounds the call.
    /// </summary>
    public IReadOnlyList<string> DataMounts()
    {
        if (!InContainer)
        {
            return [];
        }

        var mounts = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in _readMountInfo() ?? [])
        {
            var mountPoint = ParseMountPoint(line);

            if (mountPoint is null)
            {
                continue;
            }

            mountPoint = Normalise(mountPoint);

            if (mountPoint != "/" && !IsKernelOrDockerMount(mountPoint) && _directoryExists(mountPoint))
            {
                mounts.Add(mountPoint);
            }
        }

        return [.. mounts];
    }

    /// <summary>The refusal message for a folder on the container's root filesystem, or <see langword="null"/> when it is fine.</summary>
    /// <param name="path">The folder.</param>
    public string? Problem(string path) =>
        IsOnContainerRoot(path)
            ? $"rootPath {path} is inside the container, so its videos would be lost when the container is recreated. "
                + "Mount a folder there (for example a share at /data/music-videos), or pick one under a mounted folder such as /data/music."
            : null;

    private static string[]? ReadSystemMountInfo()
    {
        try
        {
            return File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsKernelOrDockerMount(string mountPoint) =>
        KernelMounts.Any(prefix => IsSameOrAncestor(prefix, mountPoint))
        || mountPoint is "/etc/hosts" or "/etc/hostname" or "/etc/resolv.conf";

    /// <summary>Resolves <c>.</c>, <c>..</c> and repeated separators lexically, always rooted at <c>/</c>.</summary>
    internal static string Normalise(string path)
    {
        var text = path.Replace('\\', '/');
        var parts = new List<string>();

        foreach (var part in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    private static bool IsSameOrAncestor(string mountPoint, string target) =>
        mountPoint == "/"
        || string.Equals(mountPoint, target, StringComparison.Ordinal)
        || target.StartsWith(mountPoint + "/", StringComparison.Ordinal);

    /// <summary>The mount point (fifth field) of a <c>mountinfo</c> line, with its octal escapes decoded.</summary>
    private static string? ParseMountPoint(string line)
    {
        var fields = line.Split(' ', 6, StringSplitOptions.None);

        return fields.Length < 5 || fields[4].Length == 0 ? null : Unescape(fields[4]);
    }

    /// <summary>Decodes <c>\040</c>-style escapes (the kernel writes a space, tab, newline and backslash that way).</summary>
    private static string Unescape(string text) =>
        OctalEscape().Replace(text, match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
