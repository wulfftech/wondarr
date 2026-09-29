using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdConfigRendererTests
{
    private readonly SlskdConfigRenderer _renderer = new();

    [Fact]
    public void Renders_the_no_credentials_golden_file()
    {
        // The default folder is applied by SoulseekOptionsPostConfigure, not by the property, so the
        // renderer has to be handed the options it would really see.
        var options = new SoulseekOptions { Username = null, Password = null, SharedFolders = ["/data/music"] };

        var yaml = _renderer.Render(options, SlskdTestData.Secrets, webhookUrl: null);

        SlskdTestData.AssertMatchesGolden("render-no-credentials.yml", yaml);
        yaml.Should().Contain("no_connect: true");
    }

    [Fact]
    public void Omits_the_soulseek_credentials_when_none_are_configured()
    {
        var options = new SoulseekOptions { Username = null, Password = null };

        var soulseek = SlskdTestData.Section(_renderer.Render(options, SlskdTestData.Secrets, webhookUrl: null), "soulseek");

        soulseek.Should().NotContainKey("username");
        soulseek.Should().NotContainKey("password");
    }

    [Fact]
    public void Renders_the_credentials_and_sharing_golden_file()
    {
        var options = new SoulseekOptions
        {
            Username = "wondarr-soulseek",
            Password = "hunter2-not-a-real-password",
            SharedFolders = ["/data/media/music", "/data/media/music-old"],
        };

        var yaml = _renderer.Render(options, SlskdTestData.Secrets, webhookUrl: null);

        SlskdTestData.AssertMatchesGolden("render-credentials-sharing.yml", yaml);
        yaml.Should().Contain("no_connect: false");
        SlskdTestData.Section(yaml, "shares")["directories"].Should().BeEquivalentTo(
            new List<object> { "/data/media/music", "/data/media/music-old" }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Renders_the_no_sharing_no_distributed_unlimited_golden_file()
    {
        var options = new SoulseekOptions
        {
            Username = "wondarr-soulseek",
            Password = "hunter2-not-a-real-password",
            ShareLibrary = false,
            DistributedNetwork = false,
            UploadSpeedLimitKib = 0,
        };

        var yaml = _renderer.Render(options, SlskdTestData.Secrets, webhookUrl: null);

        SlskdTestData.AssertMatchesGolden("render-no-sharing-no-distributed-unlimited.yml", yaml);
        yaml.Should().Contain("speed_limit: 0");
        ((List<object>)SlskdTestData.Section(yaml, "shares")["directories"]).Should().BeEmpty();
    }

    [Fact]
    public void Renders_the_webhook_golden_file_when_a_callback_url_is_given()
    {
        const string Url = "http://127.0.0.1:1077/api/v1/slskd/webhook";

        // The shared-folder default is applied by post-configuration, not by the options class.
        var yaml = _renderer.Render(new SoulseekOptions { SharedFolders = ["/data/music"] }, SlskdTestData.Secrets, Url);

        SlskdTestData.AssertMatchesGolden("render-webhook.yml", yaml);

        var webhook = Webhook(yaml);
        ((List<object>)webhook["on"]).Should().Equal(SlskdConfigRenderer.WebhookEvent);
        webhook["timeout"].Should().Be(SlskdConfigRenderer.WebhookTimeoutMs.ToString());

        var call = (Dictionary<object, object>)webhook["call"];
        call["url"].Should().Be(Url);

        var header = ((List<object>)call["headers"]).Cast<Dictionary<object, object>>().Should().ContainSingle().Subject;
        header["name"].Should().Be(SlskdConfigRenderer.WebhookHeaderName);
        header["value"].Should().Be(SlskdTestData.Secrets.WebhookToken);

        ((Dictionary<object, object>)webhook["retry"])["attempts"]
            .Should().Be(SlskdConfigRenderer.WebhookAttempts.ToString());
    }

    [Fact]
    public void Leaves_the_webhook_out_entirely_when_no_callback_url_is_given()
    {
        var yaml = _renderer.Render(new SoulseekOptions(), SlskdTestData.Secrets, webhookUrl: null);

        SlskdTestData.Parse(yaml).Should().NotContainKey("integrations");
    }

    /// <summary>The <c>integrations.webhooks.wondarr</c> section of a rendered document.</summary>
    private static Dictionary<object, object> Webhook(string yaml) =>
        (Dictionary<object, object>)((Dictionary<object, object>)SlskdTestData
            .Section(yaml, "integrations")["webhooks"])[SlskdConfigRenderer.WebhookName];

    [Theory]
    [InlineData("render-no-credentials.yml")]
    [InlineData("render-credentials-sharing.yml")]
    [InlineData("render-no-sharing-no-distributed-unlimited.yml")]
    public void Binds_loopback_and_disables_https_in_every_render(string fixture)
    {
        var yaml = SlskdTestData.ReadFixture(fixture);
        var web = SlskdTestData.Section(yaml, "web");

        yaml.Should().Contain("headless: true");
        yaml.Should().Contain("remote_configuration: false");
        web["ip_address"].Should().Be(SlskdConfigRenderer.LoopbackAddress);
        ((Dictionary<object, object>)web["https"])["disabled"].Should().Be("true");
    }

    [Fact]
    public void Writes_the_api_key_under_the_wondarr_name()
    {
        var yaml = _renderer.Render(new SoulseekOptions(), SlskdTestData.Secrets, webhookUrl: null);

        var authentication = (Dictionary<object, object>)SlskdTestData.Section(yaml, "web")["authentication"];
        var apiKeys = (Dictionary<object, object>)authentication["api_keys"];
        var key = (Dictionary<object, object>)apiKeys[SlskdConfigRenderer.ApiKeyName];

        key["key"].Should().Be(SlskdTestData.Secrets.ApiKey);
        key["role"].Should().Be("administrator");
        key["cidr"].Should().Be(SlskdConfigRenderer.ApiKeyCidr);
    }

    [Fact]
    public void Quotes_values_that_would_otherwise_break_the_document()
    {
        var options = new SoulseekOptions
        {
            Username = "user: with colon",
            Password = "pass\"quote",
        };

        var yaml = _renderer.Render(options, SlskdTestData.Secrets, webhookUrl: null);
        var soulseek = SlskdTestData.Section(yaml, "soulseek");

        soulseek["username"].Should().Be("user: with colon");
        soulseek["password"].Should().Be("pass\"quote");
    }
}
