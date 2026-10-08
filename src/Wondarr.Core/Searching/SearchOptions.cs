using Microsoft.Extensions.Options;

namespace Wondarr.Core.Searching;

/// <summary>
/// The <c>search</c> section of <c>config.yml</c>: how often the missing-song loop runs, how many
/// songs it takes at a time, and the limits the automatic grab loop works under
/// (ARCHITECTURE §5.2, MATCHING_ENGINE §6.6).
/// </summary>
public sealed class SearchOptions
{
    /// <summary>How often the <c>MissingSearch</c> task runs.</summary>
    public int MissingIntervalHours { get; set; } = 6;

    /// <summary>How many songs one <c>MissingSearch</c> run searches at most.</summary>
    public int MissingBatchSize { get; set; } = 50;

    /// <summary>How often the <c>UpgradeSearch</c> task runs (ARCHITECTURE §5.5, MATCHING_ENGINE §6.6).</summary>
    public int UpgradeIntervalHours { get; set; } = 24;

    /// <summary>How many songs one <c>UpgradeSearch</c> run searches at most, to be polite to the network.</summary>
    public int UpgradeBatchSize { get; set; } = 50;

    /// <summary>
    /// Whether adding a monitored song queues a <c>SongSearch</c> for it at once; the regular
    /// <c>MissingSearch</c> is the fallback when this is off.
    /// </summary>
    public bool SearchOnAdd { get; set; } = true;

    /// <summary>How many candidates one search run may try before giving up on it.</summary>
    public int MaxAutoAttemptsPerSearch { get; set; } = 4;

    /// <summary>How many downloads may be in flight at once.</summary>
    public int MaxActiveDownloads { get; set; } = 3;

    /// <summary>How many of a run's candidates are stored, best first.</summary>
    public int MaxStoredCandidates { get; set; } = 200;

    /// <summary>
    /// The per-song re-search backoff, in hours. The nth consecutive fruitless run waits
    /// <c>BackoffHours[min(n − 1, last)]</c> before the song is searched again (MATCHING_ENGINE §6.6).
    /// </summary>
    public List<int> BackoffHours { get; set; } = [1, 6, 24, 72, 168];

    /// <summary>How long <c>MissingSearch</c> waits between checks while every download slot is taken.</summary>
    public int SlotWaitSeconds { get; set; } = 10;

    /// <summary>
    /// The largest usenet post, in MB, downloaded whole for one song (DECISIONS build session 8 #7). A
    /// torrent is never limited: only its wanted files download.
    /// </summary>
    public int MaxContainerSizeMb { get; set; } = 1500;
}

/// <summary>
/// Validates <see cref="SearchOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MissingIntervalHours is < 1 or > 168)
        {
            failures.Add($"search.missing_interval_hours: must be between 1 and 168 (was {options.MissingIntervalHours})");
        }

        if (options.MissingBatchSize is < 1 or > 500)
        {
            failures.Add($"search.missing_batch_size: must be between 1 and 500 (was {options.MissingBatchSize})");
        }

        if (options.UpgradeIntervalHours is < 1 or > 168)
        {
            failures.Add($"search.upgrade_interval_hours: must be between 1 and 168 (was {options.UpgradeIntervalHours})");
        }

        if (options.UpgradeBatchSize is < 1 or > 500)
        {
            failures.Add($"search.upgrade_batch_size: must be between 1 and 500 (was {options.UpgradeBatchSize})");
        }

        if (options.MaxAutoAttemptsPerSearch is < 1 or > 10)
        {
            failures.Add($"search.max_auto_attempts_per_search: must be between 1 and 10 (was {options.MaxAutoAttemptsPerSearch})");
        }

        if (options.MaxActiveDownloads is < 1 or > 5)
        {
            failures.Add($"search.max_active_downloads: must be between 1 and 5 (was {options.MaxActiveDownloads})");
        }

        if (options.MaxStoredCandidates is < 20 or > 1000)
        {
            failures.Add($"search.max_stored_candidates: must be between 20 and 1000 (was {options.MaxStoredCandidates})");
        }

        if (options.SlotWaitSeconds is < 1 or > 300)
        {
            failures.Add($"search.slot_wait_seconds: must be between 1 and 300 (was {options.SlotWaitSeconds})");
        }

        if (options.MaxContainerSizeMb is < 50 or > 100_000)
        {
            failures.Add($"search.max_container_size_mb: must be between 50 and 100000 (was {options.MaxContainerSizeMb})");
        }

        var backoff = options.BackoffHours ?? [];

        if (backoff.Count == 0)
        {
            failures.Add("search.backoff_hours: must name at least one wait");
        }
        else
        {
            var previous = 0;

            foreach (var hours in backoff)
            {
                if (hours <= 0)
                {
                    failures.Add($"search.backoff_hours: every wait must be a positive number of hours (was {hours})");
                    break;
                }

                if (hours <= previous)
                {
                    failures.Add("search.backoff_hours: the waits must ascend, from the shortest to the longest");
                    break;
                }

                previous = hours;
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
