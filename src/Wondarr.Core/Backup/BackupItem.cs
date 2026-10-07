namespace Wondarr.Core.Backup;

/// <summary>One backup zip: its id (the file name), its type, its size and when it was made.</summary>
/// <param name="Id">The file name, which is also the id the API addresses it by.</param>
/// <param name="Name">The file name, for example <c>wondarr_backup_v1.0.0_2026.01.02_03.04.05.zip</c>.</param>
/// <param name="Type">Whether the scheduled task or the user created it.</param>
/// <param name="Size">The zip's size in bytes.</param>
/// <param name="Time">When it was made: read from the name, falling back to the file's write time.</param>
public sealed record BackupItem(string Id, string Name, BackupType Type, long Size, DateTime Time);

/// <summary>
/// The outcome of staging a restore: either the two files now sit in <c>&lt;ConfigDir&gt;/restore/</c>
/// waiting for the next start, or the archive was rejected and nothing was written.
/// </summary>
/// <param name="Staged">Whether the restore was staged.</param>
/// <param name="Reason">Why the archive was rejected, for the API to turn into a 400.</param>
public sealed record StagedRestoreResult(bool Staged, string? Reason)
{
    /// <summary>The staged outcome.</summary>
    public static StagedRestoreResult Success { get; } = new(true, null);

    /// <summary>Builds the rejected outcome.</summary>
    /// <param name="reason">Why the archive was rejected.</param>
    /// <returns>The result.</returns>
    public static StagedRestoreResult Failed(string reason) => new(false, reason);
}
