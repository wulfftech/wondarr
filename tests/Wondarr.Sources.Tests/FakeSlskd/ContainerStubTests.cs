using System.Net;
using System.Net.Http.Json;
using FakeSlskd;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.FakeSlskd;

/// <summary>
/// The Phase 7 gate's container stub against Wondarr's real indexer, qBittorrent and SABnzbd clients
/// over real HTTP: what the CI gate relies on, checked without Docker.
/// </summary>
public sealed class ContainerStubTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wondarr-containers-").FullName;
    private readonly int _port = FreePort();
    private readonly SocketsHttpHandler _http = new();
    private WebApplication? _app;
    private FakeSlskdState? _state;

    private string Base => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        var options = new FakeSlskdOptions
        {
            Configuration = new SlskdConfiguration(),
            ContainersPort = _port,
            ContainersRoot = _root,
            AudioGenerator = new TestAudioGenerator(),
        };

        _state = new FakeSlskdState(options);
        _app = ContainerStubApp.Build(options, _state);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail the test.
        }
    }

    public void Dispose()
    {
        _state?.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task A_torrent_is_found_downloaded_and_only_its_selected_file_rendered()
    {
        var registered = await RegisterAsync("torrent", "Queen - A Night at the Opera (1975) [FLAC]", obfuscated: false);

        var indexer = Indexer("torznab", DownloadProtocol.Torrent);
        var torznab = TorznabTest.TorznabClient(_http);
        var releases = await torznab.SearchAsync(indexer, new ReleaseQuery("Queen", "A Night at the Opera", 1975), CancellationToken.None);
        var release = releases.Should().ContainSingle().Subject;
        release.InfoHash.Should().Be(registered.InfoHash);
        release.Seeders.Should().Be(25);

        var download = await torznab.DownloadAsync(indexer, release, CancellationToken.None);
        var metainfo = TorrentMetainfo.Parse(download.Content);
        metainfo.InfoHash.Should().Be(registered.InfoHash);
        metainfo.Files.Select(file => file.Path).Should().Equal(
            "Queen - A Night at the Opera (1975) [FLAC]/01 - Death on Two Legs.flac",
            "Queen - A Night at the Opera (1975) [FLAC]/02 - Lazing on a Sunday Afternoon.flac",
            "Queen - A Night at the Opera (1975) [FLAC]/03 - I'm in Love With My Car.flac");

        var client = new QBittorrentClient(
            new QBittorrentProxy(new StaticHttpClientFactory(_http), NullLogger<QBittorrentProxy>.Instance),
            new SecretRegistry(),
            NullLogger<QBittorrentClient>.Instance);
        var row = new DownloadClient { Id = 1, Name = "qBittorrent", Type = "qbittorrent", Protocol = DownloadProtocol.Torrent, Settings = $$"""{"host":"127.0.0.1","port":{{_port}}}""" };

        await client.AddAsync(row, new TorrentAddRequest(metainfo.InfoHash, download.Content, null), CancellationToken.None);
        (await client.GetAsync(row, metainfo.InfoHash, CancellationToken.None))!.Status.Should().Be(TorrentStatus.Stopped);

        await client.SetFilePriorityAsync(row, metainfo.InfoHash, [0, 2], 0, CancellationToken.None);
        await client.StartAsync(row, metainfo.InfoHash, CancellationToken.None);

        var files = await WaitAsync(async () =>
        {
            var current = await client.GetFilesAsync(row, metainfo.InfoHash, CancellationToken.None);
            return current[1].Progress >= 1 ? current : null;
        });

        files.Select(file => file.Progress).Should().Equal(0, 1, 0);
        var info = await client.GetAsync(row, metainfo.InfoHash, CancellationToken.None);
        info!.Status.Should().Be(TorrentStatus.Completed);
        File.Exists(Path.Combine(info.SavePath, "Queen - A Night at the Opera (1975) [FLAC]", "02 - Lazing on a Sunday Afternoon.flac")).Should().BeTrue();
        File.Exists(Path.Combine(info.SavePath, "Queen - A Night at the Opera (1975) [FLAC]", "01 - Death on Two Legs.flac")).Should().BeFalse();
    }

    [Fact]
    public async Task An_nzb_is_added_paused_trimmed_unpacked_and_deleted_with_its_files()
    {
        await RegisterAsync("usenet", "Queen - A Night at the Opera (1975) [FLAC]", obfuscated: false);

        var indexer = Indexer("newznab", DownloadProtocol.Usenet);
        var newznab = TorznabTest.NewznabClient(_http);
        var release = (await newznab.SearchAsync(indexer, new ReleaseQuery("Queen", "A Night at the Opera", null), CancellationToken.None)).Single();
        var download = await newznab.DownloadAsync(indexer, release, CancellationToken.None);
        using (var stream = new MemoryStream(download.Content!))
        {
            NzbFileList.IsClean(NzbFileList.Parse(stream)).Should().BeTrue();
        }

        var client = new SabnzbdClient(new SabnzbdProxy(new StaticHttpClientFactory(_http), new SecretRegistry(), NullLogger<SabnzbdProxy>.Instance));
        var row = new DownloadClient { Id = 2, Name = "SABnzbd", Type = "sabnzbd", Protocol = DownloadProtocol.Usenet, Settings = $$"""{"host":"127.0.0.1","port":{{_port}},"urlBase":"sabnzbd","apiKey":"k"}""" };

        var job = await client.AddAsync(row, new UsenetAddRequest(release.Title, download.Content, null, Paused: true), CancellationToken.None);
        (await client.GetAsync(row, job, CancellationToken.None))!.Status.Should().Be(UsenetStatus.Paused);

        var files = await client.GetFilesAsync(row, job, CancellationToken.None);
        files.Should().HaveCount(3);

        foreach (var fileId in NzbTrimmer.FilesToDelete(files, name => name.Contains("Lazing", StringComparison.Ordinal)))
        {
            await client.DeleteFileAsync(row, job, fileId, CancellationToken.None);
        }

        await client.ResumeAsync(row, job, CancellationToken.None);

        var finished = await WaitAsync(async () =>
        {
            var current = await client.GetAsync(row, job, CancellationToken.None);
            return current?.Status == UsenetStatus.Completed ? current : null;
        });

        Directory.GetFiles(finished.StoragePath!).Select(Path.GetFileName).Should().Equal("02 - Lazing on a Sunday Afternoon.flac");

        await client.RemoveAsync(row, job, deleteFiles: true, CancellationToken.None);
        (await client.GetAsync(row, job, CancellationToken.None)).Should().BeNull();
        Directory.Exists(finished.StoragePath).Should().BeFalse();
    }

    [Fact]
    public async Task The_client_types_pass_their_connection_tests_against_the_stub()
    {
        var qbittorrent = new QBittorrentClientType(new QBittorrentProxy(new StaticHttpClientFactory(_http), NullLogger<QBittorrentProxy>.Instance));
        var sabnzbd = new SabnzbdClientType(new SabnzbdProxy(new StaticHttpClientFactory(_http), new SecretRegistry(), NullLogger<SabnzbdProxy>.Instance));

        using var qbit = System.Text.Json.JsonDocument.Parse($$"""{"host":"127.0.0.1","port":{{_port}}}""");
        using var sab = System.Text.Json.JsonDocument.Parse($$"""{"host":"127.0.0.1","port":{{_port}},"urlBase":"sabnzbd","apiKey":"k"}""");

        (await qbittorrent.TestAsync(qbit.RootElement, CancellationToken.None)).Should().Be(new ProviderTestResult(true, null));
        (await sabnzbd.TestAsync(sab.RootElement, CancellationToken.None)).Should().Be(new ProviderTestResult(true, null));
    }

    private async Task<(int Id, string InfoHash)> RegisterAsync(string protocol, string title, bool obfuscated)
    {
        using var http = new HttpClient(_http, disposeHandler: false);
        var tracks = new[] { "Death on Two Legs", "Lazing on a Sunday Afternoon", "I'm in Love With My Car" };
        var body = new
        {
            protocol,
            title,
            obfuscated,
            files = tracks.Select((track, index) => new
            {
                path = $"{index + 1:00} - {track}.flac",
                length = 180,
                audio = new { codec = "flac", durationSeconds = 180, seed = index + 1 },
                identity = new { recordingId = $"rec-{index}", title = track, durationSeconds = 180 },
            }),
        };

        using var response = await http.PostAsJsonAsync(new Uri($"{Base}/fake/containers"), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var registered = await response.Content.ReadFromJsonAsync<RegisteredRelease>();

        return (registered!.Id, registered.InfoHash);
    }

    private Indexer Indexer(string type, DownloadProtocol protocol) => new()
    {
        Id = 1,
        Name = "Gate indexer",
        Type = type,
        Protocol = protocol,
        Settings = $$"""{"url":"{{Base}}/{{type}}","apiPath":"/api","apiKey":"k","categories":"3000"}""",
    };

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("the stub never got there");
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private sealed record RegisteredRelease(int Id, string InfoHash);
}
