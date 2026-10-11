using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Configuration;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Keeps the wall-clock times of recent Soulseek search submissions across restarts, so a restart
/// (or a crash loop) does not hand out a fresh 30-per-4-minutes allowance.
/// </summary>
/// <remarks>
/// A small JSON file under the slskd directory rather than a settings row: the budget is read once
/// at start and written once per submission, it is not configuration, and a file written atomically
/// (temporary file, then move) cannot be half-read. A missing, unreadable or corrupt file is an empty
/// history — the budget then behaves as it did before this existed — never an error.
/// </remarks>
public sealed partial class SoulseekSearchHistory
{
    /// <summary>Name of the file inside the slskd directory.</summary>
    public const string FileName = "search-budget.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger<SoulseekSearchHistory> _logger;

    /// <summary>Initialises a new instance of the <see cref="SoulseekSearchHistory"/> class.</summary>
    /// <param name="paths">Where Wondarr keeps its files; the history lives in the slskd directory.</param>
    /// <param name="logger">Receives a warning when the file cannot be read or written.</param>
    public SoulseekSearchHistory(WondarrPaths paths, ILogger<SoulseekSearchHistory> logger)
        : this(Path.Combine(ArgumentNullGuard(paths).SlskdDir, FileName), logger)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="SoulseekSearchHistory"/> class.</summary>
    /// <param name="path">The file to keep the history in.</param>
    /// <param name="logger">Receives a warning when the file cannot be read or written.</param>
    public SoulseekSearchHistory(string path, ILogger<SoulseekSearchHistory> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _logger = logger;
    }

    /// <summary>Reads the stored submission times, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<StoredHistory>(File.ReadAllText(_path), SerializerOptions);

            return stored?.Submissions is { } submissions ? [.. submissions.Order()] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(_logger, exception);

            return [];
        }
    }

    /// <summary>Replaces the stored submission times, atomically. A failed write is logged, never thrown.</summary>
    public void Save(IEnumerable<DateTimeOffset> submissions)
    {
        ArgumentNullException.ThrowIfNull(submissions);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredHistory([.. submissions]), SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, exception);
        }
    }

    private static WondarrPaths ArgumentNullGuard(WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return paths;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the Soulseek search history; the search budget starts empty")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the Soulseek search history; a restart will not remember these searches")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    private sealed record StoredHistory(List<DateTimeOffset> Submissions);
}
