using Microsoft.Extensions.Options;

namespace Wondarr.Core.Backup;

/// <summary>
/// The <c>backup</c> section of <c>config.yml</c>: how often the scheduled <c>Backup</c> task runs,
/// how long scheduled backups are kept, and where they live.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>How often the <c>Backup</c> task runs, in days.</summary>
    public int IntervalDays { get; set; } = 7;

    /// <summary>
    /// How long a <c>scheduled</c> backup is kept, in days. Manual backups are never deleted
    /// automatically.
    /// </summary>
    public int RetentionDays { get; set; } = 28;

    /// <summary>
    /// The folder the backups live in. Empty (the default) means <see cref="Configuration.WondarrPaths.BackupsDir"/>,
    /// because the options class cannot see the resolved configuration directory.
    /// </summary>
    public string? Folder { get; set; }
}

/// <summary>
/// Validates <see cref="BackupOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.IntervalDays is < 1 or > 365)
        {
            failures.Add($"backup.interval_days: must be between 1 and 365 (was {options.IntervalDays})");
        }

        if (options.RetentionDays is < 1 or > 3650)
        {
            failures.Add($"backup.retention_days: must be between 1 and 3650 (was {options.RetentionDays})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
