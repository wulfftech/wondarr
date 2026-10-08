using Wondarr.Sources.Torznab.Clients;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>Tests of <see cref="QBittorrentClient"/>: the state mapping, the path mapping and the secrets.</summary>
public sealed class QBittorrentClientTests
{
    [Fact]
    public async Task Get_returns_null_when_the_client_does_not_have_the_torrent()
    {
        var harness = new QBittorrentHarness();

        var torrent = await harness.Client.GetAsync(QBittorrentHarness.Row(), "ffffffffffffffffffffffffffffffffffffffff", CancellationToken.None);

        torrent.Should().BeNull();
    }

    [Theory]
    [InlineData("metaDL", TorrentStatus.FetchingMetadata)]
    [InlineData("forcedMetaDL", TorrentStatus.FetchingMetadata)]
    [InlineData("pausedDL", TorrentStatus.Stopped)]
    [InlineData("stoppedDL", TorrentStatus.Stopped)]
    [InlineData("pausedUP", TorrentStatus.Completed)]
    [InlineData("stoppedUP", TorrentStatus.Completed)]
    [InlineData("uploading", TorrentStatus.Completed)]
    [InlineData("stalledUP", TorrentStatus.Completed)]
    [InlineData("queuedUP", TorrentStatus.Completed)]
    [InlineData("forcedUP", TorrentStatus.Completed)]
    [InlineData("queuedDL", TorrentStatus.Queued)]
    [InlineData("downloading", TorrentStatus.Downloading)]
    [InlineData("forcedDL", TorrentStatus.Downloading)]
    [InlineData("stalledDL", TorrentStatus.Stalled)]
    [InlineData("checkingDL", TorrentStatus.Checking)]
    [InlineData("checkingUP", TorrentStatus.Checking)]
    [InlineData("checkingResumeData", TorrentStatus.Checking)]
    [InlineData("allocating", TorrentStatus.Checking)]
    [InlineData("moving", TorrentStatus.Moving)]
    [InlineData("error", TorrentStatus.Error)]
    [InlineData("missingFiles", TorrentStatus.Error)]
    [InlineData("somethingNewInApi6", TorrentStatus.Downloading)]
    public void Every_state_name_of_both_generations_maps_to_its_status(string state, TorrentStatus expected)
    {
        QBittorrentClient.MapState(state, hasMetadata: true).Should().Be(expected);
    }

    [Fact]
    public async Task A_stopped_magnet_without_metadata_is_still_fetching_metadata()
    {
        var harness = new QBittorrentHarness();
        var fake = harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");
        fake.HasMetadata = false;

        var torrent = await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None);

        torrent!.Status.Should().Be(TorrentStatus.FetchingMetadata);
        torrent.HasMetadata.Should().BeFalse();
    }

    [Fact]
    public async Task Has_metadata_is_inferred_from_an_empty_file_list_when_the_client_does_not_report_it()
    {
        var harness = new QBittorrentHarness();
        var fake = harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");
        fake.HasMetadata = null;

        // No files yet: the magnet's metadata has not arrived.
        (await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None))!
            .Status.Should().Be(TorrentStatus.FetchingMetadata);

        fake.Files.Add(new FakeTorrentFile(0, "01 - A.flac", 1_000));

        (await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None))!
            .Status.Should().Be(TorrentStatus.Stopped);
    }

    [Fact]
    public async Task The_paths_the_client_reports_are_mapped_to_wondarrs_view()
    {
        var settings = """
            {"host":"127.0.0.1","port":8080,"username":"admin","password":"fixture-password-1234",
             "remotePathMappings":[{"key":"/downloads","value":"/mnt/media"}]}
            """;
        var harness = new QBittorrentHarness();
        var fake = harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "downloading");
        fake.SavePath = "/downloads";
        fake.ContentPath = "/downloads/Some Release";
        fake.HasMetadata = true;

        var torrent = await harness.Client.GetAsync(QBittorrentHarness.Row(settings), QBittorrentHarness.InfoHash, CancellationToken.None);

        torrent!.SavePath.Should().Be("/mnt/media");
        torrent.ContentPath.Should().Be(Path.Combine("/mnt/media", "Some Release"));
        torrent.InfoHash.Should().Be(QBittorrentHarness.InfoHash);
        torrent.Name.Should().Be("Some Release");
    }

    [Fact]
    public async Task The_files_of_a_torrent_are_listed_with_index_size_and_priority()
    {
        var harness = new QBittorrentHarness();
        var fake = harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "stoppedDL");
        fake.Files.Add(new FakeTorrentFile(0, "01 - A.flac", 1_000));
        fake.Files.Add(new FakeTorrentFile(1, "02 - B.flac", 2_000));

        var files = await harness.Client.GetFilesAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None);

        files.Should().HaveCount(2);
        files[0].Should().Be(new TorrentFileInfo(0, "01 - A.flac", 1_000, 0, 1));
        files[1].Should().Be(new TorrentFileInfo(1, "02 - B.flac", 2_000, 0, 1));
    }

    [Fact]
    public async Task Add_rejects_a_request_that_does_not_carry_exactly_one_payload()
    {
        var harness = new QBittorrentHarness();
        var row = QBittorrentHarness.Row();

        var both = () => harness.Client.AddAsync(row, new TorrentAddRequest(QBittorrentHarness.InfoHash, "d6:..."u8.ToArray(), QBittorrentHarness.Magnet), CancellationToken.None);
        var neither = () => harness.Client.AddAsync(row, new TorrentAddRequest(QBittorrentHarness.InfoHash, null, null), CancellationToken.None);

        await both.Should().ThrowAsync<ArgumentException>();
        await neither.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task File_priority_accepts_only_zero_and_one(int priority)
    {
        var harness = new QBittorrentHarness();

        var act = () => harness.Client.SetFilePriorityAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, [0], priority, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_password_never_appears_in_a_log_line_or_an_exception_message()
    {
        var harness = new QBittorrentHarness();
        var settings = QBittorrentHarness.Settings with { Password = "another-fixture-password" };

        // A wrong password: the client fails, and nothing it says or logs may name the password.
        var act = () => harness.Proxy.GetTorrentsAsync(harness.Session, settings, QBittorrentHarness.InfoHash, CancellationToken.None);

        await act.Should().ThrowAsync<DownloadClientException>();

        harness.ProxyLog.Messages.Should().NotBeEmpty();
        harness.ProxyLog.Messages.Should().NotContain(message => message.Contains("another-fixture-password", StringComparison.Ordinal));

        // The row's password is registered with the secret registry, so even a stray mention is redacted.
        await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None);
        harness.Secrets.Redact("the password is " + QbittorrentFake.Password + " here")
            .Should().NotContain(QbittorrentFake.Password);
    }

    [Fact]
    public async Task An_unknown_state_is_logged_once_and_treated_as_downloading()
    {
        var harness = new QBittorrentHarness();
        harness.Fake.AddTorrent(QBittorrentHarness.InfoHash, "brandNewState");

        var first = await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None);
        var second = await harness.Client.GetAsync(QBittorrentHarness.Row(), QBittorrentHarness.InfoHash, CancellationToken.None);

        first!.Status.Should().Be(TorrentStatus.Downloading);
        second!.Status.Should().Be(TorrentStatus.Downloading);
    }
}
