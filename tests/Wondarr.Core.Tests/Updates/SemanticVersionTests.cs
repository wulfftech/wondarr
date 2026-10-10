using FluentAssertions;
using Wondarr.Core.Updates;
using Xunit;

namespace Wondarr.Core.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.2")]
    [InlineData("0.1.0-rc.2", "0.1.0")]
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-9", "1.0.0-a")]
    [InlineData("0.0.0-develop.5", "0.0.1")]
    [InlineData("0.9.9", "1.0.0")]
    public void Orders_by_semver_precedence(string lower, string higher)
    {
        var low = SemanticVersion.TryParse(lower)!;
        var high = SemanticVersion.TryParse(higher)!;

        (low < high).Should().BeTrue();
        (high > low).Should().BeTrue();
        low.CompareTo(high).Should().BeNegative();
        high.CompareTo(low).Should().BePositive();
    }

    [Fact]
    public void A_v_prefix_and_build_metadata_do_not_change_the_version()
    {
        var plain = SemanticVersion.TryParse("0.1.0-rc.1")!;
        var decorated = SemanticVersion.TryParse("v0.1.0-rc.1+abc1234")!;

        (decorated == plain).Should().BeTrue();
        decorated.ToString().Should().Be("0.1.0-rc.1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("nightly-2026-10-10")]
    public void Anything_that_is_not_semver_does_not_parse(string text)
    {
        SemanticVersion.TryParse(text).Should().BeNull();
    }

    [Theory]
    [InlineData("0.1.0", false)]
    [InlineData("0.1.0-rc.1+abc", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.0.0-develop.42+deadbeef", true)]
    [InlineData("0.0.0-develop.1", true)]
    [InlineData("nightly", true)]
    [InlineData("1.0", true)]
    public void A_development_build_is_develop_or_not_semver(string informational, bool development)
    {
        RunningVersion.Parse(informational).IsDevelopmentBuild.Should().Be(development);
    }

    [Fact]
    public void The_commit_suffix_is_dropped_from_the_running_version()
    {
        RunningVersion.Parse("0.1.0+abcdef").Text.Should().Be("0.1.0");
    }
}
