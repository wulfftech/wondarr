namespace Compilarr.Sources.Slskd;

/// <summary>
/// Decides whether a change to the Soulseek settings needs the bundled slskd restarted.
/// </summary>
/// <remarks>
/// slskd watches its YAML file and reloads it live: upload speed limits and the listen port apply
/// to a running process. Everything else — credentials, shared folders, upload slots, the
/// distributed network, directories and the loopback web port — is read once at start, so a change
/// there needs a restart (<c>docs/architecture/DEPLOYMENT.md</c> §9.5).
/// </remarks>
public static class SlskdRestartPolicy
{
    /// <summary>
    /// Returns <see langword="true"/> when moving from <paramref name="before"/> to
    /// <paramref name="after"/> needs slskd restarted.
    /// </summary>
    public static bool RequiresRestart(SoulseekOptions before, SoulseekOptions after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return !string.Equals(before.Username, after.Username, StringComparison.Ordinal)
            || !string.Equals(before.Password, after.Password, StringComparison.Ordinal)
            || before.ShareLibrary != after.ShareLibrary
            || !SharesTheSameFolders(before.SharedFolders, after.SharedFolders)
            || before.UploadSlots != after.UploadSlots
            || before.DistributedNetwork != after.DistributedNetwork
            || !string.Equals(before.DownloadsDir, after.DownloadsDir, StringComparison.Ordinal)
            || !string.Equals(before.IncompleteDir, after.IncompleteDir, StringComparison.Ordinal)
            || before.WebPort != after.WebPort
            || !string.Equals(before.BinaryPath, after.BinaryPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares shared folders as a set: slskd shares the same folders regardless of the order they
    /// are listed in, so re-ordering the list leaves nothing in the running process stale.
    /// </summary>
    private static bool SharesTheSameFolders(List<string> before, List<string> after) =>
        before.Count == after.Count && before.ToHashSet(StringComparer.Ordinal).SetEquals(after);
}
