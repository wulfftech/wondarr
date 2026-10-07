// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/Logs/LogController.cs and
// LogFileController.cs, GPL-3.0. Adapted for Wondarr: the entries are read from the app's own rolling
// JSON files through ILogFileReader, which bounds the scan; because the shared paging resource cannot
// carry it, the truncation is reported in the X-Wondarr-Log-Truncated response header.

using Wondarr.Api.Paging;
using Wondarr.Core.Logging;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;

namespace Wondarr.Api.Logs;

/// <summary>
/// The app's own log, as the System → Logs view reads it: a paged list of entries and the files they
/// live in (ARCHITECTURE §5.6). slskd's output is in the same files, under the <c>slskd</c> logger.
/// </summary>
[ApiController]
[Route("api/v1/log")]
public sealed class LogController : ControllerBase
{
    /// <summary>
    /// Set to <c>true</c> when the entry scan stopped at its byte bound before reaching the oldest
    /// entry, so the page and its <c>totalRecords</c> describe the newest data only.
    /// </summary>
    public const string TruncatedHeader = "X-Wondarr-Log-Truncated";

    /// <summary>What the response reports when the caller did not name a sort key.</summary>
    private const string DefaultSortKey = "time";

    private readonly ILogFileReader _reader;

    /// <summary>Initialises a new instance of the <see cref="LogController"/> class.</summary>
    /// <param name="reader">The log file reader.</param>
    public LogController(ILogFileReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    /// <summary>Lists log entries, newest first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// <c>level</c> is a minimum level (a value that cannot be read means "no filter") and
    /// <c>filter</c> a case-insensitive substring of the message. The scan is bounded, so a large log
    /// folder is answered from its newest data; <see cref="TruncatedHeader"/> says when that happened.
    /// </remarks>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<PagingResource<LogResource>>> GetLog(CancellationToken cancellationToken)
    {
        var paging = Request.ToPagingSpec();
        var page = await _reader
            .ReadEntriesAsync(
                new LogQuery(paging.Page, paging.PageSize, ReadLevel(Request.Query["level"]), ReadFilter(Request.Query["filter"])),
                cancellationToken)
            .ConfigureAwait(false);

        if (page.Truncated)
        {
            Response.Headers[TruncatedHeader] = "true";
        }

        return Ok(new PagingResource<LogResource>(
            paging.Page,
            paging.PageSize,
            paging.SortKey ?? DefaultSortKey,
            paging.Descending ? PagingResourceExtensions.Descending : PagingResourceExtensions.Ascending,
            page.TotalRecords,
            [.. page.Records.Select((entry, index) => ToResource(entry, paging.Skip + index + 1))]));
    }

    /// <summary>Lists the log files, newest first.</summary>
    [HttpGet("file")]
    [Produces("application/json")]
    public ActionResult<IReadOnlyList<LogFileResource>> GetLogFiles()
    {
        var files = _reader.ListFiles();

        return Ok(files
            .Select((file, index) => new LogFileResource(
                index + 1,
                file.Name,
                file.LastWriteTime,
                $"/api/v1/log/file/{Uri.EscapeDataString(file.Name)}"))
            .ToList());
    }

    /// <summary>Reads one log file as text.</summary>
    /// <param name="filename">The file name exactly as the list reported it.</param>
    [HttpGet("file/{filename}")]
    [Produces("text/plain")]
    public IActionResult GetLogFile(string filename)
    {
        var stream = _reader.OpenRead(filename);
        if (stream is null)
        {
            return NotFound();
        }

        // The result disposes the stream once the response is written; the read shares the handle
        // Serilog holds for writing, so the app's own logging is never blocked.
        return File(stream, "text/plain");
    }

    private static LogResource ToResource(LogEntry entry, long id) => new(
        id,
        entry.Time,
        entry.Level.ToString(),
        entry.Logger,
        entry.Message,
        entry.Exception);

    private static LogEventLevel? ReadLevel(string? text) =>
        Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var level) ? level : null;

    private static string? ReadFilter(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text;
}
