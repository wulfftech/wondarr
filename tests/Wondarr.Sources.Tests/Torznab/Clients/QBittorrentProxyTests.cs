using System.Net;
using Wondarr.Sources.Torznab.Clients;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Contract tests of the qBittorrent HTTP layer against the fake Web API.</summary>
public sealed class QBittorrentProxyTests
{
    private const string BaseUrl = "http://127.0.0.1:8080";

    [Fact]
    public async Task Logs_in_only_after_a_403_and_retries_the_request_once()
    {
        var harness = new QBittorrentHarness();

        var version = await harness.Proxy.GetApiVersionAsync(harness.Session, QBittorrentHarness.Settings, CancellationToken.None);

        version.ToString().Should().Be("2.11.0");
        harness.Fake.Requests.Select(request => request.Path).Should().Equal(
            "/api/v2/app/webapiVersion",
            "/api/v2/auth/login",
            "/api/v2/app/webapiVersion");

        // The session keeps the SID cookie, so the next call needs no new login.
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "downloading");
        await harness.Proxy.GetTorrentsAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        harness.Fake.Requests.Count(request => request.Path == "/api/v2/auth/login").Should().Be(1);
    }

    [Fact]
    public async Task Wrong_credentials_are_a_download_client_exception_that_names_no_password()
    {
        var harness = new QBittorrentHarness();
        var settings = QBittorrentHarness.Settings with { Password = "another-fixture-password" };

        var act = () => harness.Proxy.GetApiVersionAsync(harness.Session, settings, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<DownloadClientException>();

        exception.Which.Message.Should().Contain("Wrong qBittorrent username or password.");
        exception.Which.Message.Should().NotContain("another-fixture-password");
        exception.Which.Message.Should().NotContain(QbittorrentFake.Password);
    }

    [Fact]
    public async Task A_banned_address_is_reported_as_such()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithBannedAddress());

        var act = () => harness.Proxy.GetApiVersionAsync(harness.Session, QBittorrentHarness.Settings, CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>())
            .Which.Message.Should().Contain("banned this IP address");
    }

    [Fact]
    public async Task Every_request_carries_the_base_url_as_its_referer()
    {
        var harness = new QBittorrentHarness();
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "downloading");

        await harness.Proxy.GetTorrentsAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        harness.Fake.Requests.Should().OnlyContain(request => request.Referer == BaseUrl + "/");
    }

    [Fact]
    public async Task The_sid_cookie_is_sent_with_every_request_after_the_login()
    {
        var harness = new QBittorrentHarness();
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "downloading");

        await harness.Proxy.GetTorrentsAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        var afterLogin = harness.Fake.Requests.SkipWhile(request => request.Path != "/api/v2/auth/login").Skip(1);
        afterLogin.Should().OnlyContain(request => request.Cookie != null && request.Cookie.Contains("SID="));
    }

    [Fact]
    public async Task A_client_that_needs_no_login_works_without_one()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithoutAuthentication());
        var settings = QBittorrentHarness.Settings with { Username = string.Empty, Password = string.Empty };

        var version = await harness.Proxy.GetApiVersionAsync(harness.Session, settings, CancellationToken.None);

        version.ToString().Should().Be("2.11.0");
        harness.Fake.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/v2/app/webapiVersion");
    }

    [Fact]
    public async Task A_torrent_file_is_added_stopped_from_version_2_11_0()
    {
        var harness = new QBittorrentHarness();
        var torrentFile = "d6:announce..."u8.ToArray();

        await harness.Proxy.AddTorrentAsync(
            harness.Session,
            QBittorrentHarness.Settings,
            new TorrentAddRequest(QBittorrentHarness.InfoHash, torrentFile, null),
            CancellationToken.None);

        var add = harness.Fake.Requests.Should().ContainSingle(request => request.Path == "/api/v2/torrents/add").Subject;

        add.MultipartFiles.Should().ContainKey("torrents")
            .WhoseValue.Should().Equal(torrentFile);
        add.MultipartFields["stopped"].Should().Be("true");
        add.MultipartFields.Should().NotContainKey("paused");
        add.MultipartFields["category"].Should().Be("wondarr");
    }

    [Fact]
    public async Task A_magnet_is_added_running_until_its_metadata_arrives()
    {
        var harness = new QBittorrentHarness();

        await harness.Proxy.AddTorrentAsync(
            harness.Session,
            QBittorrentHarness.Settings,
            new TorrentAddRequest(QBittorrentHarness.InfoHash, null, QBittorrentHarness.Magnet),
            CancellationToken.None);

        var add = harness.Fake.Requests.Should().ContainSingle(request => request.Path == "/api/v2/torrents/add").Subject;

        add.MultipartFields["urls"].Should().Be(QBittorrentHarness.Magnet);
        add.MultipartFields["stopCondition"].Should().Be("MetadataReceived");
        add.MultipartFields.Should().NotContainKey("stopped");
        add.MultipartFields.Should().NotContainKey("paused");
        add.MultipartFields["category"].Should().Be("wondarr");
    }

    [Fact]
    public async Task Before_version_2_11_0_a_torrent_file_is_added_paused()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithVersion("2.9.2"));

        await harness.Proxy.AddTorrentAsync(
            harness.Session,
            QBittorrentHarness.Settings,
            new TorrentAddRequest(QBittorrentHarness.InfoHash, "d6:announce..."u8.ToArray(), null),
            CancellationToken.None);

        var add = harness.Fake.Requests.Should().ContainSingle(request => request.Path == "/api/v2/torrents/add").Subject;

        add.MultipartFields["paused"].Should().Be("true");
        add.MultipartFields.Should().NotContainKey("stopped");
    }

    [Theory]
    [InlineData("2.11.0", "/api/v2/torrents/start", "/api/v2/torrents/stop")]
    [InlineData("2.9.2", "/api/v2/torrents/resume", "/api/v2/torrents/pause")]
    public async Task Start_and_stop_use_the_endpoints_of_the_client_version(string version, string startPath, string stopPath)
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithVersion(version));
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");

        await harness.Proxy.StartTorrentAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);
        await harness.Proxy.StopTorrentAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        harness.Fake.Requests.Should().Contain(request => request.Path == startPath);
        harness.Fake.Requests.Should().Contain(request => request.Path == stopPath);
    }

    [Fact]
    public async Task A_version_below_2_8_15_is_refused_with_the_qBittorrent_4_5_message()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WithVersion("2.8.3"));
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "downloading");

        var act = () => harness.Proxy.GetTorrentsAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>())
            .Which.Message.Should().Contain("qBittorrent 4.5 or newer is needed");
    }

    [Fact]
    public async Task File_priority_joins_the_indexes_with_pipes()
    {
        var harness = new QBittorrentHarness();
        var torrent = harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");
        torrent.Files.Add(new FakeTorrentFile(1, "01 - A.flac", 1));
        torrent.Files.Add(new FakeTorrentFile(4, "04 - B.flac", 2));
        torrent.Files.Add(new FakeTorrentFile(7, "07 - C.flac", 3));

        await harness.Proxy.SetFilePriorityAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, [1, 4, 7], 1, CancellationToken.None);

        var request = harness.Fake.Requests.Should().Contain(request => request.Path == "/api/v2/torrents/filePrio").Subject;

        request.Form["id"].Should().Be("1|4|7");
        request.Form["priority"].Should().Be("1");
        request.Form["hash"].Should().Be(QBittorrentHarness.InfoHash);
        torrent.Files.Should().OnlyContain(file => file.Priority == 1);
    }

    [Fact]
    public async Task A_duplicate_add_counts_as_added()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WhereDuplicateAddsFail());
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");

        var act = () => harness.Proxy.AddTorrentAsync(
            harness.Session,
            QBittorrentHarness.Settings,
            new TorrentAddRequest(QBittorrentHarness.InfoHash, "d6:announce..."u8.ToArray(), null),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_refused_add_of_a_torrent_the_client_does_not_have_throws()
    {
        var harness = new QBittorrentHarness(new QbittorrentFake().WhereDuplicateAddsFail());

        var act = () => harness.Proxy.AddTorrentAsync(
            harness.Session,
            QBittorrentHarness.Settings,
            new TorrentAddRequest(QBittorrentHarness.InfoHash, "d6:announce..."u8.ToArray(), null),
            CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>())
            .Which.Message.Should().Contain("refused to add");
    }

    [Fact]
    public async Task A_404_on_a_per_torrent_call_means_the_torrent_is_gone()
    {
        var harness = new QBittorrentHarness();

        var info = () => harness.Proxy.GetTorrentsAsync(harness.Session, QBittorrentHarness.Settings, "ffffffffffffffffffffffffffffffffffffffff", CancellationToken.None);
        var files = () => harness.Proxy.GetTorrentFilesAsync(harness.Session, QBittorrentHarness.Settings, "ffffffffffffffffffffffffffffffffffffffff", CancellationToken.None);
        var start = () => harness.Proxy.StartTorrentAsync(harness.Session, QBittorrentHarness.Settings, "ffffffffffffffffffffffffffffffffffffffff", CancellationToken.None);

        await info.Should().ThrowAsync<TorrentNotFoundException>();
        await files.Should().ThrowAsync<TorrentNotFoundException>();

        (await start.Should().ThrowAsync<DownloadClientException>())
            .Which.Message.Should().Be("not found");
    }

    [Fact]
    public async Task Remove_passes_the_delete_files_choice_through()
    {
        var harness = new QBittorrentHarness();
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");

        await harness.Proxy.RemoveTorrentAsync(harness.Session, QBittorrentHarness.Settings, QBittorrentHarness.InfoHash, false, CancellationToken.None);

        var request = harness.Fake.Requests.Should().Contain(request => request.Path == "/api/v2/torrents/delete").Subject;

        request.Form["hashes"].Should().Be(QBittorrentHarness.InfoHash);
        request.Form["deleteFiles"].Should().Be("false");
        harness.Fake.Torrents.Should().BeEmpty();
    }
}
