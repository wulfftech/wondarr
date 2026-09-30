using Microsoft.Extensions.Options;

namespace Wondarr.Core.References;

/// <summary>
/// The <c>reference</c> section of <c>config.yml</c>: how sure an identification has to be before it is
/// accepted on its own, how far a file's length may be from the identified recording's, and how many
/// pending files one identification run works through at a time (LIBRARY_OUTPUT §7.6).
/// </summary>
public sealed class ReferenceOptions
{
    /// <summary>
    /// The confidence at or above which a file becomes an owned song without the Match queue. The
    /// tiers sit around it: a tagged MBID is 1.0, an ISRC 0.95, a text search 0.90 and an unconfirmed
    /// AcoustID at most 0.89.
    /// </summary>
    public double AutoAcceptThreshold { get; set; } = 0.90;

    /// <summary>How far a file's probed length may be from the identified recording's and still be it.</summary>
    public int DurationToleranceMs { get; set; } = 5000;

    /// <summary>
    /// How many pending files one chunk holds. A chunk's identities are added as one batch, so the
    /// album policy sees them together and an artist's songs do not scatter.
    /// </summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Validates <see cref="ReferenceOptions"/>. Every failure message starts with the YAML key so the user
/// can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class ReferenceOptionsValidator : IValidateOptions<ReferenceOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ReferenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.AutoAcceptThreshold is < 0.5 or > 1.0)
        {
            failures.Add(
                $"reference.auto_accept_threshold: must be between 0.5 and 1.0 (was {options.AutoAcceptThreshold})");
        }

        if (options.DurationToleranceMs is < 1000 or > 30000)
        {
            failures.Add(
                $"reference.duration_tolerance_ms: must be between 1000 and 30000 (was {options.DurationToleranceMs})");
        }

        if (options.BatchSize is < 10 or > 500)
        {
            failures.Add($"reference.batch_size: must be between 10 and 500 (was {options.BatchSize})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
