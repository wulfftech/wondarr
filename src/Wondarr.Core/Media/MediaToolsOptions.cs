using Microsoft.Extensions.Options;

namespace Wondarr.Core.Media;

/// <summary>
/// The <c>media</c> section of <c>config.yml</c>: where the bundled <c>ffprobe</c>, <c>ffmpeg</c> and
/// <c>fpcalc</c> binaries are, how long one of them may run, and how much audio is fingerprinted.
/// </summary>
public sealed class MediaToolsOptions
{
    /// <summary>Path or name of the ffprobe binary.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Path or name of the ffmpeg binary.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path or name of the fpcalc binary.</summary>
    public string FpcalcPath { get; set; } = "fpcalc";

    /// <summary>How long any one media tool may run, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Whether a probe also decodes the file end to end (<c>ffmpeg -xerror</c>); off makes probes cheap.</summary>
    public bool DecodeCheck { get; set; } = true;

    /// <summary>How many seconds of audio a fingerprint covers, from the start of the window.</summary>
    public int FingerprintLengthSeconds { get; set; } = 120;
}

/// <summary>
/// Validates <see cref="MediaToolsOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class MediaToolsOptionsValidator : IValidateOptions<MediaToolsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MediaToolsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckPath("media.ffprobe_path", options.FfprobePath, failures);
        CheckPath("media.ffmpeg_path", options.FfmpegPath, failures);
        CheckPath("media.fpcalc_path", options.FpcalcPath, failures);

        if (options.TimeoutSeconds is < 10 or > 600)
        {
            failures.Add($"media.timeout_seconds: must be between 10 and 600 (was {options.TimeoutSeconds})");
        }

        if (options.FingerprintLengthSeconds is < 30 or > 300)
        {
            failures.Add(
                $"media.fingerprint_length_seconds: must be between 30 and 300 (was {options.FingerprintLengthSeconds})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckPath(string key, string? value, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key}: must name the executable (was '{value}')");
        }
    }
}
