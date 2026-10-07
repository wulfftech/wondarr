using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Configuration;
using Serilog.Events;

namespace Wondarr.Core.Logging;

/// <summary>One file in the log folder, as the file list reports it.</summary>
/// <param name="Name">The file name, for example <c>wondarr-20260928.json</c>.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="LastWriteTime">When the file was last written to.</param>
public sealed record LogFileInfo(string Name, long Size, DateTime LastWriteTime);

/// <summary>One parsed log line.</summary>
/// <param name="Time">When the event was logged.</param>
/// <param name="Level">The event's level.</param>
/// <param name="Logger">The <c>SourceContext</c> property, or an empty string; slskd's output is logged under <c>slskd</c>.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The exception text, or <see langword="null"/>.</param>
public sealed record LogEntry(DateTime Time, LogEventLevel Level, string Logger, string Message, string? Exception);

/// <summary>What to read from the log files.</summary>
/// <param name="Page">The 1-based page number; anything below 1 becomes 1.</param>
/// <param name="PageSize">The rows per page, clamped to 1–1000.</param>
/// <param name="MinimumLevel">Only entries at this level or above, or <see langword="null"/> for every level.</param>
/// <param name="Filter">A case-insensitive substring the message must contain, or <see langword="null"/>.</param>
public sealed record LogQuery(int Page, int PageSize, LogEventLevel? MinimumLevel, string? Filter);

/// <summary>
/// One page of entries, how many the filters matched in total, and whether the scan stopped at its
/// byte bound before it reached the oldest entry.
/// </summary>
/// <param name="Records">The entries on this page, newest first.</param>
/// <param name="TotalRecords">How many entries the filters matched within the scanned data.</param>
/// <param name="Truncated">Whether older data was left unread because the byte bound was reached.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Records, int TotalRecords, bool Truncated);

/// <summary>Reads the app's own JSON log files for the System pages.</summary>
public interface ILogFileReader
{
    /// <summary>Lists the log files, newest first.</summary>
    IReadOnlyList<LogFileInfo> ListFiles();

    /// <summary>
    /// Opens a log file for reading while the app keeps writing to it, or
    /// <see langword="null"/> when the name is not one of <see cref="ListFiles"/>'s names.
    /// </summary>
    /// <param name="name">The file name exactly as the listing reported it.</param>
    Stream? OpenRead(string name);

    /// <summary>Reads a page of entries, newest first, across the files newest first.</summary>
    /// <param name="query">The page, the minimum level and the message filter.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<LogPage> ReadEntriesAsync(LogQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the rolling JSON files the Serilog file sink writes (<see cref="LoggingSetup.JsonTemplate"/>:
/// one object per line with <c>timestamp</c>, <c>level</c>, <c>message</c>, <c>exception</c> and
/// <c>props</c>). The work is bounded: a request scans at most the newest
/// <see cref="MaxScanBytes"/> of log data, reading in chunks and keeping only the entries the page
/// needs, so a full log folder (7 × 10 MB by default) never ends up in memory.
/// </summary>
public sealed class LogFileReader : ILogFileReader
{
    /// <summary>The pattern the rolling file names match.</summary>
    private const string FilePattern = "wondarr*.json";

    /// <summary>How much log data one request may scan before it reports the rest as truncated.</summary>
    private const long MaxScanBytes = 10 * 1024 * 1024;

    /// <summary>The largest page size a caller may ask for.</summary>
    private const int MaxPageSize = 1000;

    /// <summary>
    /// The most entries held at once while scanning. A page needs the newest
    /// <c>skip + pageSize</c> matches; a page number beyond this cap still counts matches but
    /// returns an empty page, so a hostile query string cannot turn the buffer into an allocation.
    /// </summary>
    private const int MaxBufferedEntries = 10_000;

    /// <summary>A single line longer than this is dropped rather than buffered whole.</summary>
    private const int MaxLineLength = 1024 * 1024;

    /// <summary>How much is read from a file at a time.</summary>
    private const int ChunkSize = 64 * 1024;

    private readonly WondarrPaths _paths;

    /// <summary>Initialises a new instance of the <see cref="LogFileReader"/> class.</summary>
    /// <param name="paths">The resolved configuration paths, for the log folder.</param>
    public LogFileReader(WondarrPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <inheritdoc />
    public IReadOnlyList<LogFileInfo> ListFiles()
    {
        var directory = _paths.LogsDir;

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, FilePattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => new LogFileInfo(file.Name, file.Length, file.LastWriteTimeUtc))
            .ToList();
    }

    /// <inheritdoc />
    public Stream? OpenRead(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // The name has to be one the listing returned, so a caller's string is never combined into a
        // path: "../config.yml", an absolute path or a name with a separator in it finds no file.
        if (!ListFiles().Any(file => string.Equals(file.Name, name, StringComparison.Ordinal)))
        {
            return null;
        }

        // Serilog holds the current file open for writing (shared: true), so the read must share it.
        return new FileStream(
            Path.Combine(_paths.LogsDir, name),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <inheritdoc />
    public async Task<LogPage> ReadEntriesAsync(LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var skip = (Math.Max(query.Page, 1) - 1) * pageSize;
        var keepEntries = skip + pageSize <= MaxBufferedEntries;
        var capacity = keepEntries ? skip + pageSize : 0;

        // The files are scanned newest first and each file's newest matches are appended after the
        // newer files', so the page is in order without a second pass. Within a file the lines are
        // oldest first, so only a ring of the matches the page still needs is kept.
        var state = new Scan(query, capacity);

        foreach (var file in ListFiles())
        {
            if (state.Remaining <= 0)
            {
                state.Truncated = true;
                break;
            }

            state.Wanted = Math.Max(capacity - state.Page.Count, 0);

            await ScanFileAsync(file, state, cancellationToken).ConfigureAwait(false);

            if (state.Wanted > 0)
            {
                state.Page.AddRange(state.Ring.Reverse());
                state.Ring.Clear();
            }
        }

        return new LogPage(
            [.. state.Page.Skip(skip).Take(pageSize)],
            state.Total,
            state.Truncated);
    }

    /// <summary>
    /// Reads one file forwards in chunks, feeding every complete line to the filters, and stops when
    /// the byte bound runs out rather than at the end of the file.
    /// </summary>
    private async Task ScanFileAsync(LogFileInfo file, Scan state, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(file.Name);
        if (stream is null)
        {
            return;
        }

        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[ChunkSize];
        var chars = new char[ChunkSize];
        var carry = new StringBuilder();
        var dropping = false;

        while (state.Remaining > 0)
        {
            var read = await stream
                .ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, state.Remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            state.Remaining -= read;

            // A decoder rather than Encoding.UTF8.GetString, so a character split across two chunks
            // is still decoded instead of turning into a replacement character.
            var chunk = new string(chars, 0, decoder.GetChars(bytes, 0, read, chars, 0, flush: false));

            var start = 0;
            int newline;
            while ((newline = chunk.IndexOf('\n', start)) >= 0)
            {
                carry.Append(chunk, start, newline - start);
                HandleLine(carry.ToString(), dropping, state);
                carry.Clear();
                dropping = false;
                start = newline + 1;
            }

            carry.Append(chunk, start, chunk.Length - start);

            if (carry.Length > MaxLineLength)
            {
                // One line larger than the cap is dropped rather than buffered whole.
                carry.Clear();
                dropping = true;
            }

            if (state.Remaining <= 0)
            {
                state.Truncated = true;
            }
        }
    }

    private static void HandleLine(string line, bool dropping, Scan state)
    {
        if (dropping || string.IsNullOrWhiteSpace(line) || !TryParse(line, out var entry))
        {
            return;
        }

        if (state.Query.MinimumLevel is { } minimum && entry.Level < minimum)
        {
            return;
        }

        if (state.Query.Filter is { } filter && !entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        state.Total++;

        if (state.Wanted <= 0)
        {
            return;
        }

        state.Ring.Enqueue(entry);
        if (state.Ring.Count > state.Wanted)
        {
            state.Ring.Dequeue();
        }
    }

    private static bool TryParse(string line, [NotNullWhen(true)] out LogEntry? entry)
    {
        entry = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            entry = new LogEntry(
                ReadTimestamp(root),
                ReadLevel(root),
                ReadLogger(root),
                ReadString(root, "message") ?? string.Empty,
                ReadString(root, "exception"));

            return true;
        }
        catch (JsonException)
        {
            // A line that does not parse is skipped, not fatal: the file is written concurrently.
            return false;
        }
    }

    private static DateTime ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var timestamp)
            && timestamp.ValueKind == JsonValueKind.String
            && timestamp.TryGetDateTime(out var parsed)
        ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        : DateTime.MinValue;

    private static LogEventLevel ReadLevel(JsonElement root)
    {
        // A line without a level is Information: Serilog's expression template omits it there.
        if (root.TryGetProperty("level", out var level)
            && level.ValueKind == JsonValueKind.String
            && level.GetString() is { } text
            && Enum.TryParse(text, ignoreCase: true, out LogEventLevel parsed))
        {
            return parsed;
        }

        return LogEventLevel.Information;
    }

    private static string ReadLogger(JsonElement root) =>
        root.TryGetProperty("props", out var props)
            && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("SourceContext", out var context)
            && context.ValueKind == JsonValueKind.String
        ? context.GetString() ?? string.Empty
        : string.Empty;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The mutable state of one entry scan: what matched, how much is left to read.</summary>
    /// <param name="query">The filters the scan applies to every line.</param>
    /// <param name="capacity">How many of the newest matches the page needs at most.</param>
    private sealed class Scan(LogQuery query, int capacity)
    {
        /// <summary>Gets the filters the scan applies to every line.</summary>
        public LogQuery Query { get; } = query;

        /// <summary>Gets the page so far, newest first, at most <paramref name="capacity"/> entries.</summary>
        public List<LogEntry> Page { get; } = new(capacity);

        /// <summary>Gets the current file's newest matches, oldest first.</summary>
        public Queue<LogEntry> Ring { get; } = new();

        /// <summary>Gets or sets how many more matches the page needs from the current file.</summary>
        public int Wanted { get; set; }

        /// <summary>Gets how many entries the filters matched so far.</summary>
        public int Total { get; set; }

        /// <summary>Gets how many bytes of log data may still be read.</summary>
        public long Remaining { get; set; } = MaxScanBytes;

        /// <summary>Gets or sets a value indicating whether the scan ran out of bytes early.</summary>
        public bool Truncated { get; set; }
    }
}
