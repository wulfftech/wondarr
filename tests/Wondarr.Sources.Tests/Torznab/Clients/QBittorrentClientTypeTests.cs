using System.Text.Json;
using Wondarr.Sources.Torznab.Clients;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Tests of the qBittorrent client type: the settings rules and the connection test.</summary>
public sealed class QBittorrentClientTypeTests
{
    [Fact]
    public void The_type_serves_torrents_and_describes_its_fields()
    {
        var type = new QBittorrentHarness().Type;

        type.Type.Should().Be("qbittorrent");
        type.Protocol.Should().Be(Wondarr.Core.Sources.DownloadProtocol.Torrent);
        type.Fields.Select(field => field.Name).Should().Equal(
            "host", "port", "useSsl", "urlBase", "username", "password", "category", "remotePathMappings");

        var password = type.Fields.Should().Contain(field => field.Name == "password").Subject;
        password.Secret.Should().BeTrue();
        password.Type.Should().Be("password");

        type.Fields.Should().Contain(field => field.Name == "remotePathMappings").Subject.Type.Should().Be("keyValueList");
    }

    [Fact]
    public void Settings_without_a_host_are_rejected()
    {
        var type = new QBittorrentHarness().Type;

        type.Validate(Json("""{"port":8080}""")).Should().Contain("A host is required.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Ports_outside_1_to_65535_are_rejected(int port)
    {
        var type = new QBittorrentHarness().Type;

        type.Validate(Json("""{"host":"127.0.0.1","port":""" + port + "}"))
            .Should().Contain("The port must be between 1 and 65535.");
    }

    [Fact]
    public void A_category_with_a_slash_is_rejected()
    {
        var type = new QBittorrentHarness().Type;

        type.Validate(Json("""{"host":"127.0.0.1","category":"a/b"}"""))
            .Should().Contain("The category must not contain '/'.");
    }

    [Fact]
    public void A_mapping_side_that_is_not_absolute_is_rejected()
    {
        var type = new QBittorrentHarness().Type;

        var messages = type.Validate(Json(
            """{"host":"127.0.0.1","remotePathMappings":[{"key":"downloads","value":"/mnt/media"},{"key":"/downloads","value":"media"}]}"""));

        messages.Should().Contain("The remote path 'downloads' of a remote path mapping must be absolute.");
        messages.Should().Contain("The local path 'media' of a remote path mapping must be absolute.");
    }

    [Fact]
    public void Good_settings_validate_cleanly()
    {
        var type = new QBittorrentHarness().Type;

        type.Validate(Json(
            """{"host":"127.0.0.1","port":8080,"category":"wondarr","remotePathMappings":[{"key":"/downloads","value":"/mnt/media"}]}"""))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task The_test_creates_a_missing_category_and_passes_when_the_save_path_is_visible()
    {
        var harness = new QBittorrentHarness();
        using var visible = TempDirectory.Create();

        harness.Fake.SavePath = "/downloads";
        var settings = Json(
            "{\"host\":\"127.0.0.1\",\"port\":8080,\"username\":\"admin\",\"password\":\"fixture-password-1234\"," +
            "\"remotePathMappings\":[{\"key\":\"/downloads\",\"value\":" + JsonSerializer.Serialize(visible.DirectoryPath) + "}]}");

        var result = await harness.Type.TestAsync(settings, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        harness.Fake.Categories.Should().ContainKey("wondarr");
        harness.Fake.Requests.Should().Contain(request => request.Path == "/api/v2/torrents/createCategory");
    }

    [Fact]
    public async Task The_test_fails_with_the_mapping_hint_when_the_save_path_is_not_visible()
    {
        var harness = new QBittorrentHarness();
        harness.Fake.SavePath = "/downloads/wondarr";
        harness.Fake.Categories["wondarr"] = "/downloads/wondarr";

        var result = await harness.Type.TestAsync(Json(QBittorrentHarness.SettingsJson), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(
            "qBittorrent saves to /downloads/wondarr, which Wondarr sees as /downloads/wondarr — that folder does not exist here; add a remote path mapping.");
    }

    [Fact]
    public async Task The_test_fails_when_the_client_is_too_old()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithVersion("2.8.3"));

        var result = await harness.Type.TestAsync(Json(QBittorrentHarness.SettingsJson), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("qBittorrent 4.5 or newer is needed");
    }

    [Fact]
    public async Task The_test_fails_without_naming_the_password_when_the_credentials_are_wrong()
    {
        var harness = new QBittorrentHarness();

        var result = await harness.Type.TestAsync(
            Json("""{"host":"127.0.0.1","port":8080,"username":"admin","password":"a-wrong-fixture-password"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Wrong qBittorrent username or password.");
        result.Error.Should().NotContain("a-wrong-fixture-password");
    }

    private static JsonElement Json(string json) => Wondarr.Core.Notifications.NotificationSecrets.Read(json);

    /// <summary>A temporary directory, removed when the test is done.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => DirectoryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "wondarr-test-" + Guid.NewGuid().ToString("N"));

        public string DirectoryPath { get; }

        public static TempDirectory Create()
        {
            var directory = new TempDirectory();
            System.IO.Directory.CreateDirectory(directory.DirectoryPath);

            return directory;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(DirectoryPath, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing the test for.
            }
        }
    }
}
