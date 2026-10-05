using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>Golden tests for <see cref="YtDlpOptionsValidator"/>.</summary>
public class YtDlpOptionsValidatorTests
{
    private readonly YtDlpOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_defaults()
    {
        _validator.Validate(null, new YtDlpOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void Holds_concurrency_at_one(int concurrency)
    {
        var result = _validator.Validate(null, new YtDlpOptions { Concurrency = concurrency });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.concurrency").And.Contain("YouTube");
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(61)]
    public void Rejects_an_out_of_range_sleep_requests(double seconds)
    {
        var result = _validator.Validate(null, new YtDlpOptions { SleepRequestsSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.sleep_requests_seconds").And.Contain("0.1 and 60");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void Rejects_an_out_of_range_sleep_interval(int seconds)
    {
        var result = _validator.Validate(null, new YtDlpOptions { SleepIntervalSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.sleep_interval_seconds").And.Contain("0 and 120");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void Rejects_an_out_of_range_max_sleep_interval(int seconds)
    {
        var result = _validator.Validate(null, new YtDlpOptions { MaxSleepIntervalSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.max_sleep_interval_seconds").And.Contain("0 and 120");
    }

    [Fact]
    public void Rejects_a_max_sleep_interval_below_the_interval()
    {
        var result = _validator.Validate(
            null,
            new YtDlpOptions { SleepIntervalSeconds = 20, MaxSleepIntervalSeconds = 10 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("at least sleep_interval_seconds");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3601)]
    public void Rejects_an_out_of_range_timeout(int seconds)
    {
        var result = _validator.Validate(null, new YtDlpOptions { TimeoutSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.timeout_seconds").And.Contain("30 and 3600");
    }

    [Fact]
    public void Rejects_an_empty_binary_path()
    {
        var result = _validator.Validate(null, new YtDlpOptions { BinaryPath = " " });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.binary_path");
    }

    [Fact]
    public void Accepts_the_sane_bounds()
    {
        var options = new YtDlpOptions
        {
            TimeoutSeconds = 3600,
            SleepRequestsSeconds = 0.1,
            SleepIntervalSeconds = 0,
            MaxSleepIntervalSeconds = 120,
            Retries = 20,
            Concurrency = 1,
        };

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
}
