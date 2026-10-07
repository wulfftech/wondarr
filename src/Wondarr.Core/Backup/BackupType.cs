namespace Wondarr.Core.Backup;

/// <summary>What created a backup: the weekly task, or the user pressing "Back up now".</summary>
public enum BackupType
{
    /// <summary>Created by the scheduled <c>Backup</c> task; deleted once <c>retention_days</c> pass.</summary>
    Scheduled,

    /// <summary>Created through the API; never deleted automatically.</summary>
    Manual,
}
