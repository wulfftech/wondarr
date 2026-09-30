using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Notifications;
using Wondarr.Core.Notifications.Discord;
using Xunit;

namespace Wondarr.Core.Tests.Notifications;

/// <summary>
/// The Discord provider: the embed each event becomes, and how the post is sent. The webhook is a fake
/// handler, so nothing here touches the network.
/// </summary>
public class DiscordTests
{
    private const string WebHookUrl = "https://discord.com/api/webhooks/123/token";

    private static readonly JsonElement Settings = Parse($$"""{"webHookUrl":"{{WebHookUrl}}"}""");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_grab_posts_a_blue_embed_carrying_the_song_and_the_release()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("grab", "Grabbed: Oasis – Wonderwall", "Wonderwall.flac from soulseek (FLAC)")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", "(What's the Story) Morning Glory?", "mb-1"),
            Release = new NotificationRelease("Wonderwall.flac", "soulseek", "FLAC", 30_000_000),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        var embed = handler.Payloads.Single().GetProperty("embeds")[0];

        embed.GetProperty("title").GetString().Should().Be("Grabbed: Oasis – Wonderwall");
        embed.GetProperty("description").GetString().Should().Be("Wonderwall.flac from soulseek (FLAC)");
        embed.GetProperty("color").GetInt32().Should().Be(16761392);
        embed.GetProperty("author").GetProperty("name").GetString().Should().Be("Wondarr");
        embed.GetProperty("timestamp").GetString().Should().StartWith("2026-09-30T12:00:00");

        var fields = Fields(embed);

        fields["Artist"].Should().Be("Oasis");
        fields["Album"].Should().Be("(What's the Story) Morning Glory?");
        fields["Quality"].Should().Be("FLAC");
        fields["Source"].Should().Be("soulseek");
        fields["Size"].Should().Be("28.6 MB");

        handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
        handler.Requests.Single().Url.Should().Be(WebHookUrl);
        handler.Requests.Single().ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task An_import_posts_a_green_embed_with_the_files_quality()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("import", "Imported: Oasis – Wonderwall", "FLAC → /music/Oasis/Wonderwall.flac")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
            File = new NotificationFile("/music/Oasis/Wonderwall.flac", "FLAC"),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        var embed = handler.Payloads.Single().GetProperty("embeds")[0];

        embed.GetProperty("color").GetInt32().Should().Be(2605644);
        Fields(embed)["Quality"].Should().Be("FLAC");
    }

    [Fact]
    public async Task An_upgrade_posts_a_green_embed_marked_as_an_upgrade()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("upgrade", "Upgraded: Oasis – Wonderwall", "FLAC → /music/Oasis/Wonderwall.flac")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
            File = new NotificationFile("/music/Oasis/Wonderwall.flac", "FLAC"),
            IsUpgrade = true,
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        var embed = handler.Payloads.Single().GetProperty("embeds")[0];

        embed.GetProperty("color").GetInt32().Should().Be(2605644);
        Fields(embed)["Upgrade"].Should().Be("Yes");
    }

    [Fact]
    public async Task A_failure_posts_a_red_embed()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("failure", "Failed: Oasis – Wonderwall", "the peer went offline")
        {
            Song = new NotificationSong(7, "Wonderwall", "Oasis", null, null),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        var embed = handler.Payloads.Single().GetProperty("embeds")[0];

        embed.GetProperty("color").GetInt32().Should().Be(15749200);
        embed.GetProperty("description").GetString().Should().Be("the peer went offline");
    }

    [Fact]
    public async Task A_health_warning_posts_an_orange_embed()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("health", "Health: DownloadFolder", "the download folder is gone")
        {
            Health = new NotificationHealth("DownloadFolder", "warning", "the download folder is gone", null),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        handler.Payloads.Single().GetProperty("embeds")[0].GetProperty("color").GetInt32().Should().Be(16753920);
    }

    [Fact]
    public async Task A_health_error_posts_a_red_embed()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("health", "Health: DownloadFolder", "the download folder is gone")
        {
            Health = new NotificationHealth("DownloadFolder", "error", "the download folder is gone", null),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        handler.Payloads.Single().GetProperty("embeds")[0].GetProperty("color").GetInt32().Should().Be(15749200);
    }

    [Fact]
    public async Task A_test_post_is_a_blue_embed_with_the_configured_author()
    {
        var (provider, handler) = CreateProvider();
        var settings = Parse($$"""{"webHookUrl":"{{WebHookUrl}}","author":"Wondarr Test","username":"oasis","avatar":"https://img.local/a.png"}""");

        await provider.SendAsync(TestMessage(), settings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.GetProperty("username").GetString().Should().Be("oasis");
        payload.GetProperty("avatar_url").GetString().Should().Be("https://img.local/a.png");
        payload.GetProperty("embeds")[0].GetProperty("color").GetInt32().Should().Be(16761392);
        payload.GetProperty("embeds")[0].GetProperty("author").GetProperty("name").GetString().Should().Be("Wondarr Test");
    }

    [Fact]
    public async Task An_unset_username_and_avatar_are_left_out_of_the_body()
    {
        var (provider, handler) = CreateProvider();

        await provider.SendAsync(TestMessage(), Settings, CancellationToken.None);

        var payload = handler.Payloads.Single();

        payload.TryGetProperty("username", out _).Should().BeFalse();
        payload.TryGetProperty("avatar_url", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Parts_longer_than_discord_accepts_are_cut_to_fit_rather_than_losing_the_message()
    {
        var (provider, handler) = CreateProvider();
        var message = new NotificationMessage("failure", "Failed: " + new string('t', 300), new string('b', 5000))
        {
            Song = new NotificationSong(7, "Wonderwall", new string('a', 1500), null, null),
        };

        await provider.SendAsync(message, Settings, CancellationToken.None);

        var embed = handler.Payloads.Single().GetProperty("embeds")[0];

        embed.GetProperty("title").GetString()!.Length.Should().Be(256);
        embed.GetProperty("title").GetString().Should().EndWith("…");
        embed.GetProperty("description").GetString()!.Length.Should().Be(4096);
        embed.GetProperty("fields")[0].GetProperty("value").GetString()!.Length.Should().Be(1024);
    }

    [Fact]
    public void A_url_that_is_not_a_discord_https_address_is_a_validation_message()
    {
        var (provider, _) = CreateProvider();

        provider.Validate(Settings).Should().BeEmpty();
        provider.Validate(Parse("""{"webHookUrl":"http://discord.com/api/webhooks/1/token"}""")).Should().NotBeEmpty();
        provider.Validate(Parse("""{"webHookUrl":"https://not-discord.example/api/webhooks/1/token"}""")).Should().NotBeEmpty();
        provider.Validate(Parse("""{"webHookUrl":"https://discord.com.evil.example/hook"}""")).Should().NotBeEmpty();
        provider.Validate(Parse("""{"webHookUrl":"https://discord.com@evil.example/api/webhooks/1/token"}""")).Should().NotBeEmpty();
        provider.Validate(Parse("{}")).Should().NotBeEmpty();

        // The message must say what is wrong without repeating the URL, which carries the token.
        provider.Validate(Parse("""{"webHookUrl":"https://not-discord.example/api/webhooks/1/token"}"""))
            .Should().OnlyContain(message => !message.Contains("not-discord.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_429_with_a_short_Retry_After_is_waited_out_and_sent_once_more()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = attempt => attempt == 1
            ? Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2))
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        var send = provider.SendAsync(TestMessage(), Settings, CancellationToken.None);

        while (!send.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await send;

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_four_hundred_throws_a_failure_that_does_not_name_the_webhook()
    {
        var (provider, handler) = CreateProvider();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);

        var thrown = await Assert.ThrowsAsync<NotificationSendException>(
            () => provider.SendAsync(TestMessage(), Settings, CancellationToken.None));

        thrown.Message.Should().Contain("400");
        thrown.Message.Should().NotContain("discord.com");
        thrown.Message.Should().NotContain("token");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void The_webhook_url_field_is_a_secret_and_required_and_the_author_is_advanced()
    {
        var (provider, _) = CreateProvider();

        provider.Implementation.Should().Be("Discord");
        provider.Fields.Single(field => field.Name == "webHookUrl").Secret.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "webHookUrl").Required.Should().BeTrue();
        provider.Fields.Single(field => field.Name == "author").Advanced.Should().BeTrue();
        provider.Fields.Select(field => field.Name)
            .Should().Equal("webHookUrl", "username", "avatar", "author");
    }

    private static NotificationMessage TestMessage() =>
        new(NotificationEventNames.Test, "Test notification from Wondarr", "nothing else was sent");

    private static Dictionary<string, string> Fields(JsonElement embed)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in embed.GetProperty("fields").EnumerateArray())
        {
            fields[field.GetProperty("name").GetString()!] = field.GetProperty("value").GetString()!;
        }

        return fields;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Retry(HttpStatusCode status, TimeSpan wait)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);

        return response;
    }

    private (DiscordProvider Provider, NotificationTestHandler Handler) CreateProvider()
    {
        var handler = new NotificationTestHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NotificationHttp.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return (new DiscordProvider(factory, _time, NullLogger<DiscordProvider>.Instance), handler);
    }
}
