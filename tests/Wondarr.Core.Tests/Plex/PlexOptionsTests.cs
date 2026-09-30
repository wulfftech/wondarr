using Wondarr.Core.Plex;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public class PlexOptionsTests
{
    [Fact]
    public void The_defaults_validate()
    {
        new PlexOptionsValidator().Validate(null, new PlexOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("plex.tv")]
    [InlineData("/auth")]
    [InlineData("ftp://plex.tv/auth")]
    [InlineData("")]
    public void Rejects_a_base_url_that_is_not_absolute_http_or_https(string value)
    {
        var result = new PlexOptionsValidator().Validate(null, new PlexOptions { PlexTvBaseUrl = value });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.StartsWith("plex.plex_tv_base_url", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_auth_url_that_is_not_absolute_http_or_https()
    {
        var result = new PlexOptionsValidator().Validate(null, new PlexOptions { AppAuthUrl = "app.plex.tv/auth" });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.StartsWith("plex.app_auth_url", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(121)]
    public void Rejects_a_timeout_outside_the_allowed_range(int seconds)
    {
        var result = new PlexOptionsValidator().Validate(null, new PlexOptions { RequestTimeoutSeconds = seconds });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.StartsWith("plex.request_timeout_seconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(120)]
    public void Accepts_a_timeout_at_the_edge_of_the_range(int seconds)
    {
        new PlexOptionsValidator()
            .Validate(null, new PlexOptions { RequestTimeoutSeconds = seconds })
            .Succeeded.Should().BeTrue();
    }
}
