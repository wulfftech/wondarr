using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Torznab.Clients;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>The SABnzbd client (P7-06) against recorded JSON answers; no request leaves the process.</summary>
public sealed class SabnzbdClientTests
{
    private const string ApiKey = "5abApiKey0123456789";
    private const string JobId = "SABnzbd_nzo_p86tgx";

    private static readonly string FixtureDirectory = Path.Combine(
        Path.GetDirectoryName(TorznabFixtures.Directory)!,
        "sabnzbd");

    [Fact]
    public async Task Adds_a_file_paused_with_its_name_and_category_and_reads_the_job_id()
    {
        var server = new FakeSabnzbd { ["addfile"] = "addfile-ok.json" };
        var client = Client(server);

        var id = await client.AddAsync(Row(), new UsenetAddRequest("Daft Punk - Discovery", Encoding.UTF8.GetBytes("<nzb/>"), null, Paused: true), CancellationToken.None);

        id.Should().Be(JobId);
        var request = server.Requests.Single();
        request.Method.Should().Be("POST");
        request.Query["cat"].Should().Be("wondarr");
        request.Query["priority"].Should().Be("-2");
        request.Query["nzbname"].Should().Be("Daft Punk - Discovery");
        request.Query["apikey"].Should().Be(ApiKey);
        request.Body.Should().Contain("name=name").And.Contain("filename=\"Daft Punk - Discovery.nzb\"").And.Contain("application/x-nzb").And.Contain("<nzb/>");
    }

    [Fact]
    public async Task Adds_a_url_running_with_the_default_priority()
    {
        var server = new FakeSabnzbd { ["addurl"] = "addfile-ok.json" };

        await Client(server).AddAsync(Row(), new UsenetAddRequest("Album", null, "https://indexer.example/get/1?apikey=x", Paused: false), CancellationToken.None);

        var request = server.Requests.Single();
        request.Method.Should().Be("GET");
        request.Query["name"].Should().Be("https://indexer.example/get/1?apikey=x");
        request.Query["priority"].Should().Be("-100");
    }

    [Fact]
    public async Task A_refused_add_surfaces_sabnzbd_s_error_without_the_url()
    {
        var server = new FakeSabnzbd { ["addfile"] = "addfile-error.json" };

        var act = () => Client(server).AddAsync(Row(), new UsenetAddRequest("Album", [1], null, true), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<DownloadClientException>();
        thrown.Which.Message.Should().Be("SABnzbd: No NZB found in the upload");
        thrown.Which.Message.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task A_wrong_key_is_named_as_such()
    {
        var server = new FakeSabnzbd { ["get_files"] = "api-key-incorrect.json" };

        var act = () => Client(server).GetFilesAsync(Row(), JobId, CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be("SABnzbd refused the API key.");
    }

    [Fact]
    public async Task Lists_a_job_s_files_and_deletes_and_resumes_by_id()
    {
        var server = new FakeSabnzbd { ["get_files"] = "get-files.json", ["queue"] = "ok.json" };
        var client = Client(server);

        var files = await client.GetFilesAsync(Row(), JobId, CancellationToken.None);
        await client.DeleteFileAsync(Row(), JobId, "SABnzbd_nzf_a1", CancellationToken.None);
        await client.ResumeAsync(Row(), JobId, CancellationToken.None);

        files.Should().HaveCount(5);
        files[2].Should().Be(new UsenetFileInfo("SABnzbd_nzf_a3", "03 - Digital Love.flac", 34_603_008));
        server.Requests[1].Query.Should().Contain("name", "delete_nzf").And.Contain("value", JobId).And.Contain("value2", "SABnzbd_nzf_a1");
        server.Requests[2].Query.Should().Contain("name", "resume").And.Contain("value", JobId);
    }

    [Fact]
    public async Task A_queued_job_is_read_from_the_queue()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue.json" };

        var job = await Client(server).GetAsync(Row(), JobId, CancellationToken.None);

        job.Should().Be(new UsenetJobInfo(JobId, "Daft Punk - Discovery (2001) [FLAC]", UsenetStatus.Downloading, 0.75, 400L * 1024 * 1024, null, null));
        server.Requests.Select(request => request.Mode).Should().Equal("queue");
    }

    [Fact]
    public async Task A_finished_job_is_read_from_the_history_with_its_folder_mapped()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue-empty.json", ["history"] = "history-completed.json" };

        var job = await Client(server).GetAsync(Row(mapping: ("/downloads", "/mnt/usenet")), JobId, CancellationToken.None);

        job!.Status.Should().Be(UsenetStatus.Completed);
        job.Progress.Should().Be(1);
        job.StoragePath.Should().Be(Path.Combine("/mnt/usenet", "complete", "music", "Daft Punk - Discovery (2001) [FLAC]"));
        server.Requests.Select(request => request.Mode).Should().Equal("queue", "history");
        server.Requests[1].Query["nzo_ids"].Should().Be(JobId);
    }

    [Fact]
    public async Task A_failed_job_carries_sabnzbd_s_message()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue-empty.json", ["history"] = "history-failed.json" };

        var job = await Client(server).GetAsync(Row(), JobId, CancellationToken.None);

        job!.Status.Should().Be(UsenetStatus.Failed);
        job.FailMessage.Should().Be("Repair failed, not enough repair blocks (30 short)");
        job.StoragePath.Should().BeNull();
    }

    [Fact]
    public async Task A_job_in_neither_list_is_null()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue-empty.json", ["history"] = "history-empty.json" };

        (await Client(server).GetAsync(Row(), JobId, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("Grabbing", UsenetStatus.Queued)]
    [InlineData("Queued", UsenetStatus.Queued)]
    [InlineData("Propagating", UsenetStatus.Queued)]
    [InlineData("Paused", UsenetStatus.Paused)]
    [InlineData("Downloading", UsenetStatus.Downloading)]
    [InlineData("Fetching", UsenetStatus.Downloading)]
    [InlineData("Checking", UsenetStatus.Downloading)]
    [InlineData("QuickCheck", UsenetStatus.Downloading)]
    [InlineData("Verifying", UsenetStatus.Verifying)]
    [InlineData("Repairing", UsenetStatus.Repairing)]
    [InlineData("Extracting", UsenetStatus.Extracting)]
    [InlineData("Running", UsenetStatus.Extracting)]
    [InlineData("Moving", UsenetStatus.Moving)]
    [InlineData("Completed", UsenetStatus.Completed)]
    [InlineData("Failed", UsenetStatus.Failed)]
    [InlineData("Deleted", UsenetStatus.Failed)]
    public void Maps_every_sabnzbd_status(string word, UsenetStatus expected) =>
        SabnzbdClient.MapStatus(word).Should().Be(expected);

    [Fact]
    public async Task Removes_a_queued_job_from_the_queue()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue.json" };

        await Client(server).RemoveAsync(Row(), JobId, deleteFiles: true, CancellationToken.None);

        var delete = server.Requests.Last();
        delete.Mode.Should().Be("queue");
        delete.Query.Should().Contain("name", "delete").And.Contain("del_files", "1").And.Contain("value", JobId);
    }

    [Fact]
    public async Task Removes_a_finished_job_from_the_history_for_good()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue-empty.json", ["history"] = "ok.json" };

        await Client(server).RemoveAsync(Row(), JobId, deleteFiles: false, CancellationToken.None);

        var delete = server.Requests.Last();
        delete.Mode.Should().Be("history");
        delete.Query.Should().Contain("name", "delete").And.Contain("del_files", "0").And.Contain("archive", "0");
    }

    [Fact]
    public async Task Trims_a_clean_album_to_the_wanted_tracks_and_keeps_par2_and_sidecars()
    {
        var files = await Client(new FakeSabnzbd { ["get_files"] = "get-files.json" }).GetFilesAsync(Row(), JobId, CancellationToken.None);

        var deleted = NzbTrimmer.FilesToDelete(files, name => name.Contains("Digital Love", StringComparison.Ordinal) || name.Contains("Aerodynamic", StringComparison.Ordinal));

        deleted.Should().Equal("SABnzbd_nzf_a1");
    }

    [Fact]
    public void Trims_nothing_from_a_rar_set()
    {
        UsenetFileInfo[] files =
        [
            new("1", "Daft Punk - Discovery.part01.rar", 50_000_000),
            new("2", "Daft Punk - Discovery.part02.rar", 50_000_000),
            new("3", "Daft Punk - Discovery.par2", 40_000),
        ];

        NzbTrimmer.FilesToDelete(files, _ => false).Should().BeEmpty();
    }

    [Fact]
    public void Trims_nothing_from_an_obfuscated_set()
    {
        UsenetFileInfo[] files =
        [
            new("1", "a1b2c3d4e5f6a7b8c9d0e1f2.flac", 30_000_000),
            new("2", "f0e1d2c3b4a5968778695a4b.flac", 30_000_000),
        ];

        NzbTrimmer.FilesToDelete(files, name => name.StartsWith("a1", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_test_passes_when_the_category_s_folder_is_visible()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "music"));
        var server = Configured();

        var result = await new SabnzbdClientType(Proxy(server)).TestAsync(Settings(mapping: ("/downloads/complete", folder.Path)), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        server.Requests.First().Mode.Should().Be("version");
    }

    [Fact]
    public async Task The_test_reports_a_wrong_key()
    {
        var server = new FakeSabnzbd { ["version"] = "version.json", ["get_config"] = "api-key-incorrect.json" };

        var result = await new SabnzbdClientType(Proxy(server)).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("SABnzbd refused the API key.");
    }

    [Fact]
    public async Task The_test_asks_for_a_missing_category()
    {
        var result = await new SabnzbdClientType(Proxy(Configured())).TestAsync(Settings(category: "songs"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Create the category 'songs'");
    }

    [Fact]
    public async Task The_test_asks_for_a_mapping_when_the_folder_is_invisible()
    {
        var result = await new SabnzbdClientType(Proxy(Configured())).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("/downloads/complete/music").And.Contain("add a remote path mapping");
    }

    [Theory]
    [InlineData("/downloads/complete", "music", "/downloads/complete/music")]
    [InlineData("/downloads/complete/", "", "/downloads/complete/")]
    [InlineData("/downloads/complete", "/elsewhere/music", "/elsewhere/music")]
    [InlineData(@"D:\Usenet\Complete", "music", @"D:\Usenet\Complete\music")]
    public void Joins_the_category_folder_onto_the_complete_folder(string complete, string category, string expected) =>
        SabnzbdClientType.CategoryFolder(complete, category).Should().Be(expected);

    [Fact]
    public void Validation_requires_a_host_and_a_key()
    {
        using var document = JsonDocument.Parse("""{"port": 70000}""");

        new SabnzbdClientType(Proxy(new FakeSabnzbd())).Validate(document.RootElement)
            .Should().Contain(["A host is required.", "The port must be between 1 and 65535.", "An API key is required."]);
    }

    [Fact]
    public async Task The_key_is_registered_as_a_secret_and_never_logged()
    {
        var server = new FakeSabnzbd { ["queue"] = "queue.json" };
        var secrets = new SecretRegistry();
        var logger = new CapturingLogger<SabnzbdProxy>();
        var client = new SabnzbdClient(new SabnzbdProxy(new StaticHttpClientFactory(server), secrets, logger));

        await client.GetAsync(Row(), JobId, CancellationToken.None);

        secrets.Redact($"key={ApiKey}").Should().NotContain(ApiKey);
        logger.Lines.Should().NotBeEmpty().And.OnlyContain(line => !line.Contains(ApiKey, StringComparison.Ordinal));
    }

    private static FakeSabnzbd Configured() => new()
    {
        ["version"] = "version.json",
        ["get_config:misc"] = "config-misc.json",
        ["get_config:categories"] = "config-categories.json",
    };

    private static SabnzbdClient Client(FakeSabnzbd server) => new(Proxy(server));

    private static SabnzbdProxy Proxy(FakeSabnzbd server) =>
        new(new StaticHttpClientFactory(server), new SecretRegistry(), new CapturingLogger<SabnzbdProxy>());

    private static DownloadClient Row((string Remote, string Local)? mapping = null) => new()
    {
        Id = 7,
        Name = "SABnzbd",
        Type = "sabnzbd",
        Protocol = DownloadProtocol.Usenet,
        Settings = Settings(mapping: mapping).GetRawText(),
    };

    private static JsonElement Settings(string category = "wondarr", (string Remote, string Local)? mapping = null)
    {
        var mappings = mapping is { } pair
            ? $$"""[{"key": {{JsonSerializer.Serialize(pair.Remote)}}, "value": {{JsonSerializer.Serialize(pair.Local)}}}]"""
            : "[]";

        using var document = JsonDocument.Parse($$"""
            {"host": "sab.example", "port": 8080, "apiKey": "{{ApiKey}}", "category": "{{category}}", "remotePathMappings": {{mappings}}}
            """);

        return document.RootElement.Clone();
    }

    /// <summary>One request as SABnzbd saw it.</summary>
    private sealed record SeenRequest(string Method, string Mode, IReadOnlyDictionary<string, string> Query, string Body);

    /// <summary>
    /// Answers by <c>mode</c> (or <c>mode:section</c> for <c>get_config</c>) with a fixture file, and
    /// keeps every request with its query and body.
    /// </summary>
    private sealed class FakeSabnzbd : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);

        public List<SeenRequest> Requests { get; } = [];

        public string this[string mode]
        {
            set => _answers[mode] = value;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = TorznabTest.Query(request.RequestUri!);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var mode = query.GetValueOrDefault("mode", string.Empty);
            Requests.Add(new SeenRequest(request.Method.Method, mode, query, body));

            var key = mode == "get_config" ? string.Concat(mode, ":", query.GetValueOrDefault("section")) : mode;
            var fixture = _answers.GetValueOrDefault(key) ?? _answers.GetValueOrDefault(mode);

            return fixture is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(File.ReadAllText(Path.Combine(FixtureDirectory, fixture)), Encoding.UTF8, "application/json"),
                };
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("wondarr-sab-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
