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
    public void Redacts_the_json_escaped_form_of_a_secret_with_a_quote_and_a_backslash()
    {
        const string secret = "pa\\ss\"word";
        var registry = new SecretRegistry();
        registry.Register(secret);

        // As the JSON log writes it: \ becomes \\ and " becomes \".
        registry.Redact("{\"Exception\":\"login failed for pa\\\\ss\\\"word\"}")
            .Should().Be("{\"Exception\":\"login failed for (removed)\"}");

        registry.Redact("raw form pa\\ss\"word").Should().Be("raw form (removed)");
    }

    [Fact]
    public void Redacts_a_secret_with_a_quote_and_a_backslash_in_a_rendered_json_event()
    {
        const string secret = "pa\\ss\"word";
        var registry = new SecretRegistry();
        registry.Register(secret);

        var logEvent = new Serilog.Events.LogEvent(
            DateTimeOffset.UtcNow,
            Serilog.Events.LogEventLevel.Error,
            null,
            new Serilog.Parsing.MessageTemplateParser().Parse("Login failed for {Password}"),
            [new Serilog.Events.LogEventProperty("Password", new Serilog.Events.ScalarValue(secret))]);

        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new RedactingTextFormatter(new Serilog.Formatting.Json.JsonFormatter(), registry).Format(logEvent, output);

        output.ToString().Should().NotContain("ss\\\"word").And.NotContain("pa\\\\ss");
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
