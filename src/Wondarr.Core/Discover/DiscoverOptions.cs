using Microsoft.Extensions.Options;

namespace Wondarr.Core.Discover;

/// <summary>
/// The <c>discover</c> section of <c>config.yml</c> (overridable as <c>APP__DISCOVER__…</c>): how the
/// Discover page draws and ranks its suggestions.
/// </summary>
public sealed class DiscoverOptions
{
    /// <summary>The ListenBrainz Labs algorithm that was current on 2026-11-17.</summary>
    public const string DefaultListenBrainzAlgorithm =
        "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30_top_n_listeners_1000";

    /// <summary>
    /// The ListenBrainz Labs similarity algorithm. Its server validates the name against an enumeration
    /// that changes over time; a rejected name turns that source off with a warning.
    /// </summary>
    public string ListenBrainzAlgorithm { get; set; } = DefaultListenBrainzAlgorithm;

    /// <summary>How many random library songs seed a mix.</summary>
    public int MixSeedCount { get; set; } = 5;

    /// <summary>The most suggestions one artist may have in a result.</summary>
    public int MaxPerArtist { get; set; } = 3;

    /// <summary>How long a result stays cached per seed, in hours.</summary>
    public int CacheHours { get; set; } = 6;
}

/// <summary>
/// Validates <see cref="DiscoverOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class DiscoverOptionsValidator : IValidateOptions<DiscoverOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DiscoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ListenBrainzAlgorithm))
        {
            failures.Add("discover.listenbrainz_algorithm: must not be empty");
        }

        if (options.MixSeedCount is < 1 or > 20)
        {
            failures.Add($"discover.mix_seed_count: must be between 1 and 20 (was {options.MixSeedCount})");
        }

        if (options.MaxPerArtist is < 1 or > 50)
        {
            failures.Add($"discover.max_per_artist: must be between 1 and 50 (was {options.MaxPerArtist})");
        }

        if (options.CacheHours is < 0 or > 168)
        {
            failures.Add($"discover.cache_hours: must be between 0 and 168 (was {options.CacheHours})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
