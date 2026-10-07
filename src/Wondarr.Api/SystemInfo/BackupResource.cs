// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/Backup/BackupResource.cs, GPL-3.0.
// Adapted for Wondarr: a record rather than a mutable class, and the id is the backup's file name
// rather than a number the service has to keep in step with the folder.

using Wondarr.Core.Backup;

namespace Wondarr.Api.SystemInfo;

/// <summary>One backup in the System → Backup list, in Lidarr's shape.</summary>
/// <param name="Id">The backup's id (its file name).</param>
/// <param name="Name">The backup's file name.</param>
/// <param name="Path">The download path, <c>/backup/{type}/{name}</c>.</param>
/// <param name="Type">Whether the scheduled task or the user created it.</param>
/// <param name="Size">The zip's size in bytes.</param>
/// <param name="Time">When it was made.</param>
public sealed record BackupResource(string Id, string Name, string Path, BackupType Type, long Size, DateTime Time);

/// <summary>The answer to a restore request: the restore is staged, and the app must restart.</summary>
/// <param name="RestartRequired">Always <see langword="true"/>: the restore is applied on the next start.</param>
public sealed record RestoreResource(bool RestartRequired);
