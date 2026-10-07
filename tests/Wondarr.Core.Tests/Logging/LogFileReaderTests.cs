using System.Globalization;
using System.Text;
using System.Text.Json;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Tests.Configuration;
using FluentAssertions;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Wondarr.Core.Tests.Logging;

/// <summary>
/// Reads a real temporary log folder. The files are written in the shape
/// <see cref="LoggingSetup.JsonTemplate"/> produces — once through a real Serilog logger, so the
/// reader is proven against the app's own output, and elsewhere with hand-written lines that match
/// it, which is the only way to get a line without a <c>level</c> or a malformed line into a file.
/// </summary>
public sealed class LogFileReaderTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private readonly TemporaryConfigDirectory _directory = new();
    private readonly LogFileReader _reader;

    public LogFileReaderTests() => _reader = new LogFileReader(_directory.Paths);

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Reads_the_lines_the_real_logger_writes()
    {
        var root = LoggingSetup
            .Configure(new LoggerConfiguration(), new LogOptions(), _directory.Paths, new SecretRegistry())
            .CreateLogger();
        var logger = root.ForContext<LogFileReaderTests>();

        logger.Information("Imported {Count} tracks from {Source}", 3, "slskd");
        logger.Error(new InvalidOperationException("boom"), "Import failed");
        root.Dispose();

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, null), CancellationToken.None);

        page.Truncated.Should().BeFalse();
        page.TotalRecords.Should().Be(2);
        page.Records.Select(entry => entry.Message).Should().Equal("Import failed", "Imported 3 tracks from slskd");
        page.Records[0].Level.Should().Be(LogEventLevel.Error);
        page.Records[0].Exception.Should().Contain("boom");
        page.Records[1].Level.Should().Be(LogEventLevel.Information);
        page.Records[1].Logger.Should().Be(typeof(LogFileReaderTests).FullName);
    }

    [Fact]
    public async Task Reads_the_newest_entries_first_across_files()
    {
        WriteFile(
            "wondarr-20260927.json",
            Base.AddHours(-1),
            Line(0, "Information", "Older first", logger: "Wondarr.Core.Tests.Logging.LogFileReaderTests"),
            Line(1, "Warning", "Older second"));
        WriteFile(
            "wondarr-20260928.json",
            Base,
            Line(2, "Information", "Newest first"),
            Line(3, "Error", "Newest second", exception: "boom"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, null), CancellationToken.None);

        page.Truncated.Should().BeFalse();
        page.TotalRecords.Should().Be(4);
        page.Records.Select(entry => entry.Message).Should().Equal("Newest second", "Newest first", "Older second", "Older first");
        page.Records[0].Level.Should().Be(LogEventLevel.Error);
        page.Records[0].Exception.Should().Be("boom");
        page.Records[0].Time.Should().Be(Base.AddMinutes(3));
        page.Records[3].Logger.Should().Be("Wondarr.Core.Tests.Logging.LogFileReaderTests");
    }

    [Fact]
    public async Task A_line_without_a_level_is_information()
    {
        WriteFile("wondarr-20260928.json", Base, Line(0, null, "slskd started", logger: "slskd"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, null), CancellationToken.None);

        page.Records.Should().HaveCount(1);
        page.Records[0].Level.Should().Be(LogEventLevel.Information);
        page.Records[0].Logger.Should().Be("slskd");
    }

    [Fact]
    public async Task A_malformed_line_is_skipped()
    {
        WriteFile(
            "wondarr-20260928.json",
            Base,
            Line(0, "Information", "Before the broken line"),
            "{\"timestamp\": \"2026-09-28T10:02:00Z\", \"message\": \"truncated\"",
            "not json at all",
            Line(3, "Information", "After the broken line"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, null), CancellationToken.None);

        page.TotalRecords.Should().Be(2);
        page.Records.Select(entry => entry.Message).Should().Equal("After the broken line", "Before the broken line");
    }

    [Fact]
    public async Task The_level_filter_keeps_that_level_and_above()
    {
        WriteFile(
            "wondarr-20260928.json",
            Base,
            Line(0, "Debug", "Quiet"),
            Line(1, "Information", "Normal"),
            Line(2, "Warning", "Loud"),
            Line(3, "Error", "Louder"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, LogEventLevel.Warning, null), CancellationToken.None);

        page.TotalRecords.Should().Be(2);
        page.Records.Select(entry => entry.Message).Should().Equal("Louder", "Loud");
    }

    [Fact]
    public async Task The_text_filter_matches_the_message_case_insensitively()
    {
        WriteFile(
            "wondarr-20260928.json",
            Base,
            Line(0, "Information", "Imported 3 tracks"),
            Line(1, "Information", "Skipped 1 track"),
            Line(2, "Information", "Nothing to do"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, "TRACK"), CancellationToken.None);

        page.TotalRecords.Should().Be(2);
        page.Records.Select(entry => entry.Message).Should().Equal("Skipped 1 track", "Imported 3 tracks");
    }

    [Fact]
    public async Task Paging_returns_the_requested_window()
    {
        WriteFile(
            "wondarr-20260928.json",
            Base,
            Line(0, "Information", "Entry 0"),
            Line(1, "Information", "Entry 1"),
            Line(2, "Information", "Entry 2"),
            Line(3, "Information", "Entry 3"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(2, 2, null, null), CancellationToken.None);

        page.TotalRecords.Should().Be(4);
        page.Records.Select(entry => entry.Message).Should().Equal("Entry 1", "Entry 0");
    }

    [Fact]
    public void Lists_the_files_newest_first_with_their_size_and_write_time()
    {
        WriteFile("wondarr-20260927.json", Base.AddHours(-1), Line(0, "Information", "Older"));
        WriteFile("wondarr-20260928.json", Base, Line(0, "Information", "Newer"));
        File.WriteAllText(Path.Combine(_directory.Paths.LogsDir, "config.yml"), "not a log");
        File.WriteAllText(Path.Combine(_directory.Paths.LogsDir, "wondarr-20260927.txt"), "not json");

        var files = _reader.ListFiles();

        files.Select(file => file.Name).Should().Equal("wondarr-20260928.json", "wondarr-20260927.json");
        files[0].Size.Should().BeGreaterThan(0);
        files[0].LastWriteTime.Should().Be(Base);
    }

    [Theory]
    [InlineData("../config.yml")]
    [InlineData("wondarr-x.json/../../config.yml")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\config.yml")]
    public void A_name_outside_the_listing_is_not_opened(string name)
    {
        WriteFile("wondarr-20260928.json", Base, Line(0, "Information", "Only file"));

        _reader.OpenRead(name).Should().BeNull();
    }

    [Fact]
    public async Task Reads_while_another_handle_has_the_file_open_for_writing()
    {
        const string name = "wondarr-20260928.json";
        WriteFile(name, Base, Line(0, "Information", "Before the append"));

        // Serilog holds the current file open for writing; the reader must share it rather than
        // block the app's own logging.
        await using var writer = new FileStream(
            Path.Combine(_directory.Paths.LogsDir, name),
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        writer.Seek(0, SeekOrigin.End);
        var appended = Encoding.UTF8.GetBytes(Line(1, "Warning", "Appended while open") + "\n");
        await writer.WriteAsync(appended, CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);

        var read = _reader.OpenRead(name);
        using var text = new StreamReader(read ?? throw new InvalidOperationException("the listed file was not opened"));
        var content = await text.ReadToEndAsync(CancellationToken.None);

        content.Should().Contain("Appended while open");

        // And the other way round: a second writer is not blocked by the read handle either.
        await using var second = new FileStream(
            Path.Combine(_directory.Paths.LogsDir, name),
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        second.Seek(0, SeekOrigin.End);
        second.Write(new byte[] { 0x0A });
    }

    [Fact]
    public async Task The_scan_bound_reports_the_rest_as_truncated()
    {
        // A huge older file behind a small newest one: the scan reads the newest file, then stops
        // part way through the older one and reports the rest as truncated.
        var filler = new List<string>();

        for (var index = 0; index < 70_000; index++)
        {
            filler.Add(Line(0, "Information", $"Filler line {index:000000} with some padding text to make the file large"));
        }

        WriteFile("wondarr-20260927.json", Base.AddHours(-1), [.. filler]);
        WriteFile("wondarr-20260928.json", Base, Line(8, "Information", "Second newest"), Line(9, "Information", "Newest of all"));

        var page = await _reader.ReadEntriesAsync(new LogQuery(1, 10, null, null), CancellationToken.None);

        page.Truncated.Should().BeTrue();
        page.TotalRecords.Should().BeGreaterThan(2).And.BeLessThan(filler.Count + 2);
        page.Records.Should().HaveCount(10);
        page.Records[0].Message.Should().Be("Newest of all");
        page.Records[1].Message.Should().Be("Second newest");
    }

    /// <summary>Writes one log file in the template's shape and stamps its write time.</summary>
    private void WriteFile(string name, DateTime writeTime, params string[] lines)
    {
        Directory.CreateDirectory(_directory.Paths.LogsDir);
        var path = Path.Combine(_directory.Paths.LogsDir, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, writeTime);
    }

    /// <summary>
    /// One line exactly as <see cref="LoggingSetup.JsonTemplate"/> renders it: timestamp, level,
    /// message, exception, props. A <see langword="null"/> level leaves the property out, which is
    /// what the template does for Information.
    /// </summary>
    private static string Line(int minute, string? level, string message, string? logger = null, string? exception = null)
    {
        var timestamp = Base.AddMinutes(minute).ToString("O", CultureInfo.InvariantCulture);
        var levelPart = level is null ? string.Empty : $"\"level\":{JsonSerializer.Serialize(level)},";
        var exceptionPart = exception is null ? string.Empty : $",\"exception\":{JsonSerializer.Serialize(exception)}";
        var props = logger is null ? "{}" : $"{{\"SourceContext\":{JsonSerializer.Serialize(logger)}}}";

        return $"{{\"timestamp\":\"{timestamp}\",{levelPart}\"message\":{JsonSerializer.Serialize(message)}{exceptionPart},\"props\":{props}}}";
    }
}
