using System.Globalization;
using Wondarr.Core.Configuration;

namespace Wondarr.Core.HealthCheck;

/// <summary>
/// Warns when the file sink has gone quiet: Serilog swallows its own failures, so a folder that
/// passes <see cref="LogFolderHealthCheck"/> can still end up with no log file written.
/// </summary>
public sealed class LogWritingHealthCheck : IHealthCheck
{
    /// <summary>How long the app must have been running before a missing log file counts as a fault.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>How long the newest log file may go without a write. Request logging and the per-minute tasks write far more often.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private readonly string _logsDir;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;

    /// <summary>Initialises a new instance of the <see cref="LogWritingHealthCheck"/> class.</summary>
    /// <param name="paths">Where the log files live.</param>
    /// <param name="time">The clock.</param>
    /// <param name="startedAt">When the process started, on the same clock.</param>
    public LogWritingHealthCheck(WondarrPaths paths, TimeProvider time, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);

        _logsDir = paths.LogsDir;
        _time = time;
        _startedAt = startedAt;
    }

    /// <inheritdoc />
    public string Name => nameof(LogWritingHealthCheck);

    /// <inheritdoc />
    public Task<HealthCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now - _startedAt < GracePeriod)
        {
            return Task.FromResult(Ok("Wondarr has only just started, so its log file isn't checked yet"));
        }

        var files = ListLogFiles();
        var newest = files.OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();

        // The sink names files wondarr-<yyyyMMdd>.json (or wondarr-<yyyyMMdd>_001.json once a file
        // hits the size limit), with the local date.
        var todayPrefix = "wondarr-" + _time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var hasToday = files.Any(file => file.Name.StartsWith(todayPrefix, StringComparison.Ordinal));

        if (!hasToday
            || (newest is not null && now - new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero) >= StaleAfter))
        {
            return Task.FromResult(Warn(newest));
        }

        return Task.FromResult(Ok("Wondarr is writing its log file"));
    }

    private List<FileInfo> ListLogFiles() =>
        Directory.Exists(_logsDir)
            ? [.. new DirectoryInfo(_logsDir).EnumerateFiles("wondarr-*.json")]
            : [];

    private HealthCheck Warn(FileInfo? newest)
    {
        var detail = newest is null
            ? "no log file found"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"newest: {newest.Name}, last written {newest.LastWriteTime:yyyy-MM-dd HH:mm}");

        return new HealthCheck(
            Name,
            HealthCheckResult.Warning,
            $"Wondarr isn't writing its log file ({detail}). Check the container log for lines starting serilog:",
            null);
    }

    private HealthCheck Ok(string message) => new(Name, HealthCheckResult.Ok, message, null);
}
