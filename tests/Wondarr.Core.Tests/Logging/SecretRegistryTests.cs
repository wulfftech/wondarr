using Wondarr.Core.Logging;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Logging;

public class SecretRegistryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcde")]
    public void Ignores_missing_and_short_values(string? secret)
    {
        var registry = new SecretRegistry();

        registry.Register(secret);

        registry.Redact("abcde").Should().Be("abcde");
    }

    [Fact]
    public void Replaces_every_occurrence_of_a_registered_secret()
    {
        var registry = new SecretRegistry();
        registry.Register("0123456789abcdef");

        registry.Redact("left 0123456789abcdef right 0123456789abcdef")
            .Should().Be("left (removed) right (removed)");
    }

    [Fact]
    public void Falls_back_to_the_cleansing_rules_for_unregistered_secrets()
    {
        var registry = new SecretRegistry();

        registry.Redact("Calling ?apikey=unregistered").Should().Be("Calling ?apikey=(removed)");
    }

    [Fact]
    public void Replaces_a_longer_secret_before_a_shorter_one_registered_later()
    {
        var registry = new SecretRegistry();
        registry.Register("0123456789abcdef");
        registry.Register("0123456789abcdef0123");

        registry.Redact("key 0123456789abcdef0123").Should().Be("key (removed)");
    }
}
