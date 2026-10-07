namespace Wondarr.Core.Backup;

/// <summary>
/// Creates, lists, deletes and restores Wondarr's backups: a zip holding a consistent copy of the
/// database (made with SQLite's online backup API, never by copying the live file) and
/// <c>config.yml</c>.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Creates a backup of the database and <c>config.yml</c> under the type's folder.
    /// </summary>
    /// <param name="type">Whether this is a scheduled or a manual backup.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>The new backup's descriptor.</returns>
    Task<BackupItem> CreateAsync(BackupType type, CancellationToken cancellationToken);

    /// <summary>Lists every backup, newest first.</summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>Every <c>.zip</c> under <c>scheduled/</c> and <c>manual/</c>, newest first.</returns>
    Task<IReadOnlyList<BackupItem>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Deletes one backup.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <returns>Whether it existed and was deleted.</returns>
    bool Delete(string id);

    /// <summary>Opens one backup for download.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <returns>The zip's stream, or <see langword="null"/> when no such backup exists.</returns>
    FileStream? OpenRead(string id);

    /// <summary>
    /// Deletes <c>scheduled</c> backups older than <c>backup.retention_days</c>. Manual backups are
    /// never deleted automatically.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>How many backups were deleted.</returns>
    Task<int> CleanUpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates an uploaded backup and stages it for the next start, replacing any earlier staged
    /// restore.
    /// </summary>
    /// <param name="zip">The uploaded zip.</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>Whether the restore was staged, and why not when it was not.</returns>
    Task<StagedRestoreResult> StageRestoreAsync(Stream zip, CancellationToken cancellationToken);

    /// <summary>Validates a stored backup and stages it for the next start.</summary>
    /// <param name="id">The backup's id (its file name).</param>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    /// <returns>Whether the restore was staged, and why not when it was not.</returns>
    Task<StagedRestoreResult> StageRestoreAsync(string id, CancellationToken cancellationToken);
}
