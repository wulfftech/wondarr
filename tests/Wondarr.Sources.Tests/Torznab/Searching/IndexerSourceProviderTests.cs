using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Indexers;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Parsing;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The indexer sources' search (P7-04): the releases asked for, the budget, de-duplication, the file
/// lists, the matcher's candidates. Every indexer, client and MusicBrainz answer is a fake.
/// </summary>
public sealed class IndexerSourceProviderTests
{
    private const string Recording = "rec-digital-love";
    private const string AlbumRelease = "rel-discovery";
    private const int DurationMs = 301_000;

    /// <summary>About 900 kbps for 301 s: a plausible FLAC track.</summary>
    private const long FlacTrackSize = 33_000_000;

    private static readonly string[] DiscoveryTracks =
    [
        "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger", "Crescendolls",
        "Nightvision", "Superheroes", "High Life", "Something About Us", "Voyager", "Veridis Quo", "Short Circuit",
    ];

    [Fact]
    public async Task Asks_for_the_album_context_first_then_albums_singles_and_compilations()
    {
        var indexer = new FakeIndexerClient();
        var provider = Provider(indexer, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Queries.Should().Equal(
            "Daft Punk Discovery",
            "Daft Punk Digital Love",
            "Various Artists Musique Club Hits");
        indexer.Queries.Select(query => query.Album).Should().Equal("Discovery", "Digital Love", "Musique Club Hits");
        indexer.Queries[0].Year.Should().Be(2001);
    }

    [Fact]
    public async Task Finds_the_wanted_file_in_a_torrent_and_carries_the_container()
    {
        var torrent = AlbumTorrent("Daft Punk - Discovery (2001) [FLAC]");
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Discovery (2001) [FLAC]", "guid-1", seeders: 12, downloadUrl: "https://indexer.example/dl/1")]
                : [],
            Download = _ => new IndexerDownload(torrent, null),
        };
        var provider = Provider(indexer, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.SourceType.Should().Be(SourceTypes.Torznab);
        candidate.RemotePath.Should().Be("Daft Punk - Discovery (2001) [FLAC]/03 - Digital Love.flac");
        candidate.DisplayName.Should().Be("03 - Digital Love.flac");
        candidate.Container.Should().Be(CandidateContainer.AlbumContainer);
        candidate.QualityId.Should().Be(QualityId("FLAC"));
        candidate.SizeBytes.Should().Be(FlacTrackSize);
        candidate.Parsed.Artist.Should().Be("Daft Punk");
        candidate.Availability.Seeders.Should().Be(12);
        candidate.Availability.FileListKnown.Should().BeTrue();
        candidate.Release!.FileIndex.Should().Be(2);
        candidate.Release.Files.Should().HaveCount(13);
        candidate.Release.InfoHash.Should().MatchRegex("^[0-9a-f]{40}$");
        candidate.BlocklistKey.Should().Be(BlocklistKeys.Torrent(candidate.Release.InfoHash!, candidate.RemotePath));
    }

    [Fact]
    public async Task A_release_without_the_song_gives_no_candidate_and_is_counted()
    {
        var torrent = AlbumTorrent("Daft Punk - Homework (1997) [FLAC]", ["Daftendirekt", "WDPK 83.7 FM", "Revolution 909"]);
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Homework (1997) [FLAC]", "guid-2", seeders: 3, downloadUrl: "https://indexer.example/dl/2")]
                : [],
            Download = _ => new IndexerDownload(torrent, null),
        };
        var provider = Provider(indexer, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Message.Should().Contain("1 release(s) did not contain the song");
    }

    [Fact]
    public async Task A_release_on_two_indexers_is_read_once()
    {
        var torrent = AlbumTorrent("Daft Punk - Discovery (2001) [FLAC]");
        var hash = Magnets.InfoHash("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")!;
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Discovery (2001) [FLAC]", "guid-a", seeders: 5, downloadUrl: "https://a.example/1", infoHash: hash)]
                : [],
            Download = _ => new IndexerDownload(torrent, null),
        };
        var provider = Provider(indexer, out _, indexerCount: 2);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        indexer.Downloads.Should().Be(1);
        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task A_magnet_without_a_file_list_is_a_file_list_unknown_candidate()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Discovery";
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Discovery (2001) [FLAC]", "guid-m", seeders: 40, downloadUrl: magnet, magnetUrl: magnet)]
                : [],
        };
        var provider = Provider(indexer, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        indexer.Downloads.Should().Be(0);
        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.RemotePath.Should().Be("Daft Punk - Discovery (2001) [FLAC]");
        candidate.Availability.FileListKnown.Should().BeFalse();
        candidate.Release!.FileIndex.Should().BeNull();
        candidate.Release.Files.Should().BeNull();
        candidate.Release.InfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
        candidate.QualityId.Should().Be(QualityId("FLAC"));
    }

    [Fact]
    public async Task The_budget_stops_a_slow_indexer_and_keeps_what_the_others_found()
    {
        var torrent = AlbumTorrent("Daft Punk - Discovery (2001) [FLAC]");
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Discovery (2001) [FLAC]", "guid-1", seeders: 9, downloadUrl: "https://indexer.example/dl/1")]
                : [],
            Download = _ => new IndexerDownload(torrent, null),
            Hang = indexerId => indexerId == 2,
        };
        var provider = Provider(indexer, out _, indexerCount: 2, budget: TimeSpan.FromSeconds(1));

        var started = DateTime.UtcNow;
        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(15));
        result.Candidates.Should().ContainSingle();
        result.Queries.Should().ContainSingle("the budget was spent on the first release");
        result.Message.Should().Contain("stopped after 1 s");
    }

    [Fact]
    public async Task A_usenet_post_lists_its_plain_audio_files_and_the_engine_rejects_one_over_the_limit()
    {
        var nzb = AlbumNzb();
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ?
                [
                    Release(
                        "Daft Punk - Discovery (2001) [FLAC]",
                        "nzb-guid-1",
                        grabs: 30,
                        downloadUrl: "https://usenet.example/get/1",
                        size: 2_000L * 1024 * 1024,
                        protocol: DownloadProtocol.Usenet),
                ]
                : [],
            Download = _ => new IndexerDownload(nzb, null),
        };
        var provider = Provider(indexer, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.SourceType.Should().Be(SourceTypes.Newznab);
        candidate.RemotePath.Should().Be("03 - Digital Love.flac");
        candidate.BlocklistKey.Should().Be(BlocklistKeys.Usenet("nzb-guid-1", "03 - Digital Love.flac"));

        var context = new DecisionContext
        {
            SongTitle = "Digital Love",
            MainArtists = ["Daft Punk"],
            SongDurationMs = DurationMs,
            Profile = SeedData.QualityProfiles.Single(profile => profile.Id == SeedData.LosslessProfileId),
            Qualities = SeedData.Qualities.ToDictionary(quality => quality.Id, quality => quality),
            MaxContainerSizeBytes = 1_500L * 1024 * 1024,
        };
        var decision = new DecisionEngine().Evaluate(context, [candidate]).Single();
        decision.Rejections.Select(rejection => rejection.Reason).Should().Contain(RejectionReason.ContainerTooLarge);
    }

    [Fact]
    public async Task An_obfuscated_nzb_is_a_file_list_unknown_candidate()
    {
        var nzb = Nzb(("a1b2c3d4e5f6a7b8c9d0e1f2.rar", 900_000_000), ("a1b2c3d4e5f6a7b8c9d0e1f2.par2", 40_000));
        var indexer = new FakeIndexerClient
        {
            Answer = query => query.Album == "Discovery"
                ? [Release("Daft Punk - Discovery (2001) [FLAC]", "nzb-guid-2", downloadUrl: "https://usenet.example/get/2", protocol: DownloadProtocol.Usenet)]
                : [],
            Download = _ => new IndexerDownload(nzb, null),
        };
        var provider = Provider(indexer, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.RemotePath.Should().Be("Daft Punk - Discovery (2001) [FLAC]");
        candidate.Availability.FileListKnown.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_indexer_is_named_in_the_message_and_the_search_goes_on()
    {
        var indexer = new FakeIndexerClient { Fail = indexerId => indexerId == 1 };
        var provider = Provider(indexer, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Queries.Should().HaveCount(3);
        result.Message.Should().Be("Torrents: failed: Indexer 1");
    }

    [Theory]
    [InlineData(false, true, "Torrents: no enabled torrent indexer")]
    [InlineData(true, false, "Torrents: no enabled torrent download client")]
    [InlineData(true, true, null)]
    public async Task Is_available_with_an_enabled_indexer_and_client(bool indexerEnabled, bool clientEnabled, string? reason)
    {
        var provider = Provider(new FakeIndexerClient(), out var rows);
        rows.Indexers.ForEach(row => row.Enabled = indexerEnabled);
        rows.Clients.ForEach(row => row.Enabled = clientEnabled);

        var (available, why) = await provider.GetAvailabilityAsync(CancellationToken.None);

        available.Should().Be(reason is null);
        why.Should().Be(reason);
    }

    [Fact]
    public async Task A_usenet_indexer_is_not_asked_by_the_torrent_source()
    {
        var indexer = new FakeIndexerClient();
        var provider = Provider(indexer, out var rows);
        rows.Indexers.ForEach(row => row.Protocol = DownloadProtocol.Usenet);

        var (available, _) = await provider.GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("magnet:?dn=x&xt=urn%3Abtih%3A0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("magnet:?xt=urn:btmh:1220abcd", null)]
    [InlineData("https://example/1.torrent", null)]
    [InlineData(null, null)]
    public void Reads_the_info_hash_out_of_a_magnet(string? magnet, string? expected) =>
        Magnets.InfoHash(magnet).Should().Be(expected);

    private static SongSearchRequest Request() =>
        new(1, "Digital Love", "Daft Punk", ["Daft Punk"], "Discovery", DurationMs, VersionFlags.None)
        {
            MbRecordingId = Recording,
            AlbumMbReleaseId = AlbumRelease,
            AlbumTrackNo = 3,
        };

    private static long QualityId(string name) => SeedData.Qualities.Single(quality => quality.Name == name).Id;

    private static IndexerSourceProvider Provider(
        FakeIndexerClient client,
        out Rows rows,
        int indexerCount = 1,
        DownloadProtocol protocol = DownloadProtocol.Torrent,
        TimeSpan? budget = null)
    {
        rows = new Rows(
            [.. Enumerable.Range(1, indexerCount).Select(id => new Indexer
            {
                Id = id,
                Name = $"Indexer {id}",
                Type = protocol == DownloadProtocol.Torrent ? "torznab" : "newznab",
                Protocol = protocol,
                Priority = 25,
            })],
            [new DownloadClient { Id = 1, Name = "Client", Type = "qbittorrent", Protocol = protocol }]);

        var services = new ServiceCollection();
        services.AddSingleton<IIndexerService>(new FakeIndexerService(rows));
        services.AddSingleton<IDownloadClientService>(new FakeDownloadClientService(rows));
        services.AddSingleton<IMusicBrainzClient>(new FakeMusicBrainz());
        var provider = services.BuildServiceProvider();

        return new IndexerSourceProvider(
            protocol,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new SingleClientFactory(client),
            TimeProvider.System,
            NullLogger<IndexerSourceProvider>.Instance,
            budget);
    }

    private static IndexerRelease Release(
        string title,
        string guid,
        int? seeders = null,
        int? grabs = null,
        string? downloadUrl = null,
        string? magnetUrl = null,
        string? infoHash = null,
        long? size = null,
        DownloadProtocol protocol = DownloadProtocol.Torrent) =>
        new(title, guid, downloadUrl, magnetUrl, infoHash, size, DateTimeOffset.UtcNow.AddDays(-30), seeders, null, grabs, [3040], 1, null, protocol, 1, "Indexer 1", null);

    /// <summary>A twelve-track FLAC album with its cover: track 3 is the song.</summary>
    private static byte[] AlbumTorrent(string name, string[]? titles = null)
    {
        var tracks = titles ?? DiscoveryTracks;
        var entries = tracks
            .Select((title, index) => (Path: $"{index + 1:00} - {title}.flac", Size: FlacTrackSize))
            .Append((Path: "cover.jpg", Size: 250_000L))
            .ToArray();

        var writer = new BencodeWriter().BeginDict().Str("info").BeginDict().Str("name").Str(name).Str("files").BeginList();

        foreach (var (path, size) in entries)
        {
            writer.BeginDict().Str("length").Int(size).Str("path").BeginList().Str(path).End().End();
        }

        return writer.End().Str("piece length").Int(32_768).End().End().ToArray();
    }

    private static byte[] AlbumNzb() =>
        Nzb([.. DiscoveryTracks.Select((title, index) => ($"{index + 1:00} - {title}.flac", FlacTrackSize)), ("Discovery.par2", 40_000L)]);

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

    private sealed record Rows(List<Indexer> Indexers, List<DownloadClient> Clients);

    private sealed class FakeIndexerClient : IIndexerClient
    {
        public Func<ReleaseQuery, IReadOnlyList<IndexerRelease>> Answer { get; init; } = _ => [];

        public Func<IndexerRelease, IndexerDownload> Download { get; init; } = _ => throw new IndexerException("no download");

        public Func<long, bool> Hang { get; init; } = _ => false;

        public Func<long, bool> Fail { get; init; } = _ => false;

        public ConcurrentQueue<ReleaseQuery> QueryLog { get; } = new();

        public List<ReleaseQuery> Queries => [.. QueryLog];

        public int Downloads => _downloads;

        private int _downloads;

        public async Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken)
        {
            if (indexer.Id == 1)
            {
                QueryLog.Enqueue(query);
            }

            if (Hang(indexer.Id))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Fail(indexer.Id))
            {
                throw new IndexerException("The indexer answered 500.");
            }

            return [.. Answer(query).Select(release => release with { IndexerId = indexer.Id, IndexerName = indexer.Name })];
        }

        public Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloads);
            return Task.FromResult(Download(release));
        }
    }

    private sealed class SingleClientFactory(IIndexerClient client) : IIndexerClientFactory
    {
        public IIndexerClient GetClient(Indexer indexer) => client;
    }

    private sealed class FakeIndexerService(Rows rows) : IIndexerService
    {
        public Task<IReadOnlyList<Indexer>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Indexer>>(rows.Indexers);

        public Task<Indexer?> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Indexer> CreateAsync(IndexerDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Indexer?> UpdateAsync(long id, IndexerDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderTestResult> TestAsync(IndexerDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDownloadClientService(Rows rows) : IDownloadClientService
    {
        public Task<IReadOnlyList<DownloadClient>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadClient>>(rows.Clients);

        public Task<DownloadClient?> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClient> CreateAsync(DownloadClientDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadClient?> UpdateAsync(long id, DownloadClientDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderTestResult> TestAsync(DownloadClientDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// Discovery (the album context, track 3), the same album again from another country, the single,
    /// a compilation, and a live album, which ranks last and is never reached.
    /// </summary>
    private sealed class FakeMusicBrainz : IMusicBrainzClient
    {
        private static readonly MbArtistCredit[] DaftPunk = [new() { Name = "Daft Punk" }];

        public Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(All().FirstOrDefault(release => release.Id == releaseId));

        public Task<IReadOnlyList<MbRelease>> GetReleasesForRecordingAsync(
            string recordingId,
            int maxPages = 3,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MbRelease>>(
                recordingId == Recording ? [.. All().Reverse().Select(release => release with { Media = [] })] : []);

        public Task<MbRecordingSearchResult> SearchRecordingsAsync(string luceneQuery, int limit = 25, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MbRecording>> GetRecordingsByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MbReleaseGroupSearchResult> SearchReleaseGroupsAsync(string luceneQuery, int limit = 25, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MbRelease>> GetReleasesForReleaseGroupAsync(string releaseGroupId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static IEnumerable<MbRelease> All() =>
        [
            Release(AlbumRelease, "Discovery", "2001-03-12", "Album", [], DaftPunk, 3),
            Release("rel-discovery-jp", "Discovery", "2001-03-07", "Album", [], DaftPunk, 3),
            Release("rel-single", "Digital Love", "2001-06-11", "Single", [], DaftPunk, 1),
            Release("rel-comp", "Musique Club Hits", "2005-01-01", "Album", ["Compilation"], [new() { Name = "Various Artists" }], 9),
            Release("rel-live", "Alive 2007", "2007-11-19", "Album", ["Live"], DaftPunk, 4),
        ];

        private static MbRelease Release(
            string id,
            string title,
            string date,
            string primaryType,
            string[] secondaryTypes,
            MbArtistCredit[] credit,
            int position) => new()
            {
                Id = id,
                Title = title,
                Date = date,
                Status = "Official",
                ArtistCredit = credit,
                ReleaseGroup = new MbReleaseGroup
                {
                    Id = "rg-" + id,
                    Title = title,
                    PrimaryType = primaryType,
                    SecondaryTypes = secondaryTypes,
                    FirstReleaseDate = date,
                },
                Media =
                [
                    new MbMedium
                    {
                        Position = 1,
                        TrackCount = 12,
                        Tracks = [new MbTrack { Id = "t-" + id, Position = position, Title = "Digital Love", Recording = new MbRecording { Id = Recording } }],
                    },
                ],
            };
    }
}
