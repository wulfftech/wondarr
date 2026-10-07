namespace Wondarr.Core.Organizer;

/// <summary>
/// The file rules the Compact library task and the library mover share: which sidecars travel with an
/// audio file, and when a folder counts as one a move-out emptied. Written fresh for Wondarr.
/// </summary>
internal static class SongFileRules
{
    /// <summary>The sidecar extensions that travel with their audio file.</summary>
    internal static readonly string[] SidecarExtensions = [".lrc", ".txt"];

    /// <summary>What counts as an audio file when deciding whether a folder is one a move emptied.</summary>
    internal static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".alac", ".wma", ".aac"];

    /// <summary>The album folder's art sidecar.</summary>
    internal const string CoverJpgName = "cover.jpg";

    /// <summary>The sidecars that sit beside <paramref name="audioPath"/>, as it is now.</summary>
    /// <param name="disk">The file system.</param>
    /// <param name="audioPath">The audio file whose neighbours are asked for.</param>
    /// <returns>Every existing sidecar path, in extension order.</returns>
    internal static IEnumerable<string> SidecarsOf(IDiskOperations disk, string audioPath)
    {
        var stem = Path.ChangeExtension(audioPath, null);

        if (stem is null)
        {
            yield break;
        }

        foreach (var extension in SidecarExtensions)
        {
            var candidate = string.Concat(stem, extension);

            if (disk.FileExists(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// The sidecar moves that would take <paramref name="audioPath"/>'s neighbours over to
    /// <paramref name="targetPath"/>'s: one per existing sidecar whose own target name is free.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="audioPath">The audio file the sidecars sit beside.</param>
    /// <param name="targetPath">The audio file they would sit beside.</param>
    /// <returns>
    /// (<c>From</c>, <c>To</c>, <c>TargetTaken</c>) triples: a taken target is reported rather than
    /// moved, so the caller can name it in its own log.
    /// </returns>
    internal static IEnumerable<(string From, string To, bool TargetTaken)> SidecarMoves(
        IDiskOperations disk,
        string audioPath,
        string targetPath)
    {
        var fromBase = Path.ChangeExtension(audioPath, null);
        var toBase = Path.ChangeExtension(targetPath, null);

        if (fromBase is null || toBase is null)
        {
            yield break;
        }

        foreach (var extension in SidecarExtensions)
        {
            var from = string.Concat(fromBase, extension);

            if (!disk.FileExists(from))
            {
                continue;
            }

            var to = string.Concat(toBase, extension);

            yield return (from, to, disk.FileExists(to));
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> holds nothing but <c>cover.jpg</c> files — the shape a
    /// move-out leaves behind when it took every audio file with it. Anything else in the folder
    /// (an audio file, another file, a subfolder) makes it <see langword="false"/>.
    /// </summary>
    /// <param name="disk">The file system.</param>
    /// <param name="folder">The folder to inspect.</param>
    /// <param name="covers">The <c>cover.jpg</c> files it holds, empty when it is not covers-only.</param>
    /// <returns>Whether the folder is one a move-out emptied except for its art.</returns>
    internal static bool HoldsOnlyCovers(IDiskOperations disk, string folder, out IReadOnlyList<string> covers)
    {
        covers = [];

        var files = disk.EnumerateFiles(folder).ToList();

        // An audio file the move did not take with it means this is not a folder we emptied.
        if (files.Any(file => HasExtension(file, AudioExtensions)))
        {
            return false;
        }

        var found = files
            .Where(file => string.Equals(Path.GetFileName(file), CoverJpgName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count != found.Count)
        {
            return false;
        }

        covers = found;

        return true;
    }

    /// <summary>Whether <paramref name="path"/> ends with one of <paramref name="extensions"/>.</summary>
    private static bool HasExtension(string path, IReadOnlyList<string> extensions)
    {
        var name = Path.GetFileName(path);

        foreach (var extension in extensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}