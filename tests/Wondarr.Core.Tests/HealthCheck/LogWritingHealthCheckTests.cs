using Wondarr.Core.HealthCheck;
using Wondarr.Core.Tests.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.HealthCheck;

public sealed class LogWritingHealthCheckTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);

    private readonly TemporaryConfigDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(Start);

    [Fact]
    public async Task Is_ok_on_a_fresh_start_even_with_no_log_file()
    {
        _time.Advance(TimeSpan.FromMinutes(9));

        var result = await CreateCheck().CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Is_ok_when_todays_file_was_written_recently()
    {
        _time.Advance(TimeSpan.FromHours(2));
        WriteLog("wondarr-20261127.json", _time.GetUtcNow().AddMinutes(-2));

        var result = await CreateCheck().CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Warns_when_there_is_no_file_for_today_after_ten_minutes()
    {
        _time.Advance(TimeSpan.FromMinutes(10));
        WriteLog("wondarr-20261014.json", new DateTimeOffset(2026, 10, 14, 8, 46, 0, TimeSpan.Zero));

        var result = await CreateCheck().CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Source.Should().Be("LogWritingHealthCheck");
        result.Message.Should().Contain("wondarr-20261014.json").And.Contain("serilog:");
    }

    [Fact]
    public async Task Warns_when_there_is_no_log_file_at_all_after_ten_minutes()
    {
        _time.Advance(TimeSpan.FromMinutes(11));

        var result = await CreateCheck().CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Contain("no log file found");
    }

    [Fact]
    public async Task Warns_when_the_newest_file_has_not_been_written_for_thirty_minutes()
    {
        _time.Advance(TimeSpan.FromHours(2));
        WriteLog("wondarr-20261127.json", _time.GetUtcNow().AddMinutes(-31));

        var result = await CreateCheck().CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Contain("wondarr-20261127.json");
    }

    public void Dispose() => _directory.Dispose();

    private LogWritingHealthCheck CreateCheck() => new(_directory.Paths, _time, Start);

    private void WriteLog(string name, DateTimeOffset lastWritten)
    {
        Directory.CreateDirectory(_directory.Paths.LogsDir);
        var path = Path.Combine(_directory.Paths.LogsDir, name);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, lastWritten.UtcDateTime);
    }
}
