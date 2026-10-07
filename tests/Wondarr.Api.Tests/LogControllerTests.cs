using System.Globalization;
using System.Net;
using System.Text.Json;
using Wondarr.Api.Logs;
using Wondarr.Core.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/log</c> and <c>/api/v1/log/file</c> read the app's own log folder. The tests write
/// their own files into it next to the host's, and filter on a marker message so the host's own
/// lines cannot change what is expected.
/// </summary>
public sealed class LogControllerTests
{
    private const string LogEndpoint = "/api/v1/log";
    private const string Marker = "Marker entry";

    [Fact]
    public async Task The_log_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(LogEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Entries_are_paged_newest_first_across_files()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{LogEndpoint}?page=1&pageSize=2&filter={Uri.EscapeDataString(Marker)}", UriKind.Relative));
        var body = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(2);
        body.GetProperty("totalRecords").GetInt32().Should().Be(5);
        body.GetProperty("sortKey").GetString().Should().Be("time");
        body.GetProperty("sortDirection").GetString().Should().Be("descending");

        var records = body.GetProperty("records");
        records.EnumerateArray().Select(record => record.GetProperty("message").GetString())
            .Should().Equal("Marker entry 4", "Marker entry 3");

        var first = records.EnumerateArray().First();
        first.GetProperty("id").GetInt64().Should().Be(1);
        first.GetProperty("level").GetString().Should().Be("Error");
        first.GetProperty("logger").GetString().Should().Be("slskd");
        first.GetProperty("exception").GetString().Should().Be("System.InvalidOperationException: boom");
        first.GetProperty("time").GetDateTime().Should().Be(Base.AddMinutes(4));

        response.Headers.Contains(LogController.TruncatedHeader).Should().BeFalse();
    }

    [Fact]
    public async Task The_second_page_continues_the_positions()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{LogEndpoint}?page=2&pageSize=2&filter={Uri.EscapeDataString(Marker)}", UriKind.Relative));
        var body = await ReadJsonAsync(response);

        body.GetProperty("page").GetInt32().Should().Be(2);
        body.GetProperty("totalRecords").GetInt32().Should().Be(5);
        body.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("id").GetInt64())
            .Should().Equal(3, 4);
        body.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("message").GetString())
            .Should().Equal("Marker entry 2", "Marker entry 1");
    }

    [Fact]
    public async Task The_level_filter_keeps_that_level_and_above()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{LogEndpoint}?level=Warning&filter={Uri.EscapeDataString(Marker)}", UriKind.Relative));
        var body = await ReadJsonAsync(response);

        body.GetProperty("totalRecords").GetInt32().Should().Be(2);
        body.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("level").GetString())
            .Should().Equal("Error", "Warning");
    }

    [Fact]
    public async Task A_bounded_scan_sets_the_truncated_header()
    {
        using var factory = new WondarrAppFactory(configureServices: services =>
            services.AddSingleton<ILogFileReader>(new TruncatedLogReader()));
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri(LogEndpoint, UriKind.Relative));

        response.Headers.GetValues(LogController.TruncatedHeader).Should().ContainSingle("true");
    }

    [Fact]
    public async Task The_file_list_reports_the_log_files_newest_first()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{LogEndpoint}/file", UriKind.Relative));
        var files = (await ReadJsonAsync(response)).EnumerateArray().ToList();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        files.Select(file => file.GetProperty("filename").GetString())
            .Should().Contain("wondarr-test-new.json").And.Contain("wondarr-test-old.json");

        var newest = files.First(file => file.GetProperty("filename").GetString() == "wondarr-test-new.json");
        newest.GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        newest.GetProperty("lastWriteTime").GetDateTime().Should().Be(Base);
        newest.GetProperty("contentsUrl").GetString().Should().Be("/api/v1/log/file/wondarr-test-new.json");
    }

    [Fact]
    public async Task A_log_file_is_downloaded_as_text()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{LogEndpoint}/file/wondarr-test-old.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Marker entry 0");
    }

    [Fact]
    public async Task An_unknown_file_name_is_not_found()
    {
        using var factory = new WondarrAppFactory();
        WriteFiles(factory);
        using var client = Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"{LogEndpoint}/file/wondarr-nope.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static readonly DateTime Base = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two log files in the folder, the older one written first. The host's own log file is in there
    /// too, which is why every query filters on the marker message.
    /// </summary>
    private static void WriteFiles(WondarrAppFactory factory)
    {
        var directory = Path.Combine(factory.ConfigDir, "logs");
        Directory.CreateDirectory(directory);

        WriteFile(Path.Combine(directory, "wondarr-test-old.json"), Base.AddHours(-1),
            Line(0, "Information", $"{Marker} 0"),
            Line(1, "Information", $"{Marker} 1"));
        WriteFile(Path.Combine(directory, "wondarr-test-new.json"), Base,
            Line(2, "Information", $"{Marker} 2"),
            Line(3, "Warning", $"{Marker} 3"),
            Line(4, "Error", $"{Marker} 4", exception: "System.InvalidOperationException: boom"));
    }

    private static void WriteFile(string path, DateTime writeTime, params string[] lines)
    {
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, writeTime);
    }

    /// <summary>One line exactly as <see cref="LoggingSetup.JsonTemplate"/> renders it.</summary>
    private static string Line(int minute, string level, string message, string? exception = null)
    {
        var timestamp = Base.AddMinutes(minute).ToString("O", CultureInfo.InvariantCulture);
        var exceptionPart = exception is null ? string.Empty : $",\"exception\":{JsonSerializer.Serialize(exception)}";

        return $"{{\"timestamp\":\"{timestamp}\",\"level\":{JsonSerializer.Serialize(level)}," +
            $"\"message\":{JsonSerializer.Serialize(message)}{exceptionPart}," +
            $"\"props\":{{\"SourceContext\":\"slskd\"}}}}";
    }

    private static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>Stands in for the reader so the controller's truncation header can be checked cheaply.</summary>
    private sealed class TruncatedLogReader : ILogFileReader
    {
        public IReadOnlyList<LogFileInfo> ListFiles() => [];

        public Stream? OpenRead(string name) => null;

        public Task<LogPage> ReadEntriesAsync(LogQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new LogPage([], 3, Truncated: true));
    }
}
