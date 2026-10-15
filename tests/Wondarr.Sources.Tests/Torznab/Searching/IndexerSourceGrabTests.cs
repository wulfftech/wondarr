using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Tests.Torznab.Parsing;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The indexer sources' grab, status and cancel (P7-07) over fake qBittorrent and SABnzbd clients,
/// real files in a temp folder, and a SQLite file for the queue lookups.
/// </summary>
public sealed class IndexerSourceGrabTests : IDisposable
{
    private const string Album = "Daft Punk - Discovery (2001) [FLAC]";
    private const long TrackSize = 33_000_000;

    private static readonly string[] Titles =
    [
        "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger", "Crescendolls",
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("wondarr-grab-").FullName;
    private readonly FakeTorrents _torrents = new();
    private readonly FakeUsenet _usenet = new();
    private readonly FakeIndexer _indexer = new();
    private readonly IIndexerService _indexerService = Substitute.For<IIndexerService>();
    private readonly ServiceProvider _services;
    private long _seeded = 900;
    private string _indexerType = "torznab";
    private string _indexerSettings = "{}";

    public IndexerSourceGrabTests()
    {
        _torrents.SavePath = SavePath;
        var services = new ServiceCollection();
        var indexerService = _indexerService;
        indexerService.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => new Indexer { Id = call.Arg<long>(), Name = "Indexer", Type = _indexerType, Settings = _indexerSettings, Protocol = DownloadProtocol.Torrent });
        var clientService = Substitute.For<IDownloadClientService>();
        DownloadClient[] clients =
        [
            new() { Id = 1, Name = "qBittorrent", Type = "qbittorrent", Protocol = DownloadProtocol.Torrent },
            new() { Id = 2, Name = "SABnzbd", Type = "sabnzbd", Protocol = DownloadProtocol.Usenet },
        ];
        clientService.ListAsync(Arg.Any<CancellationToken>()).Returns(clients);
        clientService.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(call => clients.FirstOrDefault(row => row.Id == call.Arg<long>()));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(indexerService);
        services.AddSingleton(clientService);

        // Foreign keys off: the tests seed queue items without the songs and candidates they belong to.
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={DatabaseFile};Foreign Keys=False")
            .UseSnakeCaseNamingConvention());
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            new DatabaseMigrator(database, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private string DatabaseFile => Path.Combine(_root, "wondarr.db");

    private string SavePath => Path.Combine(_root, "torrents");

    private string StagingRoot => Path.Combine(_root, "staging");

    [Fact]
    public async Task A_torrent_grab_downloads_only_the_wanted_file()
    {
        var torrent = AlbumTorrent();
        _indexer.Download = new IndexerDownload(torrent, null);
        var provider = Provider(DownloadProtocol.Torrent);

        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);

        var added = _torrents.Single();
        added.Started.Should().BeTrue();
        added.Files.Where(file => file.Priority > 0).Select(file => file.Path).Should().Equal($"{Album}/03 - Digital Love.flac");
        var grab = ContainerGrab.Deserialize(handle.Value);
        grab.InfoHash.Should().Be(added.Hash);
        grab.FilePath.Should().Be($"{Album}/03 - Digital Love.flac");
        grab.StagingDir.Should().Be(Path.Combine(StagingRoot, "wondarr", "one"));
    }

    [Fact]
    public async Task A_bundled_grab_selects_every_wanted_file_and_a_later_grab_only_switches_its_own_on_without_an_info_hash()
    {
        // Gazelle never gives an info-hash: no release here carries one. A, B and C are songs 3, 5 and 1.
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var first = TorrentCandidate(fileIndex: 2) is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = [$"{Album}/05 - Crescendolls.flac"] } }
            : null!;

        var handleA = await provider.GrabAsync(first, "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Torznab, ContainerGrab.Deserialize(handleA.Value));
        var added = _torrents.Single();
        added.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(2, 4);

        // B's own grab (nothing else wanted) must leave A and the rest of the bundle alone.
        var handleB = await provider.GrabAsync(TorrentCandidate(fileIndex: 4), "wondarr/two", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Torznab, ContainerGrab.Deserialize(handleB.Value));
        added.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(2, 4);

        var handleC = await provider.GrabAsync(TorrentCandidate(fileIndex: 0), "wondarr/three", CancellationToken.None);

        _indexer.Downloads.Should().Be(1, "a partner's handle knows the info-hash, so the .torrent is not downloaded again");
        added.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(0, 2, 4);
        _torrents.PriorityCalls.Where(call => call.Priority == 0).Should().ContainSingle("only the first grab, which added the torrent, switched files off");
        ContainerGrab.Deserialize(handleA.Value).AddedByWondarr.Should().BeTrue();
        ContainerGrab.Deserialize(handleB.Value).AddedByWondarr.Should().BeTrue("a partner's grab added it");
        ContainerGrab.Deserialize(handleC.Value).AddedByWondarr.Should().BeTrue();
    }

    [Fact]
    public async Task A_gazelle_torrent_whose_computed_hash_is_already_in_the_client_keeps_its_other_files()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var first = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var added = _torrents.Single();

        // No queue item is written for the first grab, so nothing but the download itself tells the hash.
        var second = await provider.GrabAsync(TorrentCandidate(fileIndex: 4), "wondarr/two", CancellationToken.None);

        _indexer.Downloads.Should().Be(2);
        _torrents.Should().ContainSingle("the torrent is added once");

        // The first grab's file stays selected beside the second's.
        added.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(2, 4);
        ContainerGrab.Deserialize(first.Value).AddedByWondarr.Should().BeTrue();
        ContainerGrab.Deserialize(second.Value).AddedByWondarr.Should().BeFalse("nothing says Wondarr added it, so it is treated as not added");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_torrent_already_in_the_client_has_no_file_lowered_and_is_not_marked_as_added(bool releaseCarriesInfoHash)
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var hash = SeedForeignTorrent(started: true);
        var foreign = _torrents.Single();
        foreign.Files[0] = foreign.Files[0] with { Priority = 4 };
        foreign.Files[1] = foreign.Files[1] with { Priority = 0 };
        var provider = Provider(DownloadProtocol.Torrent);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { InfoHash = releaseCarriesInfoHash ? hash : null } };

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        _indexer.Downloads.Should().Be(releaseCarriesInfoHash ? 0 : 1);
        foreign.Files.Select(file => file.Priority).Should().Equal(4, 0, 1, 1, 1);
        _torrents.PriorityCalls.Should().NotContain(call => call.Priority == 0);
        foreign.Removed.Should().BeFalse();
        ContainerGrab.Deserialize(handle.Value).AddedByWondarr.Should().BeFalse();
    }

    [Fact]
    public async Task A_wanted_file_the_user_switched_off_is_raised_and_nothing_else_changes()
    {
        SeedForeignTorrent(started: true);
        var foreign = _torrents.Single();
        foreign.Files[2] = foreign.Files[2] with { Priority = 0 };
        foreign.Files[3] = foreign.Files[3] with { Priority = 0 };
        var provider = Provider(DownloadProtocol.Torrent);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { InfoHash = foreign.Hash } };

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        foreign.Files.Select(file => file.Priority).Should().Equal(1, 1, 1, 0, 1);
    }

    [Fact]
    public async Task A_paused_torrent_the_user_added_is_resumed_for_a_wanted_file_that_needs_data_and_never_removed()
    {
        var hash = SeedForeignTorrent(started: false);
        var foreign = _torrents.Single();
        foreign.Files[2] = foreign.Files[2] with { Progress = 0.3 };
        var log = new CapturingLogger<IndexerSourceProvider>();
        var provider = Provider(DownloadProtocol.Torrent, logger: log);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { InfoHash = hash } };

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        foreign.Started.Should().BeTrue("a wanted file needs data, so Wondarr resumed the torrent");
        log.Lines.Should().Contain(line => line.Contains("Resumed the stopped torrent", StringComparison.Ordinal));

        await provider.CancelAsync(handle, CancellationToken.None);

        foreign.Removed.Should().BeFalse();

        // A torrent Wondarr did not add is never changed on a cancel.
        foreign.Files.Select(file => file.Priority).Should().Equal(1, 1, 1, 1, 1);
    }

    [Fact]
    public async Task A_paused_torrent_the_user_added_stays_paused_when_the_wanted_file_is_already_complete()
    {
        var hash = SeedForeignTorrent(started: false);
        var foreign = _torrents.Single();
        foreign.Files[2] = foreign.Files[2] with { Progress = 1 };
        var provider = Provider(DownloadProtocol.Torrent);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { InfoHash = hash } };

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        foreign.Started.Should().BeFalse();
    }

    [Fact]
    public async Task A_pushed_release_is_grabbed_without_an_indexer_row()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var pushed = TorrentCandidate(fileIndex: 2) is var candidate
            ? candidate with { Release = candidate.Release! with { IndexerId = PushedReleaseClient.IndexerId, IndexerName = "autobrr" } }
            : null!;

        await provider.GrabAsync(pushed, "wondarr/push", CancellationToken.None);

        _torrents.Single().Started.Should().BeTrue();
        _indexer.Downloads.Should().Be(1);
        await _indexerService.DidNotReceive().GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_finished_file_is_hard_linked_into_staging_and_the_torrent_keeps_its_data()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);
        status.State.Should().Be(DownloadState.Downloading);

        var source = WriteFile(Path.Combine(SavePath, Album, "03 - Digital Love.flac"), "flac bytes");
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };

        status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
        status.CompletedPath.Should().Be(Path.Combine(StagingRoot, "wondarr", "one", "03 - Digital Love.flac"));
        File.ReadAllText(status.CompletedPath!).Should().Be("flac bytes");
        File.Exists(source).Should().BeTrue("torrent data is never moved");
    }

    [Fact]
    public async Task A_magnet_is_matched_and_selected_once_its_metadata_arrives()
    {
        var provider = Provider(DownloadProtocol.Torrent);
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Discovery";
        var candidate = TorrentCandidate(fileIndex: null) with
        {
            Release = TorrentCandidate(fileIndex: null).Release! with { DownloadUrl = magnet, MagnetUrl = magnet, Files = null, FileIndex = null },
        };

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        _indexer.Downloads.Should().Be(0);
        var torrent = _torrents.Single();
        torrent.Magnet.Should().Be(magnet);
        (await provider.GetStatusAsync(handle, CancellationToken.None)).Message.Should().Be("Fetching the torrent's metadata");

        // qBittorrent's stop condition: stopped, with the metadata and every file still wanted.
        torrent.DeliverMetadata(Album, Titles);

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().NotBe(DownloadState.Failed);
        torrent.Started.Should().BeTrue();
        torrent.Files.Where(file => file.Priority > 0).Select(file => file.Path).Should().Equal($"{Album}/03 - Digital Love.flac");
    }

    [Fact]
    public async Task A_magnet_without_the_song_fails()
    {
        var provider = Provider(DownloadProtocol.Torrent);
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
        var candidate = TorrentCandidate(fileIndex: null) with
        {
            Release = TorrentCandidate(fileIndex: null).Release! with { DownloadUrl = magnet, MagnetUrl = magnet, Files = null, FileIndex = null },
        };
        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);
        _torrents.Single().DeliverMetadata("Homework", ["Daftendirekt", "Revolution 909"]);

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        status.Message.Should().Be("The song is not in this torrent.");
    }

    [Fact]
    public async Task Cancel_switches_the_file_off_and_removes_an_untouched_unshared_torrent()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();

        await provider.CancelAsync(handle, CancellationToken.None);

        torrent.Files[2].Priority.Should().Be(0);
        torrent.Removed.Should().BeTrue();
        torrent.RemovedWithFiles.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_keeps_a_torrent_another_queue_item_uses_or_one_that_has_data()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        var other = ContainerGrab.Deserialize(handle.Value) with { FilePath = $"{Album}/01 - One More Time.flac", StagingDir = "elsewhere" };
        await SeedQueueItemAsync(SourceTypes.Torznab, other);

        await provider.CancelAsync(handle, CancellationToken.None);

        torrent.Removed.Should().BeFalse("another queue item still uses the torrent");
        torrent.Files[2].Priority.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_keeps_a_torrent_that_has_downloaded_something()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        torrent.Files[2] = torrent.Files[2] with { Progress = 0.4 };

        await provider.CancelAsync(handle, CancellationToken.None);

        torrent.Removed.Should().BeFalse();
    }

    [Theory]
    [InlineData(TorrentStatus.Checking)]
    [InlineData(TorrentStatus.Moving)]
    [InlineData(TorrentStatus.Error)]
    [InlineData(TorrentStatus.Completed)]
    [InlineData(TorrentStatus.Unknown)]
    public async Task Cancel_keeps_a_torrent_in_any_state_that_is_not_on_the_removable_list(TorrentStatus status)
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        torrent.Status = status;

        await provider.CancelAsync(handle, CancellationToken.None);

        torrent.Removed.Should().BeFalse("what the client reports in this state cannot be trusted");
        torrent.Files[2].Priority.Should().Be(0, "only this song's file is switched off");
        torrent.Files.Count(file => file.Priority > 0).Should().Be(0);
    }

    [Theory]
    [InlineData(TorrentStatus.Stopped)]
    [InlineData(TorrentStatus.Queued)]
    [InlineData(TorrentStatus.Downloading)]
    [InlineData(TorrentStatus.Stalled)]
    public async Task Cancel_removes_an_untouched_torrent_in_each_removable_state(TorrentStatus status)
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        _torrents.Single().Status = status;

        await provider.CancelAsync(handle, CancellationToken.None);

        _torrents.Removals.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_removes_a_magnet_still_fetching_its_metadata_without_deleting_files()
    {
        var provider = Provider(DownloadProtocol.Torrent);
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Discovery";
        var candidate = TorrentCandidate(fileIndex: null);
        candidate = candidate with { Release = candidate.Release! with { DownloadUrl = magnet, MagnetUrl = magnet, Files = null, FileIndex = null } };
        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        await provider.CancelAsync(handle, CancellationToken.None);

        _torrents.Removals.Should().ContainSingle().Which.DeleteFiles.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_only_switches_its_own_file_off_when_the_torrent_is_shared()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var first = TorrentCandidate(fileIndex: 2) is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = [$"{Album}/05 - Crescendolls.flac"] } }
            : null!;
        var handle = await provider.GrabAsync(first, "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        var partner = ContainerGrab.Deserialize(handle.Value) with { FilePath = $"{Album}/05 - Crescendolls.flac", StagingDir = "elsewhere" };
        await SeedQueueItemAsync(SourceTypes.Torznab, partner);

        await provider.CancelAsync(handle, CancellationToken.None);

        torrent.Removed.Should().BeFalse();
        torrent.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(4);
    }

    [Fact]
    public async Task Cancel_does_nothing_for_a_handle_saved_before_ownership_was_recorded()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        var legacy = new GrabHandle(handle.SourceType, handle.Value.Replace(",\"addedByWondarr\":true", string.Empty, StringComparison.Ordinal));
        ContainerGrab.Deserialize(legacy.Value).AddedByWondarr.Should().BeFalse("a handle without the field reads as not added");

        await provider.CancelAsync(legacy, CancellationToken.None);

        torrent.Removed.Should().BeFalse();
        torrent.Files[2].Priority.Should().Be(1);
    }

    [Fact]
    public async Task A_selection_that_fails_on_a_torrent_just_added_removes_it()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { Files = [new ContainerFile(0, "Elsewhere/01 - Nope.flac", TrackSize)], FileIndex = 0 } };

        var act = () => provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<DownloadClientException>();
        _torrents.Should().BeEmpty("the torrent has no data yet");
        _torrents.Removals.Should().ContainSingle().Which.DeleteFiles.Should().BeFalse();
    }

    [Fact]
    public async Task A_selection_that_fails_on_a_torrent_the_user_added_leaves_it_as_it_was()
    {
        var hash = SeedForeignTorrent(started: true);
        var foreign = _torrents.Single();
        var provider = Provider(DownloadProtocol.Torrent);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { InfoHash = hash, Files = [new ContainerFile(0, "Elsewhere/01 - Nope.flac", TrackSize)], FileIndex = 0 } };

        var act = () => provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<DownloadClientException>();
        _torrents.Should().ContainSingle();
        foreign.Removed.Should().BeFalse();
        foreign.Files.Select(file => file.Priority).Should().Equal(1, 1, 1, 1, 1);
    }

    [Fact]
    public async Task A_finished_file_is_staged_from_the_incomplete_folder_while_the_torrent_is_still_downloading()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        _torrents.DownloadPath = Path.Combine(_root, "incomplete");
        var source = WriteFile(Path.Combine(_torrents.DownloadPath, Album, "03 - Digital Love.flac"), "flac bytes");
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };
        torrent.Status = TorrentStatus.Downloading;

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
        File.ReadAllText(status.CompletedPath!).Should().Be("flac bytes");
        File.Exists(source).Should().BeTrue("torrent data is never moved");
    }

    [Fact]
    public async Task The_incomplete_folder_is_found_from_the_content_path_when_the_client_reports_no_download_path()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        var incomplete = Path.Combine(_root, "incomplete");
        _torrents.ContentPath = Path.Combine(incomplete, Album);
        WriteFile(Path.Combine(incomplete, Album, "03 - Digital Love.flac"), "flac bytes");
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };
        torrent.Status = TorrentStatus.Downloading;

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
    }

    [Theory]
    [InlineData(TorrentStatus.Downloading)]
    [InlineData(TorrentStatus.Moving)]
    public async Task A_complete_file_that_is_not_found_is_a_wait_while_the_torrent_is_incomplete_or_moving(TorrentStatus status)
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };
        torrent.Status = status;

        var result = await provider.GetStatusAsync(handle, CancellationToken.None);

        result.State.Should().Be(DownloadState.RemotelyQueued);
    }

    [Fact]
    public async Task A_file_is_never_linked_or_copied_while_the_client_is_moving_it()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        WriteFile(Path.Combine(SavePath, Album, "03 - Digital Love.flac"), "half moved");
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };
        torrent.Status = TorrentStatus.Moving;

        var result = await provider.GetStatusAsync(handle, CancellationToken.None);

        result.State.Should().Be(DownloadState.RemotelyQueued);
        Directory.Exists(Path.Combine(StagingRoot, "wondarr", "one")).Should().BeFalse("nothing was staged");
    }

    [Fact]
    public async Task A_complete_file_still_missing_after_the_torrent_completed_fails()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var handle = await provider.GrabAsync(TorrentCandidate(fileIndex: 2), "wondarr/one", CancellationToken.None);
        var torrent = _torrents.Single();
        torrent.Files[2] = torrent.Files[2] with { Progress = 1 };
        torrent.Status = TorrentStatus.Completed;

        var result = await provider.GetStatusAsync(handle, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Message.Should().Contain("is not there");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_gazelle_grab_spends_a_freeleech_token_only_when_the_row_says_so(bool useTokens)
    {
        _indexerType = "gazelle";
        _indexerSettings = $$"""{"url":"https://redacted.example","apiKey":"gazelle-secret-key","useFreeleechTokens":{{(useTokens ? "true" : "false")}}}""";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(AlbumTorrent()) });
        var gazelle = new GazelleClient(new StaticHttpClientFactory(handler), new SecretRegistry(), TimeProvider.System, NullLogger<GazelleClient>.Instance);
        var provider = Provider(DownloadProtocol.Torrent, client: gazelle);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { DownloadUrl = "https://redacted.example/ajax.php?action=download&id=701" + GazelleClient.TokenMark } };

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        var url = handler.Requests.Should().ContainSingle().Subject.ToString();
        url.Should().NotContain("#");
        (url.Contains("usetoken=1", StringComparison.Ordinal)).Should().Be(useTokens);
    }

    [Fact]
    public async Task A_gazelle_grab_of_a_release_no_token_can_be_spent_on_never_sends_one()
    {
        _indexerType = "gazelle";
        _indexerSettings = """{"url":"https://redacted.example","apiKey":"gazelle-secret-key","useFreeleechTokens":true}""";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(AlbumTorrent()) });
        var gazelle = new GazelleClient(new StaticHttpClientFactory(handler), new SecretRegistry(), TimeProvider.System, NullLogger<GazelleClient>.Instance);
        var provider = Provider(DownloadProtocol.Torrent, client: gazelle);
        var candidate = TorrentCandidate(fileIndex: 2);
        candidate = candidate with { Release = candidate.Release! with { DownloadUrl = "https://redacted.example/ajax.php?action=download&id=702" } };

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.ToString().Should().NotContain("usetoken");
    }

    [Fact]
    public async Task A_usenet_grab_trims_a_clean_post_to_the_wanted_files_and_resumes_it()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var candidate = UsenetCandidate("03 - Digital Love.flac") with
        {
            Release = UsenetCandidate("03 - Digital Love.flac").Release! with { AlsoWanted = ["05 - Crescendolls.flac"] },
        };

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        var job = _usenet.Single();
        job.Paused.Should().BeFalse("it was added paused, trimmed, then resumed");
        job.AddedPaused.Should().BeTrue();
        job.Files.Select(file => file.FileName).Should().Equal("03 - Digital Love.flac", "05 - Crescendolls.flac", "Discovery.par2");
        ContainerGrab.Deserialize(handle.Value).JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task A_usenet_grab_of_a_post_the_search_read_takes_the_nzb_from_the_cache_without_a_second_download()
    {
        var candidate = UsenetCandidate("03 - Digital Love.flac");
        var cache = new NzbCache(TimeProvider.System);
        cache.Put(NzbCache.KeyOf(candidate.Release!.IndexerId, candidate.Release.DownloadUrl, candidate.Release.ReleaseId), AlbumNzb());
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: cache);

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        _indexer.Downloads.Should().Be(0, "the indexer counts every NZB download against its daily quota");
        var job = _usenet.Single();
        job.AddedPaused.Should().BeTrue();
        job.Files.Select(file => file.FileName).Should().Equal("03 - Digital Love.flac", "Discovery.par2");
        ContainerGrab.Deserialize(handle.Value).JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task A_usenet_grab_downloads_the_nzb_when_the_cache_does_not_have_that_release()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var candidate = UsenetCandidate("03 - Digital Love.flac");
        var cache = new NzbCache(TimeProvider.System);
        cache.Put(NzbCache.KeyOf(candidate.Release!.IndexerId, "https://usenet.example/get/2", candidate.Release.ReleaseId), Nzb(("other.flac", 1)));
        cache.Put(NzbCache.KeyOf(candidate.Release.IndexerId + 1, candidate.Release.DownloadUrl, candidate.Release.ReleaseId), Nzb(("other.flac", 1)));
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: cache);

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        _indexer.Downloads.Should().Be(1);
        _usenet.Single().Files.Select(file => file.FileName).Should().Equal("03 - Digital Love.flac", "Discovery.par2");
    }

    [Theory]
    [InlineData("You already reached your daily restriction of 10 downloads.", "Indexer: You already reached your daily restriction of 10 downloads.")]
    [InlineData("<?xml version=\"1.0\"?><error code=\"500\" description=\"Daily download limit reached\"/>", "Indexer: Daily download limit reached")]
    public async Task A_usenet_grab_the_indexer_answers_with_something_that_is_not_an_nzb_fails_with_its_words_and_adds_nothing_to_the_client(string body, string message)
    {
        _indexer.Download = new IndexerDownload(Encoding.UTF8.GetBytes(body), null);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(TimeProvider.System));

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be(message);
        _usenet.Count.Should().Be(0, "nothing that is not an NZB reaches SABnzbd");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_usenet_grab_the_indexer_answers_with_a_429_or_a_403_fails_with_the_indexer_s_name(HttpStatusCode status)
    {
        _indexer.Failure = new IndexerException($"The indexer answered {(int)status}.", status);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(TimeProvider.System));

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be($"Indexer: The indexer answered {(int)status}.");
        _usenet.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_download_that_is_not_a_quota_answer_keeps_failing_the_way_it_did()
    {
        _indexer.Failure = new IndexerException("The indexer answered 503.", HttpStatusCode.ServiceUnavailable);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(TimeProvider.System));

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>().WithMessage("The indexer answered 503.");
        _usenet.Count.Should().Be(0);
    }

    [Fact]
    public async Task The_indexer_s_words_in_a_failed_grab_are_cleaned_of_the_api_key_the_registry_knows()
    {
        const string key = "S3cretIndexerKey";
        var secrets = new SecretRegistry();
        secrets.Register(key);
        _indexer.Download = new IndexerDownload(Encoding.UTF8.GetBytes($"Invalid key {key} for this account"), null);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(TimeProvider.System), secrets: secrets);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be("Indexer: Invalid key (removed) for this account");
    }

    [Fact]
    public async Task A_finished_post_s_file_is_moved_into_staging_and_the_job_deleted_after_the_last_item()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        // The search service writes each grab's queue item (with its handle) before the next grab; the
        // first grab asks for the second song's file as well, so the job keeps it.
        var bundled = UsenetCandidate("03 - Digital Love.flac") is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = ["05 - Crescendolls.flac"] } }
            : null!;
        var first = await provider.GrabAsync(bundled, "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(first.Value));
        var second = await provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac"), "wondarr/two", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(second.Value));

        _usenet.Count.Should().Be(1, "the second grab found the job the first one added");
        var job = _usenet.Single();
        var folder = Path.Combine(_root, "usenet", Album);
        WriteFile(Path.Combine(folder, "03 - Digital Love.flac"), "three");
        WriteFile(Path.Combine(folder, "05 - Crescendolls.flac"), "five");
        job.Complete(folder);

        var one = await provider.GetStatusAsync(first, CancellationToken.None);

        one.State.Should().Be(DownloadState.Completed);
        File.ReadAllText(one.CompletedPath!).Should().Be("three");
        job.Removed.Should().BeFalse("the second song still needs the job");

        await MarkDoneAsync(ContainerGrab.Deserialize(first.Value));
        var two = await provider.GetStatusAsync(second, CancellationToken.None);

        two.State.Should().Be(DownloadState.Completed);
        File.ReadAllText(two.CompletedPath!).Should().Be("five");
        job.Removed.Should().BeTrue();
        job.RemovedWithFiles.Should().BeTrue();
    }

    [Fact]
    public async Task A_job_trimmed_for_another_song_is_not_reused_and_a_fresh_one_is_added()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var first = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(first.Value));
        _usenet.Single().Files.Select(file => file.FileName).Should().NotContain("05 - Crescendolls.flac", "the job was trimmed to song 3");

        var second = await provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac"), "wondarr/two", CancellationToken.None);

        _usenet.Count.Should().Be(2);
        ContainerGrab.Deserialize(second.Value).JobId.Should().Be(_usenet[1].Id);
        _usenet[1].Files.Select(file => file.FileName).Should().Contain("05 - Crescendolls.flac");
        _usenet[1].Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_job_is_found_by_the_release_id_even_when_the_id_is_escaped_in_the_handle()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var bundled = UsenetCandidate("03 - Digital Love.flac", "https://nzb.example/get?id=1&x=a+b") is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = ["05 - Crescendolls.flac"] } }
            : null!;
        var first = await provider.GrabAsync(bundled, "wondarr/one", CancellationToken.None);
        first.Value.Should().NotContain("&x=a+b", "the JSON encoder escapes & and +");
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(first.Value));

        var second = await provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac", "https://nzb.example/get?id=1&x=a+b"), "wondarr/two", CancellationToken.None);

        _usenet.Count.Should().Be(1, "the second song found the job the first one added");
        ContainerGrab.Deserialize(second.Value).JobId.Should().Be(_usenet.Single().Id);
        ContainerGrab.Deserialize(second.Value).AddedByWondarr.Should().BeTrue();
    }

    [Fact]
    public async Task A_job_that_cannot_be_resumed_is_deleted_again()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        _usenet.FailResume = true;
        var provider = Provider(DownloadProtocol.Usenet);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<DownloadClientException>();
        _usenet.Single().Removed.Should().BeTrue("a paused job nobody knows about would sit there for ever");
    }

    [Fact]
    public async Task A_failed_item_deletes_its_job_once_no_other_item_uses_it()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var handle = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        _usenet.Single().Status = UsenetStatus.Failed;

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        _usenet.Single().Removed.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_item_keeps_a_job_another_item_still_uses()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var handle = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(handle.Value) with { StagingDir = "elsewhere", FilePath = "05 - Crescendolls.flac" });
        _usenet.Single().Status = UsenetStatus.Failed;

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        _usenet.Single().Removed.Should().BeFalse();
    }

    [Fact]
    public async Task A_finished_folder_that_is_not_visible_fails_the_item_but_keeps_the_job()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var handle = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        _usenet.Single().Complete(Path.Combine(_root, "not-mapped"));

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        status.Message.Should().Contain("not visible");
        _usenet.Single().Removed.Should().BeFalse("the user can fix the mapping, and the download is finished");
    }

    [Fact]
    public async Task A_song_that_is_not_in_the_downloaded_post_fails_the_item_and_deletes_the_job()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var handle = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        var folder = Path.Combine(_root, "usenet", "other");
        WriteFile(Path.Combine(folder, "99 - Something Else.flac"), "x");
        _usenet.Single().Complete(folder);

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        _usenet.Single().Removed.Should().BeTrue();
    }

    [Fact]
    public async Task A_job_whose_files_cannot_be_listed_is_not_reused()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var bundled = UsenetCandidate("03 - Digital Love.flac") is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = ["05 - Crescendolls.flac"] } }
            : null!;
        var first = await provider.GrabAsync(bundled, "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(first.Value));
        _usenet.FailGetFiles = true;

        await provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac"), "wondarr/two", CancellationToken.None);

        _usenet.Count.Should().Be(2, "a fresh job was added");
    }

    [Fact]
    public async Task A_failed_job_is_never_reused()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var bundled = UsenetCandidate("03 - Digital Love.flac") is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = ["05 - Crescendolls.flac"] } }
            : null!;
        var first = await provider.GrabAsync(bundled, "wondarr/one", CancellationToken.None);
        await SeedQueueItemAsync(SourceTypes.Newznab, ContainerGrab.Deserialize(first.Value));
        _usenet.Single().Status = UsenetStatus.Failed;

        await provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac"), "wondarr/two", CancellationToken.None);

        _usenet.Count.Should().Be(2);
    }

    [Fact]
    public async Task An_obfuscated_post_is_matched_after_unpacking()
    {
        _indexer.Download = new IndexerDownload(Nzb(("a1b2c3d4e5f6a7b8c9d0e1f2.rar", 900_000_000)), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var candidate = UsenetCandidate(null);

        var handle = await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        var job = _usenet.Single();
        job.Files.Should().HaveCount(1, "a RAR set is never trimmed");
        var folder = Path.Combine(_root, "usenet", "unpacked");
        WriteFile(Path.Combine(folder, "CD1", "02 - Aerodynamic.flac"), new string('a', 100));
        WriteFile(Path.Combine(folder, "CD1", "03 - Digital Love.flac"), new string('d', 100));
        job.Complete(folder);

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
        Path.GetFileName(status.CompletedPath).Should().Be("03 - Digital Love.flac");
    }

    [Fact]
    public async Task A_failed_post_carries_the_client_s_message()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        var handle = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        _usenet.Single().Status = UsenetStatus.Failed;

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Failed);
        status.Message.Should().Be("Repair failed");
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private IndexerSourceProvider Provider(
        DownloadProtocol protocol,
        IIndexerClient? client = null,
        Microsoft.Extensions.Logging.ILogger<IndexerSourceProvider>? logger = null,
        NzbCache? nzbCache = null,
        ISecretRegistry? secrets = null)
    {
        var import = Substitute.For<IOptionsMonitor<ImportOptions>>();
        import.CurrentValue.Returns(new ImportOptions { ContainerStagingPath = StagingRoot });

        return new IndexerSourceProvider(
            protocol,
            _services.GetRequiredService<IServiceScopeFactory>(),
            new SingleClientFactory(client ?? _indexer),
            _torrents,
            _usenet,
            new DiskOperations(),
            import,
            TimeProvider.System,
            logger ?? NullLogger<IndexerSourceProvider>.Instance,
            nzbCache: nzbCache,
            secrets: secrets);
    }

    /// <summary>A torrent the user added: all five files wanted, in the client already.</summary>
    private string SeedForeignTorrent(bool started)
    {
        var file = AlbumTorrent();
        _torrents.AddAsync(new DownloadClient { Id = 1 }, new TorrentAddRequest(TorrentMetainfo.Parse(file).InfoHash, file, null), CancellationToken.None).GetAwaiter().GetResult();
        var torrent = _torrents[^1];
        torrent.Started = started;
        torrent.Status = started ? TorrentStatus.Downloading : TorrentStatus.Stopped;
        _torrents.PriorityCalls.Clear();

        return torrent.Hash;
    }

    private static Candidate TorrentCandidate(int? fileIndex)
    {
        var files = Titles.Select((title, index) => new ContainerFile(index, $"{Album}/{index + 1:00} - {title}.flac", TrackSize)).ToList();

        return new Candidate
        {
            SourceType = SourceTypes.Torznab,
            BlocklistKey = "key",
            DisplayName = "03 - Digital Love.flac",
            RemotePath = fileIndex is { } index ? files[index].Path : Album,
            QualityId = 6,
            Container = CandidateContainer.AlbumContainer,
            Release = new ContainerRelease
            {
                IndexerId = 1,
                IndexerName = "Indexer",
                Title = Album,
                ReleaseId = "guid-1",
                DownloadUrl = "https://indexer.example/dl/1",
                FileIndex = fileIndex,
                Files = files,
                Song = new ContainerMatchRequest("Digital Love", VersionFlags.None, 3, 301_000, 1),
            },
        };
    }

    private static Candidate UsenetCandidate(string? file, string releaseId = "nzb-guid-1") => new()
    {
        SourceType = SourceTypes.Newznab,
        BlocklistKey = "key",
        DisplayName = file ?? Album,
        RemotePath = file ?? Album,
        Container = CandidateContainer.AlbumContainer,
        Release = new ContainerRelease
        {
            IndexerId = 1,
            IndexerName = "Indexer",
            Title = Album,
            ReleaseId = releaseId,
            DownloadUrl = "https://usenet.example/get/1",
            FileIndex = file is null ? null : Array.FindIndex(Titles, title => file.Contains(title, StringComparison.Ordinal)),
            Files = file is null ? null : [.. Titles.Select((title, index) => new ContainerFile(index, $"{index + 1:00} - {title}.flac", TrackSize))],
            Song = new ContainerMatchRequest(
                file is null ? "Digital Love" : Titles.First(title => file.Contains(title, StringComparison.Ordinal)),
                VersionFlags.None,
                null,
                null,
                1),
        },
    };

    private static byte[] AlbumTorrent()
    {
        var writer = new BencodeWriter().BeginDict().Str("info").BeginDict().Str("name").Str(Album).Str("files").BeginList();

        for (var index = 0; index < Titles.Length; index++)
        {
            writer.BeginDict().Str("length").Int(TrackSize).Str("path").BeginList().Str($"{index + 1:00} - {Titles[index]}.flac").End().End();
        }

        return writer.End().Str("piece length").Int(32_768).End().End().ToArray();
    }

    private static byte[] AlbumNzb() =>
        Nzb([.. Titles.Select((title, index) => ($"{index + 1:00} - {title}.flac", TrackSize)), ("Discovery.par2", 40_000L)]);

    private static byte[] Nzb(params (string Name, long Size)[] files)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">\n");

        for (var index = 0; index < files.Length; index++)
        {
            var (name, size) = files[index];
            xml.Append($"<file poster=\"p\" date=\"0\" subject=\"[{index + 1}/{files.Length}] - &quot;{name}&quot; yEnc (1/1)\">")
                .Append("<groups><group>alt.binaries.sounds.flac</group></groups>")
                .Append($"<segments><segment bytes=\"{size}\" number=\"1\">id{index}@example</segment></segments></file>\n");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</nzb>").ToString());
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task SeedQueueItemAsync(string sourceType, ContainerGrab grab)
    {
        using var scope = _services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        database.QueueItems.Add(new QueueItem
        {
            SongId = ++_seeded,
            CandidateId = 999,
            SearchRunId = 999,
            SourceType = sourceType,
            Handle = grab.Serialize(),
            Destination = grab.StagingDir,
            State = QueueItemState.Downloading,
        });
        await database.SaveChangesAsync();
    }

    private async Task MarkDoneAsync(ContainerGrab grab)
    {
        using var scope = _services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var handle = grab.Serialize();
        var item = await database.QueueItems.SingleAsync(row => row.Handle == handle);
        item.State = QueueItemState.Completed;
        await database.SaveChangesAsync();
    }

    private sealed class SingleClientFactory(IIndexerClient client) : IIndexerClientFactory
    {
        public IIndexerClient GetClient(Indexer indexer) => client;
    }

    private sealed class FakeIndexer : IIndexerClient
    {
        public IndexerDownload Download { get; set; } = new(null, null);

        /// <summary>Thrown by a download instead of answering, when set.</summary>
        public Exception? Failure { get; set; }

        public int Downloads { get; private set; }

        public List<string?> Urls { get; } = [];

        public Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexerRelease>>([]);

        public Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
        {
            Downloads++;
            Urls.Add(release.DownloadUrl);

            return Failure is { } failure ? Task.FromException<IndexerDownload>(failure) : Task.FromResult(Download);
        }
    }

    /// <summary>One torrent in the fake qBittorrent.</summary>
    private sealed class FakeTorrent(string hash)
    {
        public string Hash { get; } = hash;

        public string? Magnet { get; init; }

        public List<TorrentFileInfo> Files { get; } = [];

        public TorrentStatus Status { get; set; } = TorrentStatus.Stopped;

        public bool HasMetadata { get; set; }

        public bool Started { get; set; }

        public bool Removed { get; set; }

        public bool RemovedWithFiles { get; set; }

        /// <summary>What qBittorrent does when a magnet's metadata arrives under the stop condition.</summary>
        public void DeliverMetadata(string name, string[] titles)
        {
            Files.AddRange(titles.Select((title, index) => new TorrentFileInfo(index, $"{name}/{index + 1:00} - {title}.flac", TrackSize, 0, 1)));
            HasMetadata = true;
            Status = TorrentStatus.Stopped;
        }
    }

    private sealed class FakeTorrents : List<FakeTorrent>, ITorrentClient
    {
        /// <summary>Every priority call, in order.</summary>
        public List<(int[] Indexes, int Priority)> PriorityCalls { get; } = [];

        /// <summary>Every removal, in order.</summary>
        public List<(string Hash, bool DeleteFiles)> Removals { get; } = [];

        /// <summary>The incomplete folder the fake client reports, as Wondarr sees it; null when it keeps none.</summary>
        public string? DownloadPath { get; set; }

        /// <summary>The content path the fake client reports.</summary>
        public string ContentPath { get; set; } = string.Empty;

        public Task AddAsync(DownloadClient client, TorrentAddRequest request, CancellationToken cancellationToken)
        {
            var torrent = new FakeTorrent(request.InfoHash) { Magnet = request.MagnetUrl };

            if (request.TorrentFile is { } file)
            {
                torrent.Files.AddRange(TorrentMetainfo.Parse(file).Files.Select(entry => new TorrentFileInfo(entry.Index, entry.Path, entry.Size, 0, 1)));
                torrent.HasMetadata = true;
            }
            else
            {
                torrent.Status = TorrentStatus.FetchingMetadata;
            }

            Add(torrent);
            return Task.CompletedTask;
        }

        public Task<TorrentInfo?> GetAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
        {
            var torrent = Find(infoHash);

            return Task.FromResult(torrent is null
                ? null
                : new TorrentInfo(
                    torrent.Hash,
                    "torrent",
                    torrent.Status,
                    torrent.Files.Count == 0 ? 0 : torrent.Files.Average(file => file.Progress),
                    SavePath,
                    ContentPath,
                    torrent.HasMetadata,
                    null,
                    DownloadPath));
        }

        public Task<IReadOnlyList<TorrentFileInfo>> GetFilesAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TorrentFileInfo>>(Find(infoHash)?.Files.ToList() ?? throw new TorrentNotFoundException());

        public Task SetFilePriorityAsync(DownloadClient client, string infoHash, IReadOnlyCollection<int> fileIndexes, int priority, CancellationToken cancellationToken)
        {
            var files = Find(infoHash)!.Files;
            PriorityCalls.Add(([.. fileIndexes], priority));

            for (var index = 0; index < files.Count; index++)
            {
                if (fileIndexes.Contains(files[index].Index))
                {
                    files[index] = files[index] with { Priority = priority };
                }
            }

            return Task.CompletedTask;
        }

        public Task StartAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken)
        {
            var torrent = Find(infoHash)!;
            torrent.Started = true;
            torrent.Status = TorrentStatus.Downloading;
            return Task.CompletedTask;
        }

        public Task StopAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(DownloadClient client, string infoHash, bool deleteFiles, CancellationToken cancellationToken)
        {
            var torrent = Find(infoHash)!;
            torrent.Removed = true;
            torrent.RemovedWithFiles = deleteFiles;
            Removals.Add((infoHash, deleteFiles));
            Remove(torrent);
            return Task.CompletedTask;
        }

        /// <summary>Where the fake client saves, as Wondarr sees it.</summary>
        public string SavePath { get; set; } = string.Empty;

        private FakeTorrent? Find(string hash) => this.FirstOrDefault(torrent => torrent.Hash == hash);
    }

    /// <summary>One job in the fake SABnzbd.</summary>
    private sealed class FakeJob(string id)
    {
        public string Id { get; } = id;

        public List<UsenetFileInfo> Files { get; } = [];

        public bool AddedPaused { get; init; }

        public bool Paused { get; set; }

        public UsenetStatus Status { get; set; } = UsenetStatus.Queued;

        public string? Storage { get; set; }

        public bool Removed { get; set; }

        public bool RemovedWithFiles { get; set; }

        public void Complete(string folder)
        {
            Status = UsenetStatus.Completed;
            Storage = folder;
        }
    }

    private sealed class FakeUsenet : List<FakeJob>, IUsenetClient
    {
        /// <summary>Makes every resume fail the way a refused call does.</summary>
        public bool FailResume { get; set; }

        /// <summary>Makes listing a job's files fail once the first job is in.</summary>
        public bool FailGetFiles { get; set; }

        public Task<string> AddAsync(DownloadClient client, UsenetAddRequest request, CancellationToken cancellationToken)
        {
            var job = new FakeJob($"SABnzbd_nzo_{Count + 1}") { AddedPaused = request.Paused, Paused = request.Paused };
            using var stream = new MemoryStream(request.NzbFile!);
            job.Files.AddRange(NzbFileList.Parse(stream).Select(file => new UsenetFileInfo($"nzf_{file.Index}", file.FileName ?? "?", file.Size)));
            Add(job);
            return Task.FromResult(job.Id);
        }

        public Task<IReadOnlyList<UsenetFileInfo>> GetFilesAsync(DownloadClient client, string jobId, CancellationToken cancellationToken) =>
            FailGetFiles
                ? throw new DownloadClientException("SABnzbd did not answer.")
                : Task.FromResult<IReadOnlyList<UsenetFileInfo>>([.. Find(jobId)!.Files]);

        public Task DeleteFileAsync(DownloadClient client, string jobId, string fileId, CancellationToken cancellationToken)
        {
            Find(jobId)!.Files.RemoveAll(file => file.FileId == fileId);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(DownloadClient client, string jobId, CancellationToken cancellationToken)
        {
            if (FailResume)
            {
                throw new DownloadClientException("SABnzbd refused the resume.");
            }

            var job = Find(jobId)!;
            job.Paused = false;
            job.Status = UsenetStatus.Downloading;
            return Task.CompletedTask;
        }

        public Task<UsenetJobInfo?> GetAsync(DownloadClient client, string jobId, CancellationToken cancellationToken)
        {
            var job = Find(jobId);

            return Task.FromResult(job is null || job.Removed
                ? null
                : new UsenetJobInfo(
                    job.Id,
                    Album,
                    job.Status,
                    job.Status == UsenetStatus.Completed ? 1 : 0.5,
                    100,
                    job.Storage,
                    job.Status == UsenetStatus.Failed ? "Repair failed" : null));
        }

        public Task RemoveAsync(DownloadClient client, string jobId, bool deleteFiles, CancellationToken cancellationToken)
        {
            var job = Find(jobId)!;
            job.Removed = true;
            job.RemovedWithFiles = deleteFiles;
            return Task.CompletedTask;
        }

        private FakeJob? Find(string id) => this.FirstOrDefault(job => job.Id == id);
    }
}
