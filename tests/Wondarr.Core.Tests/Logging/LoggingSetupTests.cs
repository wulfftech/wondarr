using System.Text.Json;
using Wondarr.Core.Configuration;
using Wondarr.Core.Logging;
using Wondarr.Core.Tests.Configuration;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Xunit;

namespace Wondarr.Core.Tests.Logging;

public class LoggingSetupTests
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Writes_one_json_object_per_line_to_a_daily_file()
    {
        using var directory = new TemporaryConfigDirectory();
        var logger = CreateLogger(new LogOptions(), directory.Paths, new SecretRegistry());

        logger.Information("Imported {Count} tracks from {Source}", 3, "slskd");
        logger.Dispose();

        var file = SingleLogFile(directory.Paths);
        Path.GetFileName(file).Should().StartWith("wondarr-").And.EndWith(".json");

        var line = File.ReadAllLines(file).Where(line => line.Length > 0).First();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        root.GetProperty("timestamp").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("level").GetString().Should().Be("Information");
        root.GetProperty("message").GetString().Should().Be("Imported 3 tracks from slskd");
        root.GetProperty("props").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void Writes_the_event_to_todays_file_under_the_name_the_health_check_looks_for()
    {
        using var directory = new TemporaryConfigDirectory();
        var logger = CreateLogger(new LogOptions(), directory.Paths, new SecretRegistry());

        logger.Information("sink check {Marker}", "m75-marker");
        logger.Dispose();

        var expected = Path.Combine(
            directory.Paths.LogsDir,
            $"wondarr-{DateTime.Now:yyyyMMdd}.json");
        File.Exists(expected).Should().BeTrue();
        File.ReadAllText(expected).Should().Contain("m75-marker");
    }

    [Fact]
    public void The_file_being_written_can_be_read_the_way_the_log_reader_opens_it()
    {
        using var directory = new TemporaryConfigDirectory();
        using var logger = CreateLogger(new LogOptions(), directory.Paths, new SecretRegistry());

        logger.Information("still open {Marker}", "m75b-marker");

        using var stream = new FileStream(
            SingleLogFile(directory.Paths),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().Should().Contain("m75b-marker");
    }

    [Fact]
    public void Never_writes_a_registered_secret()
    {
        using var directory = new TemporaryConfigDirectory();
        var registry = new SecretRegistry();
        registry.Register(ApiKey);
        var logger = CreateLogger(new LogOptions(), directory.Paths, registry);

        logger.Information("Calling http://x/api?apikey={Key}", ApiKey);
        logger.Error(new InvalidOperationException($"Authenticating with {ApiKey} failed"), "Request failed");
        logger.Information("X-Api-Key: {Header}", ApiKey);
        logger.Dispose();

        var text = File.ReadAllText(SingleLogFile(directory.Paths));

        text.Should().Contain("(removed)");
        text.Should().NotContain(ApiKey);
        text.Should().NotContain("0123456789abcdef");
    }

    [Fact]
    public void The_text_console_format_stays_readable()
    {
        using var directory = new TemporaryConfigDirectory();
        var options = new LogOptions { ConsoleFormat = LogConsoleFormat.Text, Level = "Debug" };

        var configuration = LoggingSetup.Configure(new LoggerConfiguration(), options, directory.Paths, new SecretRegistry());
        var logger = configuration.CreateLogger();

        logger.Debug("Quiet");
        logger.Dispose();

        SingleLogFile(directory.Paths).Should().NotBeNull();
    }

    private static Logger CreateLogger(LogOptions options, WondarrPaths paths, ISecretRegistry registry) =>
        LoggingSetup.Configure(new LoggerConfiguration(), options, paths, registry).CreateLogger();

    private static string SingleLogFile(WondarrPaths paths)
    {
        Directory.GetFiles(paths.LogsDir, "wondarr-*").Should().HaveCount(1);
        return Directory.GetFiles(paths.LogsDir, "wondarr-*")[0];
    }
}
