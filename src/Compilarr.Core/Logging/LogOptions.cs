using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Compilarr.Core.Logging;

/// <summary>How the console sink renders each event.</summary>
public enum LogConsoleFormat
{
    /// <summary>One JSON object per line, the format used by the file sink too.</summary>
    Json,

    /// <summary>Human-readable single-line text.</summary>
    Text,
}

/// <summary>
/// The <c>log</c> section of <c>config.yml</c>.
/// </summary>
public sealed class LogOptions
{
    /// <summary>Serilog level name: Verbose, Debug, Information, Warning, Error or Fatal.</summary>
    public string Level { get; set; } = "Information";

    /// <summary>Console rendering; the log files are always JSON.</summary>
    public LogConsoleFormat ConsoleFormat { get; set; } = LogConsoleFormat.Json;

    /// <summary>How many daily log files to keep.</summary>
    public int RetainedFiles { get; set; } = 7;

    /// <summary>Size at which a log file rolls over.</summary>
    public int FileSizeLimitMb { get; set; } = 10;
}

/// <summary>
/// Validates <see cref="LogOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class LogOptionsValidator : IValidateOptions<LogOptions>
{
    private static readonly string[] LevelNames = Enum.GetNames<LogEventLevel>();

    public ValidateOptionsResult Validate(string? name, LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        var level = options.Level ?? string.Empty;
        if (!LevelNames.Contains(level, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"log.level: must be one of {string.Join(", ", LevelNames)} (was '{level}')");
        }

        if (!Enum.IsDefined(options.ConsoleFormat))
        {
            failures.Add($"log.console_format: must be Json or Text (was {(int)options.ConsoleFormat})");
        }

        if (options.RetainedFiles is < 1 or > 100)
        {
            failures.Add($"log.retained_files: must be between 1 and 100 (was {options.RetainedFiles})");
        }

        if (options.FileSizeLimitMb is < 1 or > 1000)
        {
            failures.Add($"log.file_size_limit_mb: must be between 1 and 1000 (was {options.FileSizeLimitMb})");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
