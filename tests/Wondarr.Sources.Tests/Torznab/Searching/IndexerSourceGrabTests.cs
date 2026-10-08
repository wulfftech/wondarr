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
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
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

    public IndexerSourceGrabTests()
    {
        _torrents.SavePath = SavePath;
        var services = new ServiceCollection();
        var indexerService = _indexerService;
        indexerService.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => new Indexer { Id = call.Arg<long>(), Name = "Indexer", Type = "torznab", Protocol = DownloadProtocol.Torrent });
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
    public async Task A_bundled_grab_selects_every_wanted_file_and_a_second_grab_only_switches_its_own_on()
    {
        _indexer.Download = new IndexerDownload(AlbumTorrent(), null);
        var provider = Provider(DownloadProtocol.Torrent);
        var first = TorrentCandidate(fileIndex: 2) is var candidate
            ? candidate with { Release = candidate.Release! with { AlsoWanted = [$"{Album}/05 - Crescendolls.flac"] } }
            : null!;

        await provider.GrabAsync(first, "wondarr/one", CancellationToken.None);
        var added = _torrents.Single();
        var hash = added.Hash;
        var second = TorrentCandidate(fileIndex: 4) with { Release = TorrentCandidate(fileIndex: 4).Release! with { InfoHash = hash } };
        await provider.GrabAsync(second, "wondarr/two", CancellationToken.None);
        var third = TorrentCandidate(fileIndex: 0) with { Release = TorrentCandidate(fileIndex: 0).Release! with { InfoHash = hash } };
        await provider.GrabAsync(third, "wondarr/three", CancellationToken.None);

        _indexer.Downloads.Should().Be(1, "the torrent was in the client already for the later grabs");
        added.Files.Where(file => file.Priority > 0).Select(file => file.Index).Should().Equal(0, 2, 4);
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
    public async Task A_finished_post_s_file_is_moved_into_staging_and_the_job_deleted_after_the_last_item()
    {
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet);
        // The search service writes each grab's queue item (with its handle) before the next grab.
        var first = await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
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

    private IndexerSourceProvider Provider(DownloadProtocol protocol)
    {
        var import = Substitute.For<IOptionsMonitor<ImportOptions>>();
        import.CurrentValue.Returns(new ImportOptions { ContainerStagingPath = StagingRoot });

        return new IndexerSourceProvider(
            protocol,
            _services.GetRequiredService<IServiceScopeFactory>(),
            new SingleClientFactory(_indexer),
            _torrents,
            _usenet,
            new DiskOperations(),
            import,
            TimeProvider.System,
            NullLogger<IndexerSourceProvider>.Instance);
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

    private static Candidate UsenetCandidate(string? file) => new()
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
            ReleaseId = "nzb-guid-1",
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

        public int Downloads { get; private set; }

        public Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexerRelease>>([]);

        public Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(Download);
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
                    string.Empty,
                    torrent.HasMetadata,
                    null));
        }

        public Task<IReadOnlyList<TorrentFileInfo>> GetFilesAsync(DownloadClient client, string infoHash, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TorrentFileInfo>>(Find(infoHash)?.Files.ToList() ?? throw new TorrentNotFoundException());

        public Task SetFilePriorityAsync(DownloadClient client, string infoHash, IReadOnlyCollection<int> fileIndexes, int priority, CancellationToken cancellationToken)
        {
            var files = Find(infoHash)!.Files;

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
        public Task<string> AddAsync(DownloadClient client, UsenetAddRequest request, CancellationToken cancellationToken)
        {
            var job = new FakeJob($"SABnzbd_nzo_{Count + 1}") { AddedPaused = request.Paused, Paused = request.Paused };
            using var stream = new MemoryStream(request.NzbFile!);
            job.Files.AddRange(NzbFileList.Parse(stream).Select(file => new UsenetFileInfo($"nzf_{file.Index}", file.FileName ?? "?", file.Size)));
            Add(job);
            return Task.FromResult(job.Id);
        }

        public Task<IReadOnlyList<UsenetFileInfo>> GetFilesAsync(DownloadClient client, string jobId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UsenetFileInfo>>([.. Find(jobId)!.Files]);

        public Task DeleteFileAsync(DownloadClient client, string jobId, string fileId, CancellationToken cancellationToken)
        {
            Find(jobId)!.Files.RemoveAll(file => file.FileId == fileId);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(DownloadClient client, string jobId, CancellationToken cancellationToken)
        {
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
