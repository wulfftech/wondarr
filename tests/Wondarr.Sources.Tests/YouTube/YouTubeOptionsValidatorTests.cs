using Wondarr.Sources.YouTube;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

public class YouTubeOptionsValidatorTests
{
    private readonly YouTubeOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_defaults()
    {
        _validator.Validate(null, new YouTubeOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_search_limit_outside_the_bounds()
    {
        _validator.Validate(null, new YouTubeOptions { SearchLimit = 0 }).FailureMessage.Should()
            .Contain("youtube.search_limit: must be between 1 and 50 (was 0)");
        _validator.Validate(null, new YouTubeOptions { SearchLimit = 51 }).FailureMessage.Should()
            .Contain("youtube.search_limit: must be between 1 and 50 (was 51)");
        _validator.Validate(null, new YouTubeOptions { SearchLimit = 1 }).Succeeded.Should().BeTrue();
        _validator.Validate(null, new YouTubeOptions { SearchLimit = 50 }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_base_url_that_is_not_an_absolute_http_uri()
    {
        _validator.Validate(null, new YouTubeOptions { BaseUrl = "music.youtube.com" }).FailureMessage.Should()
            .Contain("youtube.base_url: must be an absolute http(s) URI (was music.youtube.com)");
        _validator.Validate(null, new YouTubeOptions { BaseUrl = "" }).FailureMessage.Should()
            .Contain("youtube.base_url: must be an absolute http(s) URI");
    }

    [Fact]
    public void Rejects_a_po_token_base_url_that_is_not_an_absolute_http_uri()
    {
        _validator.Validate(null, new YouTubeOptions { PoTokenBaseUrl = "localhost:4416" }).FailureMessage.Should()
            .Contain("youtube.po_token_base_url: must be an absolute http(s) URI (was localhost:4416)");
    }

    [Fact]
    public void Reports_every_failure_at_once()
    {
        var result = _validator.Validate(null, new YouTubeOptions { SearchLimit = 0, BaseUrl = "nope" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("youtube.search_limit");
        result.FailureMessage.Should().Contain("youtube.base_url");
    }
}
