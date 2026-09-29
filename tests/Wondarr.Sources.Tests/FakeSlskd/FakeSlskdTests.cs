using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using global::FakeSlskd;
using FluentAssertions;
using Wondarr.Sources.Slskd;
using Xunit;

namespace Wondarr.Sources.Tests.FakeSlskd;

/// <summary>
/// The fake slskd and its AcoustID stub, exercised the way the Phase 2 gate uses them: the app's own
/// response types are used to read the fake's answers, so a shape that drifts away from slskd 0.26
/// fails here rather than in the container.
/// </summary>
public sealed class FakeSlskdTests
{
    private const string Peer = "gate-peer";

    /// <summary>A peer path that the scenario files and the assertions share.</summary>
    private const string TrackPath = @"@@gate\Music\Daft Punk\Get Lucky.mp3";

    private const string RecordingId = "833f00e1-781f-4edd-90e4-e52712618862";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Application_reports_the_configured_username_version_and_share_counts()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson());

        File.WriteAllBytes(Path.Combine(harness.ShareDirectory, "single.mp3"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(harness.ShareDirectory, "album"));
        File.WriteAllBytes(Path.Combine(harness.ShareDirectory, "album", "one.flac"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(harness.ShareDirectory, "album", "notes.txt"), [1]);

        // Read with the app's own supervisor model: this is the shape SlskdHost polls.
        var state = await harness.GetAsync<SlskdApplicationState>("/api/v0/application");

        state.Version.Current.Should().Be("0.26.0");
        state.Server.IsLoggedIn.Should().BeTrue();
        state.Server.State.Should().Be("Connected, LoggedIn");
        state.User.Username.Should().Be("wondarr-test");

        // Like slskd, the fake scanned its shares at start, before the files existed.
        var before = (await harness.GetJsonAsync("/api/v0/application"))["shares"]!;
        before["ready"]!.GetValue<bool>().Should().BeTrue();
        before["directories"]!.GetValue<int>().Should().Be(0);
        before["files"]!.GetValue<int>().Should().Be(0);

        using var rescan = await harness.Slskd.PutAsync(new Uri("api/v0/shares", UriKind.Relative), content: null);
        rescan.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var shares = (await harness.GetJsonAsync("/api/v0/application"))["shares"]!;
        shares["directories"]!.GetValue<int>().Should().Be(2);
        shares["files"]!.GetValue<int>().Should().Be(2);

        var listing = (await harness.GetJsonAsync("/api/v0/shares"))["local"]![0]!;
        listing["files"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Application_reports_logged_out_when_no_username_is_configured()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(), username: null);

        var state = await harness.GetAsync<SlskdApplicationState>("/api/v0/application");

        state.Server.IsLoggedIn.Should().BeFalse();
        state.Server.State.Should().Be("Disconnected");
        state.User.Username.Should().BeEmpty();

        var server = await harness.GetJsonAsync("/api/v0/server");
        server["isLoggedIn"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Shares_lists_each_shared_directory_with_its_audio_files()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson());

        File.WriteAllBytes(Path.Combine(harness.ShareDirectory, "single.mp3"), [1]);
        (await harness.Slskd.PutAsync(new Uri("api/v0/shares", UriKind.Relative), content: null)).EnsureSuccessStatusCode();

        var shares = (await harness.GetJsonAsync("/api/v0/shares"))["local"]!.AsArray();

        shares.Should().ContainSingle();
        shares[0]!["localPath"]!.GetValue<string>().Should().Be(harness.ShareDirectory);
        shares[0]!["alias"]!.GetValue<string>().Should().Be("music");
        shares[0]!["remotePath"]!.GetValue<string>().Should().Be("music");
        shares[0]!["files"]!.GetValue<int>().Should().Be(1);
        shares[0]!["directories"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Api_requests_without_a_configured_key_are_unauthorized()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson());

        using var anonymous = harness.CreateAnonymousClient();

        (await anonymous.GetAsync("/api/v0/application")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v0/application");
        request.Headers.Add(FakeSlskdApp.ApiKeyHeader, "not-the-key");

        using var wrongKey = await anonymous.SendAsync(request);
        wrongKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // GET /fake/log is the gate's own endpoint: loopback only, no key.
        (await anonymous.GetAsync("/fake/log")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_responses_stay_empty_until_the_search_completes()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(searchDelayMs: 60_000));

        var id = Guid.NewGuid();
        var created = await harness.PostAsync<SlskdSearch>(
            "/api/v0/searches",
            new { id, searchText = "daft punk get lucky", searchTimeout = 8000, responseLimit = 100 });

        created.Id.Should().Be(id);
        created.SearchText.Should().Be("daft punk get lucky");
        created.State.Should().Be("InProgress");
        created.IsComplete.Should().BeFalse();
        created.EndedAt.Should().BeNull();

        (await harness.GetAsync<List<SlskdSearchResponse>>($"/api/v0/searches/{id}/responses"))
            .Should().BeEmpty("the real endpoint keeps responses back while the search runs");

        // Unknown ids are 404, like slskd's.
        var unknown = Guid.NewGuid();
        (await harness.Slskd.GetAsync($"/api/v0/searches/{unknown}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await harness.Slskd.GetAsync($"/api/v0/searches/{unknown}/responses")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var stopped = await harness.Slskd.PutAsync($"/api/v0/searches/{id}", content: null);
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);

        var complete = await harness.GetAsync<SlskdSearch>($"/api/v0/searches/{id}");
        complete.IsComplete.Should().BeTrue();
        complete.State.Should().Be("Completed, Cancelled");
        complete.EndedAt.Should().NotBeNull();

        var responses = await harness.GetAsync<List<SlskdSearchResponse>>($"/api/v0/searches/{id}/responses");
        responses.Should().ContainSingle();
        responses[0].Username.Should().Be(Peer);
        responses[0].HasFreeUploadSlot.Should().BeTrue();
        responses[0].UploadSpeed.Should().Be(1146398);
        responses[0].QueueLength.Should().Be(0);
        responses[0].Files.Should().ContainSingle().Which.Filename.Should().Be(TrackPath);
        responses[0].Files[0].BitRate.Should().Be(320);
        responses[0].Files[0].Length.Should().Be(249);

        // A search that is deleted is gone: 204, then 404.
        using var deleted = await harness.Slskd.DeleteAsync($"/api/v0/searches/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await harness.Slskd.GetAsync($"/api/v0/searches/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await harness.Slskd.DeleteAsync($"/api/v0/searches/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_search_completes_by_itself_after_the_scenario_delay()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(searchDelayMs: 150));

        var id = Guid.NewGuid();
        await harness.PostAsync<SlskdSearch>("/api/v0/searches", new { id, searchText = "daft punk get lucky" });

        var complete = await WaitForAsync(
            async () => await harness.GetAsync<SlskdSearch>($"/api/v0/searches/{id}"),
            search => search.IsComplete,
            "the search to complete on its own");

        complete.State.Should().Be("Completed, TimedOut");
        complete.ResponseCount.Should().Be(1);
        complete.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task Matching_folds_diacritics_and_requires_every_search_token()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(
            searchDelayMs: 60_000,
            files:
            [
                Offer(@"@@peer-a\Music\Björk\Jóga.flac", length: 300),
                Track(),
            ]));

        (await ResponsesForAsync(harness, "bjork joga")).Should().ContainSingle()
            .Which.Files[0].Filename.Should().Be(@"@@peer-a\Music\Björk\Jóga.flac");

        // Token order does not matter.
        (await ResponsesForAsync(harness, "joga bjork")).Should().ContainSingle();

        // An extra token that the path does not contain is not a match...
        (await ResponsesForAsync(harness, "bjork joga remix")).Should().BeEmpty();

        // ...and neither is a token the path does not have at all.
        (await ResponsesForAsync(harness, "bjork dao")).Should().BeEmpty();
    }

    [Fact]
    public async Task Searches_above_the_in_flight_limit_are_rejected()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(searchDelayMs: 60_000, maxInFlight: 2));

        foreach (var _ in new[] { 1, 2 })
        {
            using var accepted = await harness.PostRawAsync(
                "/api/v0/searches",
                new { id = Guid.NewGuid(), searchText = "daft punk get lucky" });

            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var rejected = await harness.PostRawAsync(
            "/api/v0/searches",
            new { id = Guid.NewGuid(), searchText = "sigur ros hoppipolla" });

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_gate_log_records_posts_deletes_and_the_search_budget()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(searchDelayMs: 60_000, maxInFlight: 2));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await harness.PostAsync<SlskdSearch>("/api/v0/searches", new { id = first, searchText = "daft punk get lucky" });
        await harness.PostAsync<SlskdSearch>("/api/v0/searches", new { id = second, searchText = "sigur ros hoppipolla" });

        using var deleted = await harness.Slskd.DeleteAsync($"/api/v0/searches/{second}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var log = await harness.GetJsonAsync("/fake/log");
        var searches = log["searches"]!.AsArray();

        log["maxInFlight"]!.GetValue<int>().Should().Be(2);
        log["transfers"]!.AsArray().Should().BeEmpty();
        searches.Should().HaveCount(2);

        var firstEntry = searches.Single(entry => entry!["id"]!.GetValue<Guid>() == first)!;
        var secondEntry = searches.Single(entry => entry!["id"]!.GetValue<Guid>() == second)!;

        firstEntry["text"]!.GetValue<string>().Should().Be("daft punk get lucky");
        secondEntry["text"]!.GetValue<string>().Should().Be("sigur ros hoppipolla");
        firstEntry["deletedAt"].Should().BeNull();
        secondEntry["deletedAt"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        foreach (var entry in searches)
        {
            entry!["postedAt"]!.GetValue<string>().Should().MatchRegex(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        }
    }

    [Fact]
    public async Task A_download_completes_moves_the_file_and_posts_the_webhook()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(searchDelayMs: 20, transferDelayMs: 20));

        var batch = await PostBatchAsync(harness, destination: "track-1");

        batch.Failures.Should().BeEmpty();
        batch.Batch.Username.Should().Be(Peer);

        var transfer = await harness.WaitForTransferAsync(Peer, t => t.State == "Completed, Succeeded", "completed");

        transfer.Size.Should().Be(TestAudioGenerator.GeneratedSize);
        transfer.BytesTransferred.Should().Be(TestAudioGenerator.GeneratedSize);
        transfer.PercentComplete.Should().Be(100);
        transfer.BytesRemaining.Should().Be(0);
        transfer.PlaceInQueue.Should().BeNull();
        transfer.EndedAt.Should().NotBeNull();

        var expected = Path.Combine(harness.DownloadsDirectory, "track-1", "Get Lucky.mp3");
        File.Exists(expected).Should().BeTrue();
        new FileInfo(expected).Length.Should().Be(TestAudioGenerator.GeneratedSize);

        // The incomplete directory is only a staging area.
        Directory.EnumerateFiles(harness.IncompleteDirectory, "*", SearchOption.AllDirectories)
            .Should().BeEmpty();

        var fetched = await harness.GetAsync<SlskdTransferResource>(
            $"/api/v0/transfers/downloads/{Peer}/{transfer.Id}");

        fetched.State.Should().Be("Completed, Succeeded");

        var hook = await harness.Webhooks.WaitAsync(TimeSpan.FromSeconds(5));
        hook.Headers[FakeSlskdHarness.WebhookHeaderName].Should().Be(FakeSlskdHarness.WebhookHeaderValue);

        var payload = JsonNode.Parse(hook.Body)!;
        payload["type"]!.GetValue<string>().Should().Be("DownloadFileComplete");
        payload["localFilename"]!.GetValue<string>().Should().Be(expected);
        payload["remoteFilename"]!.GetValue<string>().Should().Be(TrackPath);
        payload["transfer"]!["state"]!.GetValue<string>().Should().Be("Completed, Succeeded");
        payload["timestamp"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_rejected_download_ends_rejected_and_a_stalled_one_stays_in_progress()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(
            transferDelayMs: 20,
            files:
            [
                Offer(@"@@gate\Music\Rejected.mp3", transfer: "reject"),
                Offer(@"@@gate\Music\Stalled.mp3", transfer: "stall"),
            ]));

        using var posted = await harness.PostRawAsync(
            "/api/v0/transfers/downloads/batches",
            new
            {
                username = Peer,
                files = new[]
                {
                    new { filename = @"@@gate\Music\Rejected.mp3", size = 1000L },
                    new { filename = @"@@gate\Music\Stalled.mp3", size = 1000L },
                },
            });

        posted.StatusCode.Should().Be(HttpStatusCode.Created);

        var rejected = await harness.WaitForTransferAsync(
            Peer,
            t => t.Filename == @"@@gate\Music\Rejected.mp3" && t.State == "Completed, Rejected",
            "rejected");

        rejected.BytesTransferred.Should().Be(0);

        var stalled = await harness.WaitForTransferAsync(
            Peer,
            t => t.Filename == @"@@gate\Music\Stalled.mp3" && t.State == "InProgress",
            "in progress");

        stalled.BytesTransferred.Should().Be(0);
        stalled.PercentComplete.Should().Be(0);

        (await harness.GetAsync<int>($"/api/v0/transfers/downloads/{Peer}/{stalled.Id}/position"))
            .Should().Be(1);

        // Nothing was written for either file.
        var written = Directory.Exists(harness.DownloadsDirectory)
            ? Directory.EnumerateFiles(harness.DownloadsDirectory, "*", SearchOption.AllDirectories).ToArray()
            : [];

        written.Should().BeEmpty();

        // Cancelling the stalled transfer ends it, as slskd's cancel does.
        using var cancelled = await harness.Slskd.DeleteAsync(
            $"/api/v0/transfers/downloads/{Peer}/{stalled.Id}?remove=false");

        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterCancel = await harness.GetAsync<SlskdTransferResource>(
            $"/api/v0/transfers/downloads/{Peer}/{stalled.Id}");

        afterCancel.State.Should().Be("Completed, Cancelled");

        (await harness.Slskd.GetAsync($"/api/v0/transfers/downloads/{Peer}/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Files_the_peer_does_not_offer_are_reported_as_failures()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(transferDelayMs: 20));

        using var partial = await harness.PostRawAsync(
            "/api/v0/transfers/downloads/batches",
            new
            {
                username = Peer,
                files = new[]
                {
                    new { filename = TrackPath, size = 1000L },
                    new { filename = @"@@gate\Music\NotThere.mp3", size = 1000L },
                },
            });

        partial.StatusCode.Should().Be(HttpStatusCode.MultiStatus);

        var body = await partial.Content.ReadFromJsonAsync<EnqueueBatchResponse>(JsonOptions);
        body!.Failures.Should().ContainSingle().Which.Filename.Should().Be(@"@@gate\Music\NotThere.mp3");

        using var none = await harness.PostRawAsync(
            "/api/v0/transfers/downloads/batches",
            new
            {
                username = Peer,
                files = new[] { new { filename = @"@@gate\Music\NotThere.mp3", size = 1000L } },
            });

        none.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_destination_that_traverses_upwards_is_rejected()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson());

        using var response = await harness.PostRawAsync(
            "/api/v0/transfers/downloads/batches",
            new
            {
                username = Peer,
                files = new[] { new { filename = TrackPath, size = 1000L } },
                options = new { destination = "../escape" },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_download_without_a_destination_lands_in_the_remote_folder()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(transferDelayMs: 20));

        await PostBatchAsync(harness, destination: null);
        await harness.WaitForTransferAsync(Peer, t => t.State == "Completed, Succeeded", "completed");

        // slskd's default destination is ${SOURCE_DIRECTORY}.
        File.Exists(Path.Combine(harness.DownloadsDirectory, "Daft Punk", "Get Lucky.mp3")).Should().BeTrue();
    }

    [Fact]
    public async Task Acoustid_lookup_answers_known_fingerprints_and_rejects_an_empty_client()
    {
        await using var harness = await FakeSlskdHarness.StartAsync(ScenarioJson(transferDelayMs: 20));

        await PostBatchAsync(harness, destination: "track-1");
        await harness.WaitForTransferAsync(Peer, t => t.State == "Completed, Succeeded", "completed");

        var fingerprint = TestAudioGenerator.FingerprintFor(TrackPath);

        // The form-encoded POST the app's AcoustID client uses.
        var known = await LookupAsync(harness, fingerprint);
        known["status"]!.GetValue<string>().Should().Be("ok");

        var result = known["results"]!.AsArray().Should().ContainSingle().Subject!;
        result["score"]!.GetValue<double>().Should().Be(0.96);

        var recording = result["recordings"]!.AsArray().Should().ContainSingle().Subject!;
        recording["id"]!.GetValue<string>().Should().Be(RecordingId);
        recording["title"]!.GetValue<string>().Should().Be("Get Lucky");
        recording["artists"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be("Daft Punk");

        // The same lookup, gzip-compressed, which AcoustID clients send for large fingerprints.
        var compressed = await LookupGzipAsync(harness, fingerprint);
        compressed["results"]!.AsArray().Should().ContainSingle();

        // A fingerprint nothing generated is unknown, not an error.
        var unknown = await LookupAsync(harness, "AQADtEmi");
        unknown["status"]!.GetValue<string>().Should().Be("ok");
        unknown["results"]!.AsArray().Should().BeEmpty();

        // An empty client is AcoustID's error 4.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = string.Empty,
            ["fingerprint"] = fingerprint,
            ["duration"] = "249",
        });

        using var invalid = await harness.AcoustId.PostAsync("/v2/lookup", form);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var error = JsonNode.Parse(await invalid.Content.ReadAsStringAsync())!;
        error["status"]!.GetValue<string>().Should().Be("error");
        error["error"]!["code"]!.GetValue<int>().Should().Be(AcoustIdStubApp.InvalidApiKeyCode);

        // GET works the same way.
        using var getResponse = await harness.AcoustId.GetAsync(
            $"/v2/lookup?client=test&fingerprint={Uri.EscapeDataString(fingerprint)}&duration=249&meta=recordings");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        get["results"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task The_real_audio_generator_writes_a_file_when_ffmpeg_is_available()
    {
        if (!FfmpegAudioGenerator.IsAvailable())
        {
            // The dev machine has neither ffmpeg nor fpcalc; the container gate exercises this path.
            return;
        }

        await using var harness = await FakeSlskdHarness.StartAsync(
            ScenarioJson(transferDelayMs: 20),
            generator: new FfmpegAudioGenerator());

        await PostBatchAsync(harness, destination: "track-1");
        var transfer = await harness.WaitForTransferAsync(Peer, t => t.State == "Completed, Succeeded", "completed");

        transfer.Size.Should().BeGreaterThan(1024);

        var expected = Path.Combine(harness.DownloadsDirectory, "track-1", "Get Lucky.mp3");
        new FileInfo(expected).Length.Should().Be(transfer.Size);

        // The generated audio was fingerprinted, so the AcoustID stub knows it.
        var lookup = await LookupAsync(harness, "any");
        lookup["status"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void The_rendered_slskd_configuration_is_read_from_the_fixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "slskd",
            "render-credentials-sharing.yml");

        File.Exists(path).Should().BeTrue();

        var configuration = SlskdConfiguration.Load(path);

        configuration.WebPort.Should().Be(5030);
        configuration.WebIpAddress.Should().Be("127.0.0.1");
        configuration.AuthenticationDisabled.Should().BeFalse();
        configuration.ApiKeys.Should().ContainSingle().Which.Should().Be(new string('a', 64));
        configuration.SoulseekUsername.Should().Be("wondarr-soulseek");
        configuration.DownloadsDirectory.Should().Be("/data/downloads/slskd");
        configuration.IncompleteDirectory.Should().Be("/data/downloads/slskd/incomplete");
        configuration.ShareDirectories.Should().BeEquivalentTo(["/data/media/music", "/data/media/music-old"]);
    }

    [Fact]
    public void Webhook_headers_are_read_in_both_documented_shapes()
    {
        var configuration = SlskdConfiguration.FromYaml(
            """
            integrations:
              webhooks:
                mapped:
                  on:
                  - DownloadFileComplete
                  call:
                    url: http://127.0.0.1:9/mapped
                    headers:
                      X-Gate: mapped
                listed:
                  on:
                  - Any
                  call:
                    url: http://127.0.0.1:9/listed
                    headers:
                    - name: X-Gate
                      value: listed
            """);

        configuration.Webhooks.Should().HaveCount(2);

        var mapped = configuration.Webhooks.Single(webhook => webhook.Name == "mapped");
        mapped.Headers["X-Gate"].Should().Be("mapped");
        mapped.Listens(DownloadFileCompleteEvent.EventName).Should().BeTrue();
        mapped.Listens("UploadFileComplete").Should().BeFalse();

        var listed = configuration.Webhooks.Single(webhook => webhook.Name == "listed");
        listed.Headers["X-Gate"].Should().Be("listed");
        listed.Listens(DownloadFileCompleteEvent.EventName).Should().BeTrue();
    }

    private static async Task<EnqueueBatchResponse> PostBatchAsync(FakeSlskdHarness harness, string? destination)
    {
        using var response = await harness.PostRawAsync(
            "/api/v0/transfers/downloads/batches",
            new
            {
                username = Peer,
                files = new[] { new { filename = TrackPath, size = 30038322L } },
                options = destination is null ? null : new { destination },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<EnqueueBatchResponse>(JsonOptions))!;
    }

    private static async Task<JsonNode> LookupAsync(FakeSlskdHarness harness, string fingerprint)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = "test-client",
            ["fingerprint"] = fingerprint,
            ["duration"] = "249",
            ["meta"] = "recordings",
        });

        using var response = await harness.AcoustId.PostAsync("/v2/lookup", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> LookupGzipAsync(FakeSlskdHarness harness, string fingerprint)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = "test-client",
            ["fingerprint"] = fingerprint,
            ["duration"] = "249",
        });

        var body = await form.ReadAsByteArrayAsync();

        using var compressed = new MemoryStream();

        await using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            await gzip.WriteAsync(body);
        }

        compressed.Position = 0;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/lookup")
        {
            Content = new StreamContent(compressed),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Content.Headers.ContentEncoding.Add("gzip");

        using var response = await harness.AcoustId.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<IReadOnlyList<SlskdSearchResponse>> ResponsesForAsync(
        FakeSlskdHarness harness,
        string searchText)
    {
        var id = Guid.NewGuid();

        await harness.PostAsync<SlskdSearch>("/api/v0/searches", new { id, searchText });

        using var stopped = await harness.Slskd.PutAsync($"/api/v0/searches/{id}", content: null);
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);

        var responses = await harness.GetAsync<List<SlskdSearchResponse>>($"/api/v0/searches/{id}/responses");

        // Searches are deleted rather than left to pile up, which is also what frees the in-flight slot.
        (await harness.Slskd.DeleteAsync($"/api/v0/searches/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        return responses;
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> probe, Func<T, bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            var value = await probe();

            if (predicate(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"timed out waiting for {description}");
    }

    private static string ScenarioJson(
        int searchDelayMs = 1500,
        int transferDelayMs = 1000,
        int maxInFlight = 2,
        object[]? files = null) =>
        JsonSerializer.Serialize(
            new { searchDelayMs, transferDelayMs, maxInFlight, files = files ?? [Track()] },
            JsonOptions);

    /// <summary>The default scenario entry: one mp3 both the search and the download tests use.</summary>
    private static object Track() => Offer(TrackPath);

    /// <summary>One scenario entry, with the shapes the schema document describes.</summary>
    private static object Offer(
        string path,
        string transfer = "ok",
        int length = 249,
        int seed = 7) => new
    {
        username = Peer,
        path,
        size = 30038322L,
        bitRate = 320,
        length,
        hasFreeUploadSlot = true,
        uploadSpeed = 1146398L,
        queueLength = 0,
        transfer,
        audio = new
        {
            codec = "mp3",
            bitrateKbps = 320,
            durationSeconds = (double)length,
            seed,
            frequency = 440,
        },
        identity = new
        {
            recordingId = RecordingId,
            title = "Get Lucky",
            artists = new[] { new { id = "056e4f3e-d505-4dad-8ec1-d04f521cbb56", name = "Daft Punk" } },
            durationSeconds = (double)length,
        },
    };
}
