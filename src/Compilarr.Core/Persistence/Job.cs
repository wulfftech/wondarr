namespace Compilarr.Core.Persistence;

/// <summary>
/// Scheduler state for a named recurring job. Populated by the job runner (P0-06).
/// </summary>
public sealed class Job : EntityBase
{
    /// <summary>Gets or sets the unique job name, for example <c>wanted-search</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured interval, or <see langword="null"/> for an on-demand job.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>Gets or sets the UTC instant the job last ran.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Gets or sets the UTC instant the job is next due.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>Gets or sets a short human-readable description of the last outcome.</summary>
    public string? LastResult { get; set; }
}