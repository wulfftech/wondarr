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
    public void The_escaped_form_matches_what_serilogs_json_formatter_writes_for_control_characters()
    {
        const string secret = "tab\t-nl\n-ff\f-bs\b-bell\u0007-end\"\\";
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

        output.ToString().Should().Contain("(removed)").And.NotContain("tab").And.NotContain("bell");
    }

    [Fact]
    public void Replaces_a_longer_secret_before_a_shorter_one_registered_later()
    {
        var registry = new SecretRegistry();
        registry.Register("0123456789abcdef");
        registry.Register("0123456789abcdef0123");

        registry.Redact("key 0123456789abcdef0123").Should().Be("key (removed)");
    }

    // A key echoed by an indexer that refuses a request: in free text, and as a query string
    // (the parameter is called k, which the cleansing rules do not know, so only the registry can
    // remove what follows it).
    private static void ShouldBeRedactedInSurroundingText(string secret, string form)
    {
        var registry = new SecretRegistry();
        registry.Register(secret);

        registry.Redact($"The indexer said: bad key {form} for this user.")
            .Should().Be("The indexer said: bad key (removed) for this user.", $"'{form}' is a form of the secret");
        registry.Redact($"Bad request: /api?t=get&k={form}&q=x")
            .Should().Be("Bad request: /api?t=get&k=(removed)&q=x", $"'{form}' is a form of the secret");
    }

    [Theory]
    [InlineData("Ab+c/d=Ef12")]
    [InlineData("Ab%2Bc%2Fd%3DEf12")]
    [InlineData("Ab%2bc%2fd%3dEf12")]
    public void Redacts_a_key_with_a_plus_a_slash_and_an_equals_sign_as_it_is_and_percent_encoded_in_either_case(string form)
    {
        ShouldBeRedactedInSurroundingText("Ab+c/d=Ef12", form);
    }

    [Theory]
    [InlineData("Ab c+d=Ef12")]
    [InlineData("Ab%20c%2Bd%3DEf12")]
    [InlineData("Ab%20c%2bd%3dEf12")]
    [InlineData("Ab+c%2Bd%3DEf12")]
    [InlineData("Ab+c%2bd%3dEf12")]
    public void Redacts_a_key_with_a_space_percent_encoded_and_with_a_plus_for_the_space(string form)
    {
        ShouldBeRedactedInSurroundingText("Ab c+d=Ef12", form);
    }

    // Uri.EscapeDataString and WebUtility.UrlEncode disagree about these characters: the first
    // encodes ! and * and leaves ~, the second does the reverse.
    [Theory]
    [InlineData("Ab~c!d*Ef12")]
    [InlineData("Ab~c%21d%2AEf12")]
    [InlineData("Ab~c%21d%2aEf12")]
    [InlineData("Ab%7Ec!d*Ef12")]
    [InlineData("Ab%7ec!d*Ef12")]
    public void Redacts_a_key_in_the_form_each_percent_encoder_writes_it(string form)
    {
        ShouldBeRedactedInSurroundingText("Ab~c!d*Ef12", form);
    }

    [Theory]
    [InlineData("Ab&c<d>\"Ef'12")]
    [InlineData("Ab&amp;c&lt;d&gt;&quot;Ef&#39;12")]
    [InlineData("Ab%26c%3Cd%3E%22Ef%2712")]
    public void Redacts_a_key_with_html_special_characters_as_it_is_and_html_or_percent_encoded(string form)
    {
        ShouldBeRedactedInSurroundingText("Ab&c<d>\"Ef'12", form);
    }

    [Theory]
    [InlineData("Ab\\c&d=Ef12")]
    [InlineData("Ab\\\\c&d=Ef12")]
    [InlineData("Ab\\c&amp;d=Ef12")]
    [InlineData("Ab\\\\c&amp;d=Ef12")]
    [InlineData("Ab%5Cc%26d%3DEf12")]
    public void Redacts_a_key_with_a_backslash_as_the_json_log_writes_each_of_its_forms(string form)
    {
        ShouldBeRedactedInSurroundingText("Ab\\c&d=Ef12", form);
    }

    [Fact]
    public void Redacts_the_html_encoded_form_of_a_key_in_a_rendered_json_event()
    {
        const string secret = "Ab\\c&d=Ef12";
        var registry = new SecretRegistry();
        registry.Register(secret);

        var logEvent = new Serilog.Events.LogEvent(
            DateTimeOffset.UtcNow,
            Serilog.Events.LogEventLevel.Error,
            null,
            new Serilog.Parsing.MessageTemplateParser().Parse("Indexer refused: {Text}"),
            [new Serilog.Events.LogEventProperty("Text", new Serilog.Events.ScalarValue("bad key Ab\\c&amp;d=Ef12 for this user"))]);

        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new RedactingTextFormatter(new Serilog.Formatting.Json.JsonFormatter(), registry).Format(logEvent, output);

        output.ToString().Should().NotContain("Ef12").And.Contain("bad key (removed) for this user");
    }

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData("Qq9Zz0Xx8Ww7Vv6U")]
    public void A_plain_alphanumeric_key_is_redacted_exactly_as_before(string secret)
    {
        var registry = new SecretRegistry();
        registry.Register(secret);

        registry.Redact(secret).Should().Be("(removed)");
        registry.Redact($"left {secret} right {secret}").Should().Be("left (removed) right (removed)");
        registry.Redact($"left {secret.ToUpperInvariant()} right").Should().Be($"left {secret.ToUpperInvariant()} right", "only the exact text is a secret");
    }

    [Fact]
    public void A_short_secret_is_ignored_even_though_its_encoded_form_is_long_enough()
    {
        var registry = new SecretRegistry();
        registry.Register("a+b=c");

        registry.Redact("echo a%2Bb%3Dc and a+b=c").Should().Be("echo a%2Bb%3Dc and a+b=c");
    }
}
