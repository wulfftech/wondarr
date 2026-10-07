// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/Logs/LogItemResource.cs
// and LogFileResource.cs, GPL-3.0. Adapted for Wondarr: the entries come from the app's own rolling
// JSON files through ILogFileReader, and the file list carries an absolute contentsUrl.

namespace Wondarr.Api.Logs;

/// <summary>
/// One log entry as the System → Logs view reads it. The field names match Lidarr's
/// <c>LogItemResource</c>.
/// </summary>
/// <param name="Id">A stable number within the response: the entry's position in the result set.</param>
/// <param name="Time">When the event was logged.</param>
/// <param name="Level">The level name, for example <c>Information</c>.</param>
/// <param name="Logger">The logger the entry came from, for example <c>slskd</c>.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The exception text, or <see langword="null"/>.</param>
public sealed record LogResource(
    long Id,
    DateTime Time,
    string Level,
    string Logger,
    string Message,
    string? Exception);

/// <summary>
/// One log file as the file list reports it. The field names match Lidarr's
/// <c>LogFileResource</c>.
/// </summary>
/// <param name="Id">A stable number within the response: the file's position in the list.</param>
/// <param name="Filename">The file name, for example <c>wondarr-20260928.json</c>.</param>
/// <param name="LastWriteTime">When the file was last written to.</param>
/// <param name="ContentsUrl">Where the file's contents are read from.</param>
public sealed record LogFileResource(
    long Id,
    string Filename,
    DateTime LastWriteTime,
    string ContentsUrl);
