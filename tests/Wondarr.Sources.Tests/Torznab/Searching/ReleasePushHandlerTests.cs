using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.DownloadClients;
using Wondarr.Core.Paging;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Wanted;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Tests.Torznab.Parsing;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary><c>release/push</c> for torrents and NZBs (P7-07b): the match, the file list, the engine's verdict.</summary>
public sealed class ReleasePushHandlerTests
{
    private const string Album = "Daft Punk - Discovery (2001) [FLAC]";
    private const long TrackSize = 33_000_000;

    private static readonly string[] Titles = ["One More Time", "Aerodynamic", "Digital Love", "Crescendolls"];

    private readonly List<(long SongId, Candidate Candidate)> _judged = [];
    private readonly ISongSearchService _search = Substitute.For<ISongSearchService>();
    private readonly IDownloadClientService _clients = Substitute.For<IDownloadClientService>();
    private readonly IWantedService _wanted = Substitute.For<IWantedService>();
    private readonly List<Uri> _downloads = [];
    private byte[] _container = AlbumTorrent();

    public ReleasePushHandlerTests()
    {
        _clients.ListAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new DownloadClient { Id = 1, Name = "qBittorrent", Type = "qbittorrent", Protocol = DownloadProtocol.Torrent },
            new DownloadClient { Id = 2, Name = "SABnzbd", Type = "sabnzbd", Protocol = DownloadProtocol.Usenet },
        ]);

        // The first song's grab bundles the rest, as the search service does.
        _search.JudgeAsync(Arg.Any<long>(), Arg.Any<IReadOnlyList<Candidate>>(), SearchTrigger.Push, true, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _judged.Add((call.Arg<long>(), call.Arg<IReadOnlyList<Candidate>>().Single()));

                return _judged.Count == 1
                    ? new SongSearchResult(1, SearchOutcome.Grabbed, [], 10, "Grabbed candidate 10.")
                    : new SongSearchResult(0, SearchOutcome.Cancelled, [], null, "Already downloading");
            });

        Wanted(Song(1, "Digital Love", 3), Song(2, "Crescendolls", 4), Song(3, "Get Lucky", 8, album: "Random Access Memories"));
    }

    [Fact]
    public async Task A_torrent_of_an_album_is_judged_for_each_wanted_song_on_it_and_approved()
    {
        var outcome = await Handler().PushAsync(Push("torrent", "https://tracker.example/dl/1?passkey=secret"), CancellationToken.None);

        outcome.Approved.Should().BeTrue();
        outcome.Rejections.Should().BeEmpty();
        outcome.SongId.Should().Be(1);
        _judged.Select(judged => judged.SongId).Should().Equal(1, 2);
        var candidate = _judged[0].Candidate;
        candidate.SourceType.Should().Be(SourceTypes.Torznab);
        candidate.RemotePath.Should().Be($"{Album}/03 - Digital Love.flac");
        candidate.Release!.IndexerId.Should().Be(PushedReleaseClient.IndexerId);
        candidate.Release.IndexerName.Should().Be("autobrr-indexer");
        candidate.Release.ReleaseId.Should().StartWith("push-").And.NotContain("secret");
        candidate.Release.Files.Should().HaveCount(4);
        _downloads.Should().ContainSingle();
    }

    [Fact]
    public async Task An_nzb_is_read_and_judged_as_a_usenet_candidate()
    {
        _container = AlbumNzb();

        var outcome = await Handler().PushAsync(Push("usenet", "https://usenet.example/get/1"), CancellationToken.None);

        outcome.Approved.Should().BeTrue();
        _judged[0].Candidate.SourceType.Should().Be(SourceTypes.Newznab);
        _judged[0].Candidate.RemotePath.Should().Be("03 - Digital Love.flac");
    }

    [Fact]
    public async Task A_magnet_push_is_a_file_list_unknown_candidate()
    {
        var outcome = await Handler().PushAsync(
            Push("torrent", null, magnet: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
            CancellationToken.None);

        outcome.Approved.Should().BeTrue();
        _downloads.Should().BeEmpty();
        _judged[0].Candidate.Availability.FileListKnown.Should().BeFalse();
        _judged[0].Candidate.Release!.InfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public async Task Without_a_client_of_the_protocol_it_is_rejected_with_the_song_it_matched()
    {
        _clients.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var outcome = await Handler().PushAsync(Push("torrent", "https://tracker.example/dl/1"), CancellationToken.None);

        outcome.Approved.Should().BeFalse();
        outcome.Rejections.Should().Equal("No download client for protocol 'torrent'");
        outcome.SongId.Should().Be(1);
        _downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_nobody_wants_is_rejected_unread()
    {
        var outcome = await Handler().PushAsync(Push("torrent", "https://tracker.example/dl/1", title: "Justice - Cross (2007) [FLAC]"), CancellationToken.None);

        outcome.Rejections.Should().Equal("No wanted song matches 'Justice - Cross (2007) [FLAC]'");
        _downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_matching_album_without_the_song_s_file_says_so()
    {
        _container = AlbumTorrent(["One More Time", "Aerodynamic"]);

        var outcome = await Handler().PushAsync(Push("torrent", "https://tracker.example/dl/1"), CancellationToken.None);

        outcome.Approved.Should().BeFalse();
        outcome.Rejections.Should().Equal("'Digital Love' is not in the release", "'Crescendolls' is not in the release");
    }

    [Fact]
    public async Task The_engine_s_rejections_are_passed_on()
    {
        _search.JudgeAsync(Arg.Any<long>(), Arg.Any<IReadOnlyList<Candidate>>(), SearchTrigger.Push, true, Arg.Any<CancellationToken>())
            .Returns(call => new SongSearchResult(
                1,
                SearchOutcome.NoAcceptableCandidate,
                [new CandidateDecision(
                    call.Arg<IReadOnlyList<Candidate>>().Single(),
                    new ScoreBreakdown(0, 0, 0, 0, 0, 0, 0, [], 0, 0, false),
                    [new Rejection(RejectionReason.FormatNotAllowed, "Format not allowed: FLAC")])],
                null,
                "none acceptable"));

        var outcome = await Handler().PushAsync(Push("torrent", "https://tracker.example/dl/1"), CancellationToken.None);

        outcome.Approved.Should().BeFalse();
        outcome.Rejections.Should().Equal("Format not allowed: FLAC");
    }

    private ReleasePushHandler Handler()
    {
        var handler = new RecordingHandler(request =>
        {
            _downloads.Add(request.RequestUri!);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_container) };
        });
        var http = new StaticHttpClientFactory(handler);
        var factory = new IndexerClientFactory(
            TorznabTest.TorznabClient(handler),
            TorznabTest.NewznabClient(handler),
            new PushedReleaseClient(http, NullLogger<PushedReleaseClient>.Instance));

        return new ReleasePushHandler(_wanted, _search, _clients, factory, TimeProvider.System, NullLogger<ReleasePushHandler>.Instance);
    }

    private static PushedRelease Push(string protocol, string? url, string title = Album, string? magnet = null) =>
        new(title, protocol, url, magnet, 400_000_000, "autobrr-indexer", DateTimeOffset.UtcNow);

    private void Wanted(params Song[] songs)
    {
        _wanted.GetMissingAsync(Arg.Any<PagingSpec>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song>(songs, songs.Length));
        _wanted.GetCutoffUnmetAsync(Arg.Any<PagingSpec>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Song>([], 0));
    }

    private static Song Song(long id, string title, int trackNo, string album = "Discovery") => new()
    {
        Id = id,
        Title = title,
        ArtistCredit = "Daft Punk",
        PrimaryArtist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" },
        DurationMs = 301_000,
        AlbumContext = new AlbumContext { AlbumTitle = album, AlbumArtist = "Daft Punk", AlbumKey = "k", TrackNo = trackNo },
    };

    private static byte[] AlbumTorrent(string[]? titles = null)
    {
        var tracks = titles ?? Titles;
        var writer = new BencodeWriter().BeginDict().Str("info").BeginDict().Str("name").Str(Album).Str("files").BeginList();

        for (var index = 0; index < tracks.Length; index++)
        {
            var number = Array.IndexOf(Titles, tracks[index]) + 1;
            writer.BeginDict().Str("length").Int(TrackSize).Str("path").BeginList().Str($"{number:00} - {tracks[index]}.flac").End().End();
        }

        return writer.End().Str("piece length").Int(32_768).End().End().ToArray();
    }

    private static byte[] AlbumNzb()
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">\n");

        for (var index = 0; index < Titles.Length; index++)
        {
            xml.Append($"<file poster=\"p\" date=\"0\" subject=\"[{index + 1}/{Titles.Length}] - &quot;{index + 1:00} - {Titles[index]}.flac&quot; yEnc (1/1)\">")
                .Append("<groups><group>alt.binaries.sounds.flac</group></groups>")
                .Append($"<segments><segment bytes=\"{TrackSize}\" number=\"1\">id{index}@example</segment></segments></file>\n");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</nzb>").ToString());
    }
}
