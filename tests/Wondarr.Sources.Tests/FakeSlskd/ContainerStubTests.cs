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

        // Every client here shares _http, so the connection the warm-up opens is the one they all use.
        using var http = new HttpClient(_http, disposeHandler: false);
        await FakeSlskdHarness.WarmUpAsync(http, new Uri($"{Base}/"));
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
    public async Task An_nzb_is_added_paused_trimmed_unpacked_and_removed_but_its_folder_stays_as_with_sabnzbd()
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

        // The real SABnzbd deletes the files of a failed job only: a completed job's folder stays.
        Directory.Exists(finished.StoragePath).Should().BeTrue();
        (await ReportAsync()).Should().Contain("\"storageExists\":true");
    }

    [Fact]
    public async Task The_stub_says_where_the_wondarr_category_finishes_its_jobs_and_a_finished_job_is_inside_it()
    {
        var (client, row, job) = await AddPostWithSidecarsAsync();
        await client.ResumeAsync(row, job, CancellationToken.None);

        var finished = await WaitAsync(async () => await FinishedAsync(client, row, job));
        var completed = await client.GetCompletedFolderAsync(row, CancellationToken.None);

        completed.Should().Be(Path.Combine(_root, "usenet", "complete", "music"));
        UsenetJobFolder.Refusal(finished.StoragePath!, completed, Path.Combine(_root, "staging", "wondarr", "one"), null).Should().BeNull();
    }

    [Fact]
    public async Task A_post_trimmed_the_way_wondarr_does_it_completes_without_its_par2_volumes_and_sfv()
    {
        var (client, row, job) = await AddPostWithSidecarsAsync();

        var files = await client.GetFilesAsync(row, job, CancellationToken.None);
        files.Select(file => file.FileName).Should().Contain([$"{Title}.par2", $"{Title}.vol000+01.par2", $"{Title}.sfv", $"{Title}.nfo"]);

        var deleted = NzbTrimmer.FilesToDelete(files, name => name.Contains("Lazing", StringComparison.Ordinal));
        deleted.Should().HaveCount(5, "two tracks, the two Par2 files and the SFV; the NFO stays");

        foreach (var fileId in deleted)
        {
            await client.DeleteFileAsync(row, job, fileId, CancellationToken.None);
        }

        await client.ResumeAsync(row, job, CancellationToken.None);

        var finished = await WaitAsync(async () => await FinishedAsync(client, row, job));

        finished.Status.Should().Be(UsenetStatus.Completed);
        Directory.GetFiles(finished.StoragePath!).Select(Path.GetFileName).Should().BeEquivalentTo("02 - Lazing on a Sunday Afternoon.flac", $"{Title}.nfo");
        (await ReportAsync()).Should().Contain("\"failMessage\":null").And.Contain($"\"extras\":[\"{Title}.nfo\"]");
    }

    [Theory]
    [InlineData(".par2")]
    [InlineData(".sfv")]
    public async Task A_trimmed_post_that_still_holds_par2_or_sfv_fails_like_sabnzbd_and_leaves_nothing_in_the_complete_folder(string kept)
    {
        var (client, row, job) = await AddPostWithSidecarsAsync();

        // Only the audio files are dropped, and one repair file stays: its set describes the deleted tracks.
        var files = await client.GetFilesAsync(row, job, CancellationToken.None);
        var drop = files.Where(file =>
            (file.FileName.EndsWith(".flac", StringComparison.Ordinal) && !file.FileName.Contains("Lazing", StringComparison.Ordinal))
            || (kept != ".par2" && file.FileName.EndsWith(".par2", StringComparison.Ordinal))
            || (kept != ".sfv" && file.FileName.EndsWith(".sfv", StringComparison.Ordinal)));

        foreach (var file in drop)
        {
            await client.DeleteFileAsync(row, job, file.FileId, CancellationToken.None);
        }

        await client.ResumeAsync(row, job, CancellationToken.None);

        var failed = await WaitAsync(async () => await FinishedAsync(client, row, job));

        failed.Status.Should().Be(UsenetStatus.Failed);
        failed.FailMessage.Should().MatchRegex(@"^Repair failed, not enough repair blocks \([1-9][0-9]* short\)$");
        failed.StoragePath.Should().BeNull();
        Directory.GetFileSystemEntries(Path.Combine(_root, "usenet", "complete", "music")).Should().BeEmpty("nothing reaches the complete folder");
        (await ReportAsync()).Should().Contain("Repair failed, not enough repair blocks");
    }

    [Fact]
    public async Task A_post_that_was_not_trimmed_completes_with_its_par2_set()
    {
        var (client, row, job) = await AddPostWithSidecarsAsync();

        await client.ResumeAsync(row, job, CancellationToken.None);

        var finished = await WaitAsync(async () => await FinishedAsync(client, row, job));

        finished.Status.Should().Be(UsenetStatus.Completed);
        Directory.GetFiles(finished.StoragePath!).Should().HaveCount(4, "every track is unpacked and the NFO is kept; the repair data is consumed");
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

    [Fact]
    public async Task The_stub_has_the_relative_default_in_misc_like_a_stock_sabnzbd_and_the_real_folder_in_fullstatus()
    {
        using var http = new HttpClient(_http, disposeHandler: false);

        var misc = await http.GetStringAsync(new Uri($"{Base}/sabnzbd/api?mode=get_config&section=misc&apikey=k&output=json"));
        var status = await http.GetStringAsync(new Uri($"{Base}/sabnzbd/api?mode=fullstatus&skip_dashboard=1&apikey=k&output=json"));

        misc.Should().Contain("\"complete_dir\":\"Downloads/complete\"");
        status.Should().Contain("\"completedir\":").And.Contain("complete").And.NotContain("Downloads/complete");
    }

    private const string Title = "Queen - A Night at the Opera (1975) [FLAC]";

    /// <summary>A plain-named post of three tracks with a Par2 set, an SFV and an NFO, added to the fake SABnzbd paused.</summary>
    private async Task<(SabnzbdClient Client, DownloadClient Row, string Job)> AddPostWithSidecarsAsync()
    {
        await RegisterAsync("usenet", Title, obfuscated: false, sidecars: [$"{Title}.par2", $"{Title}.vol000+01.par2", $"{Title}.sfv", $"{Title}.nfo"]);

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

        return (client, row, job);
    }

    /// <summary>The job once it has left the queue for the history, finished or failed.</summary>
    private static async Task<UsenetJobInfo?> FinishedAsync(SabnzbdClient client, DownloadClient row, string job)
    {
        var current = await client.GetAsync(row, job, CancellationToken.None);

        return current?.Status is UsenetStatus.Completed or UsenetStatus.Failed ? current : null;
    }

    private async Task<string> ReportAsync()
    {
        using var http = new HttpClient(_http, disposeHandler: false);

        return await http.GetStringAsync(new Uri($"{Base}/fake/containers"));
    }

    private async Task<(int Id, string InfoHash)> RegisterAsync(string protocol, string title, bool obfuscated, string[]? sidecars = null)
    {
        using var http = new HttpClient(_http, disposeHandler: false);
        var tracks = new[] { "Death on Two Legs", "Lazing on a Sunday Afternoon", "I'm in Love With My Car" };
        var body = new
        {
            protocol,
            title,
            obfuscated,
            sidecars = sidecars ?? [],
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
