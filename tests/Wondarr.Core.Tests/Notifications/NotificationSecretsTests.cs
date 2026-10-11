using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Notifications;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The two rules that keep a stored secret from leaving with a request it does not belong to: it is
/// reused only for the target it was saved for (R-038), and it is masked even when the type that
/// would say which keys are secret is gone (owner question 4).
/// </summary>
public sealed class NotificationSecretsTests
{
    /// <summary>The fields of a client that splits its address into host, port and SSL, as qBittorrent does.</summary>
    private static readonly IReadOnlyList<NotificationField> SplitFields =
    [
        new("host", "Host", "text", Required: true),
        new("port", "Port", "number", Required: false),
        new("useSsl", "Use SSL", "checkbox", Required: false),
        new("urlBase", "URL base", "text", Required: false),
        new("username", "Username", "text", Required: false),
        new("password", "Password", "password", Required: false, Secret: true),
    ];

    [Fact]
    public void A_changed_host_port_or_ssl_flag_is_another_target()
    {
        const string stored = """{"host":"qbit.local","port":8080,"useSsl":false,"password":"wonderwall"}""";

        Refused("""{"host":"attacker.example","port":8080,"useSsl":false,"password":"********"}""", stored).Should().BeTrue();
        Refused("""{"host":"qbit.local","port":9090,"useSsl":false,"password":"********"}""", stored).Should().BeTrue();
        Refused("""{"host":"qbit.local","port":8080,"useSsl":true,"password":"********"}""", stored).Should().BeTrue();
    }

    [Fact]
    public void A_changed_url_base_username_or_letter_case_is_the_same_target()
    {
        const string stored = """{"host":"qbit.local","port":8080,"useSsl":false,"urlBase":"/a","username":"me","password":"wonderwall"}""";

        Refused("""{"host":"QBIT.local","port":8080,"useSsl":false,"urlBase":"/b","username":"you","password":"********"}""", stored)
            .Should().BeFalse();
    }

    [Fact]
    public void A_new_row_has_nothing_stored_so_the_mask_stands_for_nothing()
    {
        var merged = NotificationSecrets.Merge(
            NotificationSecrets.Read("""{"host":"qbit.local","password":"********"}"""),
            stored: null,
            SplitFields);

        JsonDocument.Parse(merged).RootElement.TryGetProperty("password", out _).Should().BeFalse();
    }

    [Fact]
    public void A_stored_row_with_no_secret_needs_no_reentry_for_a_new_host()
    {
        const string stored = """{"host":"qbit.local","port":8080,"password":""}""";

        Refused("""{"host":"attacker.example","port":8080,"password":"********"}""", stored).Should().BeFalse();
    }

    [Fact]
    public void The_mask_is_not_the_secret_and_reentry_names_the_field_and_not_the_value()
    {
        var failure = Assert.Throws<SecretReentryException>(() => NotificationSecrets.Merge(
            NotificationSecrets.Read("""{"host":"attacker.example","password":"********"}"""),
            """{"host":"qbit.local","password":"wonderwall"}""",
            SplitFields));

        failure.Field.Should().Be("password");
        failure.Message.Should().Be("Enter the Password again: the address changed.");
        failure.Message.Should().NotContain("wonderwall");
    }

    [Fact]
    public void Settings_of_a_type_that_is_gone_are_masked_by_the_names_that_suggest_a_secret()
    {
        var settings = NotificationSecrets.Read(
            """
            {
              "url": "http://tracker.local",
              "apiKey": "k1", "API_TOKEN": "t1", "Password": "p1", "clientSecret": "s1",
              "passkey": "pk", "sessionCookie": "c1", "authHeader": "a1",
              "category": "music", "priority": 5, "emptyKey": ""
            }
            """);

        var masked = NotificationSecrets.Masked(settings, fields: null);

        foreach (var name in new[] { "apiKey", "API_TOKEN", "Password", "clientSecret", "passkey", "sessionCookie", "authHeader" })
        {
            masked[name]!.GetValue<string>().Should().Be(NotificationSecrets.Mask, name);
        }

        masked["url"]!.GetValue<string>().Should().Be("http://tracker.local");
        masked["category"]!.GetValue<string>().Should().Be("music");
        masked["priority"]!.GetValue<int>().Should().Be(5);
        masked["emptyKey"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public void A_known_types_fields_still_decide_what_is_secret()
    {
        var settings = NotificationSecrets.Read("""{"apiKey":"k1","password":"p1"}""");

        var masked = NotificationSecrets.Masked(settings, [new NotificationField("password", "Password", "password", false, Secret: true)]);

        masked["password"]!.GetValue<string>().Should().Be(NotificationSecrets.Mask);
        masked["apiKey"]!.GetValue<string>().Should().Be("k1");
    }

    private static bool Refused(string incoming, string stored)
    {
        try
        {
            NotificationSecrets.Merge(NotificationSecrets.Read(incoming), stored, SplitFields);

            return false;
        }
        catch (SecretReentryException)
        {
            return true;
        }
    }
}
