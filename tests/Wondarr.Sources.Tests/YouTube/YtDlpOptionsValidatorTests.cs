using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>Golden tests for <see cref="YtDlpOptionsValidator"/>.</summary>
public class YtDlpOptionsValidatorTests
{
    private readonly YtDlpOptionsValidator _validator = new();

    [Fact]
    public void Validates_through_the_service_collection()
    {
        // The options pipeline only asks validators about the type it resolves: the validator must
        // be registered against the bound YouTubeOptions type, not the nested YtDlpOptions.
        var services = new ServiceCollection();
        services.AddWondarrYouTube();
        services.AddOptions<YouTubeOptions>().Configure(
            options => options.Ytdlp.Concurrency = 5);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<YouTubeOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("youtube.ytdlp.concurrency");
    }

    [Fact]
    public void Accepts_the_defaults_through_the_service_collection()
    {
        var services = new ServiceCollection();
        services.AddWondarrYouTube();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<YouTubeOptions>>().Value.Ytdlp.Concurrency
            .Should().Be(1);
    }

    [Fact]
    public void Accepts_the_defaults()
    {
        _validator.Validate(null, new YouTubeOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void Holds_concurrency_at_one(int concurrency)
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { Concurrency = concurrency } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.concurrency").And.Contain("YouTube");
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(61)]
    public void Rejects_an_out_of_range_sleep_requests(double seconds)
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { SleepRequestsSeconds = seconds } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.sleep_requests_seconds").And.Contain("0.1 and 60");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void Rejects_an_out_of_range_sleep_interval(int seconds)
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { SleepIntervalSeconds = seconds } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.sleep_interval_seconds").And.Contain("0 and 120");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void Rejects_an_out_of_range_max_sleep_interval(int seconds)
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { MaxSleepIntervalSeconds = seconds } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.max_sleep_interval_seconds").And.Contain("0 and 120");
    }

    [Fact]
    public void Rejects_a_max_sleep_interval_below_the_interval()
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions
            {
                Ytdlp = new YtDlpOptions { SleepIntervalSeconds = 20, MaxSleepIntervalSeconds = 10 },
            });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("at least sleep_interval_seconds");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3601)]
    public void Rejects_an_out_of_range_timeout(int seconds)
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { TimeoutSeconds = seconds } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.timeout_seconds").And.Contain("30 and 3600");
    }

    [Fact]
    public void Rejects_an_empty_binary_path()
    {
        var result = _validator.Validate(
            null,
            new YouTubeOptions { Ytdlp = new YtDlpOptions { BinaryPath = " " } });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.ytdlp.binary_path");
    }

    [Fact]
    public void Accepts_the_sane_bounds()
    {
        var options = new YouTubeOptions
        {
            Ytdlp = new YtDlpOptions
            {
                TimeoutSeconds = 3600,
                SleepRequestsSeconds = 0.1,
                SleepIntervalSeconds = 0,
                MaxSleepIntervalSeconds = 120,
                Retries = 20,
                Concurrency = 1,
            },
        };

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
}
