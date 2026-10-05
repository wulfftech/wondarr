using Wondarr.Sources.YouTube;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// Golden tests for <see cref="YtDlpErrorTaxonomy"/> over the exact error strings re-verified in
/// <c>docs/research/research_youtube.md</c> §0.1, served from <c>tests/fixtures/ytdlp</c>.
/// </summary>
public class YtDlpErrorTaxonomyTests
{
    [Theory]
    [InlineData("bot-check.stderr", YtDlpErrorKind.BotCheck, YtDlpErrorAction.RetryLater)]
    [InlineData("rate-limited-page.stderr", YtDlpErrorKind.RateLimited, YtDlpErrorAction.RetryLater)]
    [InlineData("rate-limited-429.stderr", YtDlpErrorKind.RateLimited, YtDlpErrorAction.RetryLater)]
    [InlineData("geo-restricted.stderr", YtDlpErrorKind.GeoRestricted, YtDlpErrorAction.Blocklist)]
    [InlineData("age-gated.stderr", YtDlpErrorKind.AgeGated, YtDlpErrorAction.Blocklist)]
    [InlineData("age-gated-login.stderr", YtDlpErrorKind.AgeGated, YtDlpErrorAction.Blocklist)]
    [InlineData("private.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("unavailable.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("not-exist.stderr", YtDlpErrorKind.PrivateOrUnavailable, YtDlpErrorAction.Blocklist)]
    [InlineData("garbage.stderr", YtDlpErrorKind.Unknown, YtDlpErrorAction.RetryLater)]
    public void Classifies_the_recorded_error_strings(string fixture, YtDlpErrorKind kind, YtDlpErrorAction action)
    {
        var standardError = YtDlpFixtures.Read(fixture);

        YtDlpErrorTaxonomy.Classify(standardError).Should().Be(kind);
        YtDlpErrorTaxonomy.ActionFor(kind).Should().Be(action);
    }

    [Fact]
    public void Classifies_case_insensitively()
    {
        YtDlpErrorTaxonomy.Classify("error: HTTP ERROR 429: too many requests")
            .Should().Be(YtDlpErrorKind.RateLimited);
    }

    [Fact]
    public void Classifies_an_empty_stderr_as_unknown()
    {
        YtDlpErrorTaxonomy.Classify(string.Empty).Should().Be(YtDlpErrorKind.Unknown);
    }
}
