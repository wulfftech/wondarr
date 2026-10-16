using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>Prowlarr's aggregate search and Gazelle-direct (P7-03b), against recorded answers.</summary>
public sealed class ProwlarrAndGazelleTests
{
    private const string ProwlarrKey = "prowlarr-secret-key";
    private const string GazelleKey = "gazelle-secret-key";

    [Fact]
    public async Task Prowlarr_is_asked_with_repeated_array_parameters_and_the_key_in_a_header()
    {
        var handler = new HeaderHandler(_ => Fixture("prowlarr-search.json"));
        var client = Prowlarr(handler);

        await client.SearchAsync(ProwlarrRow(DownloadProtocol.Torrent, indexerIds: "1,2"), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);

        var request = handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be("/api/v1/search");
        request.Uri.Query.Should().Contain("query=Daft%20Punk%20Discovery").And.Contain("type=search")
            .And.Contain("categories=3000&categories=3040").And.Contain("indexerIds=1&indexerIds=2");
        request.Uri.Query.Should().NotContain(ProwlarrKey);
        request.Headers["X-Api-Key"].Should().Be(ProwlarrKey);
    }

    [Fact]
    public async Task Prowlarr_keeps_the_row_s_protocol_and_maps_every_field()
    {
        var client = Prowlarr(new HeaderHandler(_ => Fixture("prowlarr-search.json")));

        var torrents = await client.SearchAsync(ProwlarrRow(DownloadProtocol.Torrent), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);
        var usenet = await client.SearchAsync(ProwlarrRow(DownloadProtocol.Usenet), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);

        torrents.Should().HaveCount(2);
        usenet.Should().ContainSingle().Which.Protocol.Should().Be(DownloadProtocol.Usenet);

        var first = torrents[0];
        first.Title.Should().Be("Daft Punk - Discovery (2001) [FLAC]");
        first.ReleaseId.Should().Be("https://tracker.example/torrents/1");
        first.InfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
        first.Size.Should().Be(412_345_678);
        first.Seeders.Should().Be(42);
        first.Peers.Should().Be(45);
        first.Grabs.Should().Be(120);
        first.Categories.Should().Equal(3000, 3040);
        first.IndexerId.Should().Be(9);
        first.IndexerName.Should().Be("Prowlarr (Tracker One)");
        first.PublishDate.Should().Be(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        torrents[1].MagnetUrl.Should().StartWith("magnet:");
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(10, 1)]
    [InlineData(43, 0)]
    public async Task Prowlarr_skips_torrents_with_fewer_seeders_than_the_row_s_minimum(int? minimum, int expected)
    {
        var client = Prowlarr(new HeaderHandler(_ => Fixture("prowlarr-search.json")));

        var torrents = await client.SearchAsync(ProwlarrRow(DownloadProtocol.Torrent, minimumSeeders: minimum), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);

        torrents.Should().HaveCount(expected, "the fixture's torrents have 42 and 7 seeders; the default minimum is 1");
    }

    [Fact]
    public async Task Prowlarr_does_not_filter_usenet_results_by_the_minimum_seeders()
    {
        var client = Prowlarr(new HeaderHandler(_ => Fixture("prowlarr-search.json")));

        var usenet = await client.SearchAsync(ProwlarrRow(DownloadProtocol.Usenet, minimumSeeders: 100), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);

        usenet.Should().ContainSingle();
    }

    [Fact]
    public void Prowlarr_offers_the_same_minimum_seeders_field_as_torznab()
    {
        var prowlarr = new ProwlarrIndexerType(new StaticHttpClientFactory(new HeaderHandler(_ => Fixture("prowlarr-search.json"))), new SecretRegistry());
        var torznab = TorznabTest.TorznabType(RecordingHandler.Serving("caps-prowlarr.xml"));

        prowlarr.Fields.Single(field => field.Name == "minimumSeeders")
            .Should().BeEquivalentTo(torznab.Fields.Single(field => field.Name == "minimumSeeders"));
        ProwlarrSettings.Read(Json("""{"url":"http://prowlarr:9696","apiKey":"k"}""")).MinimumSeeders.Should().Be(IndexerEndpoint.DefaultMinimumSeeders);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, null)]
    [InlineData(HttpStatusCode.Unauthorized, false, "Prowlarr refused the API key.")]
    public async Task Prowlarr_s_test_reads_the_system_status(HttpStatusCode status, bool success, string? error)
    {
        var handler = new HeaderHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("{}") });
        var type = new ProwlarrIndexerType(new StaticHttpClientFactory(handler), new SecretRegistry());

        var result = await type.TestAsync(Json($$"""{"url":"http://prowlarr:9696","apiKey":"{{ProwlarrKey}}"}"""), CancellationToken.None);

        result.Should().Be(new ProviderTestResult(success, error));
        handler.Requests.Single().Uri.AbsolutePath.Should().Be("/api/v1/system/status");
    }

    [Fact]
    public async Task Gazelle_browses_with_the_key_in_the_authorization_header_and_maps_one_release_per_torrent()
    {
        var handler = GazelleServer();
        var client = Gazelle(handler);

        var releases = await client.SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);

        var browse = handler.Requests[0];
        browse.Uri.Query.Should().Contain("action=browse").And.Contain("artistname=Daft%20Punk").And.Contain("groupname=Discovery");
        browse.Uri.Query.Should().NotContain(GazelleKey);
        browse.Headers["Authorization"].Should().Be(GazelleKey);

        releases.Should().HaveCount(6);
        var first = releases.Single(release => release.ReleaseId == "gazelle-701");
        first.Title.Should().Be("Daft Punk - Discovery (2001) [FLAC Lossless] [CD] [Cue]");
        first.Seeders.Should().Be(40);
        first.Peers.Should().Be(42);
        first.Size.Should().Be(412_345_678);
        first.InfoUrl.Should().Be("https://redacted.example/torrents.php?id=72&torrentid=701");
        first.DownloadUrl.Should().Be("https://redacted.example/ajax.php?action=download&id=701" + GazelleClient.TokenMark);
        first.DownloadVolumeFactor.Should().Be(1);
        releases.Single(release => release.ReleaseId == "gazelle-702").DownloadVolumeFactor.Should().Be(0);
        releases.Single(release => release.ReleaseId == "gazelle-704").Title.Should().Be("Daft Punk - Discovery & Other Things (2001) [MP3 V0 (VBR)] [CD]");
    }

    [Fact]
    public async Task Gazelle_reads_the_file_lists_of_the_five_best_seeded_torrents_only()
    {
        var handler = GazelleServer();

        var releases = await Gazelle(handler).SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);

        var fetched = handler.Requests.Where(request => request.Uri.Query.Contains("action=torrent", StringComparison.Ordinal))
            .Select(request => request.Uri.Query[(request.Uri.Query.LastIndexOf('=') + 1)..]).ToList();
        fetched.Should().BeEquivalentTo(["701", "702", "703", "704", "705"], "the least-seeded sixth is not read");
        releases.Single(release => release.ReleaseId == "gazelle-706").FileList.Should().BeNull();

        var files = releases.Single(release => release.ReleaseId == "gazelle-701").FileList!;
        files.Should().HaveCount(5);
        files[3].Should().Be(new ReleaseFile(3, "Daft Punk - Discovery (2001) [FLAC]/04 - Harder, Better, Faster & Stronger.flac", 35_000_000));
    }

    [Fact]
    public async Task A_gazelle_search_never_puts_usetoken_in_a_release_link_and_marks_only_where_a_token_can_be_spent()
    {
        var releases = await Gazelle(GazelleServer()).SearchAsync(
            GazelleRow(useTokens: true),
            new ReleaseQuery("Daft Punk", "Discovery", null),
            CancellationToken.None);

        releases.Select(release => release.DownloadUrl).Should().NotContain(url => url!.Contains("usetoken", StringComparison.Ordinal));
        releases.Single(release => release.ReleaseId == "gazelle-701").DownloadUrl.Should().EndWith(GazelleClient.TokenMark);
        releases.Single(release => release.ReleaseId == "gazelle-702").DownloadUrl.Should().NotContain("#", "it is freeleech already");
        releases.Single(release => release.ReleaseId == "gazelle-704").DownloadUrl.Should().NotContain("#", "no token can be used on it");
    }

    [Theory]
    [InlineData(true, "https://redacted.example/ajax.php?action=download&id=701&usetoken=1")]
    [InlineData(false, "https://redacted.example/ajax.php?action=download&id=701")]
    public void A_marked_link_gets_usetoken_only_on_the_grab_path_and_only_when_the_row_says_so(bool useTokens, string expected)
    {
        var marked = "https://redacted.example/ajax.php?action=download&id=701" + GazelleClient.TokenMark;

        GazelleClient.ForGrab(GazelleRow(useTokens), marked).Should().Be(expected);
        GazelleClient.ForGrab(GazelleRow(useTokens), "https://redacted.example/ajax.php?action=download&id=702")
            .Should().Be("https://redacted.example/ajax.php?action=download&id=702", "a link no token can be spent on is left as it is");
    }

    [Fact]
    public async Task Downloading_a_marked_link_at_search_time_never_sends_the_mark_or_a_token()
    {
        var handler = new HeaderHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([(byte)'d', (byte)'e']) });
        var client = Gazelle(handler);
        var release = new IndexerRelease(
            "t", "gazelle-701", "https://redacted.example/ajax.php?action=download&id=701" + GazelleClient.TokenMark, null, null, null, null, null, null, null, [], 1, null, DownloadProtocol.Torrent, 5, "Redacted", null);

        await client.DownloadAsync(GazelleRow(useTokens: true), release, CancellationToken.None);

        handler.Requests.Single().Uri.ToString().Should().NotContain("usetoken").And.NotContain("#");
    }

    [Fact]
    public async Task Gazelle_sends_at_most_five_requests_in_any_ten_seconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = GazelleServer();
        var client = Gazelle(handler, time);

        // A browse and five file lists: the sixth request must wait for the window.
        var search = client.SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);

        await WaitUntilAsync(() => handler.Requests.Count == 5);
        await YieldAsync();
        handler.Requests.Should().HaveCount(5, "the sixth request waits for the window");
        search.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(10));
        await search;

        handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task Gazelle_has_no_hidden_retry_handler_so_its_own_retries_are_the_only_extra_requests()
    {
        // The real registration: the "indexer" client retries 5xx twice outside the limiter, Gazelle's own does not.
        var handler = new HeaderHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWondarrTorznab();
        services.AddHttpClient(GazelleClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new GazelleClient(provider.GetRequiredService<IHttpClientFactory>(), new SecretRegistry(), time, NullLogger<GazelleClient>.Instance);
        var settings = GazelleSettings.Read(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""));

        var first = async () => await client.IndexAsync(5, settings, CancellationToken.None);
        await first.Should().ThrowAsync<IndexerException>();

        handler.Requests.Should().HaveCount(3, "one try and Gazelle's own two retries, nothing behind its back");

        // Three more attempts: the 4th and 5th go out, the 6th waits for the window.
        var second = client.IndexAsync(5, settings, CancellationToken.None);
        await YieldAsync();
        handler.Requests.Should().HaveCount(5);
        second.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(10));
        var secondFailed = async () => await second;
        await secondFailed.Should().ThrowAsync<IndexerException>();
        handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_502_then_a_502_then_a_200_succeeds_and_every_attempt_took_a_limiter_turn()
    {
        var attempts = 0;
        var handler = new HeaderHandler(_ => ++attempts < 3 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Fixture("gazelle-index.json"));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = Gazelle(handler, time);
        var settings = GazelleSettings.Read(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""));

        await client.IndexAsync(5, settings, CancellationToken.None);

        handler.Requests.Should().HaveCount(3);

        // 3 turns used: two more requests go out, the next one has to wait for the window.
        await client.IndexAsync(5, settings, CancellationToken.None);
        await client.IndexAsync(5, settings, CancellationToken.None);
        handler.Requests.Should().HaveCount(5);
        var sixth = client.IndexAsync(5, settings, CancellationToken.None);
        await YieldAsync();
        handler.Requests.Should().HaveCount(5);
        sixth.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(10));
        await sixth;
        handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_429_with_retry_after_keeps_the_row_quiet_until_then_and_is_reported_as_a_rate_limit()
    {
        var throttled = true;
        var handler = new HeaderHandler(_ =>
        {
            if (!throttled)
            {
                return Fixture("gazelle-index.json");
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = Gazelle(handler, time);
        var settings = GazelleSettings.Read(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""));

        var limited = async () => await client.IndexAsync(5, settings, CancellationToken.None);
        (await limited.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Contain("rate limit");
        throttled = false;

        var next = client.IndexAsync(5, settings, CancellationToken.None);
        await YieldAsync();
        handler.Requests.Should().HaveCount(1, "the row is quiet for the Retry-After");
        next.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(29));
        await YieldAsync();
        handler.Requests.Should().HaveCount(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await next;
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_429_without_retry_after_waits_ten_seconds()
    {
        var calls = 0;
        var handler = new HeaderHandler(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Fixture("gazelle-index.json"));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = Gazelle(handler, time);
        var settings = GazelleSettings.Read(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""));

        var limited = async () => await client.IndexAsync(5, settings, CancellationToken.None);
        await limited.Should().ThrowAsync<IndexerException>();
        var next = client.IndexAsync(5, settings, CancellationToken.None);
        await YieldAsync();
        next.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(10));
        await next;
    }

    [Fact]
    public async Task One_gazelle_row_s_wait_does_not_hold_up_another_row()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = GazelleServer();
        var client = Gazelle(handler, time);

        var first = client.SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);
        await WaitUntilAsync(() => handler.Requests.Count == 5);

        var other = GazelleRow() is var row ? new Indexer { Id = 6, Name = "Orpheus", Type = row.Type, Protocol = row.Protocol, Settings = row.Settings } : null!;
        var index = client.IndexAsync(other.Id, GazelleSettings.Read(System.Text.Json.JsonDocument.Parse(other.Settings).RootElement), CancellationToken.None);

        await index.WaitAsync(TimeSpan.FromSeconds(5));
        first.IsCompleted.Should().BeFalse("the first row still waits for its window");

        time.Advance(TimeSpan.FromSeconds(10));
        await first;
    }

    [Fact]
    public async Task A_refused_gazelle_download_is_an_error_not_a_torrent()
    {
        var client = Gazelle(new HeaderHandler(_ => Fixture("gazelle-failure.json")));
        var release = new IndexerRelease("t", "gazelle-701", "https://redacted.example/ajax.php?action=download&id=701", null, null, null, null, null, null, null, [], null, null, DownloadProtocol.Torrent, 5, "Redacted", null);

        var act = () => client.DownloadAsync(GazelleRow(), release, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Be("The tracker said: bad credentials");
    }

    [Fact]
    public async Task Gazelle_s_test_reports_the_tracker_s_error_and_never_the_key()
    {
        var failing = new HeaderHandler(_ => Fixture("gazelle-failure.json"));
        var type = new GazelleIndexerType(Gazelle(failing));

        var result = await type.TestAsync(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""), CancellationToken.None);

        result.Should().Be(new ProviderTestResult(false, "The tracker said: bad credentials"));
        failing.Requests.Single().Uri.Query.Should().Be("?action=index");

        var working = new GazelleIndexerType(Gazelle(new HeaderHandler(_ => Fixture("gazelle-index.json"))));
        (await working.TestAsync(Json($$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}"}"""), CancellationToken.None))
            .Should().Be(new ProviderTestResult(true, null));
    }

    [Fact]
    public async Task A_gazelle_download_carries_the_key_in_the_header()
    {
        var handler = new HeaderHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("d4:infod4:name1:aee"u8.ToArray()) });
        var client = Gazelle(handler);
        var release = new IndexerRelease("t", "gazelle-701", "https://redacted.example/ajax.php?action=download&id=701", null, null, null, null, null, null, null, [], null, null, DownloadProtocol.Torrent, 5, "Redacted", null);

        var download = await client.DownloadAsync(GazelleRow(), release, CancellationToken.None);

        download.Content.Should().NotBeNull();
        handler.Requests.Single().Headers["Authorization"].Should().Be(GazelleKey);
    }

    [Fact]
    public async Task A_prowlarr_timeout_is_an_indexer_failure_but_a_cancelled_caller_still_cancels()
    {
        var client = Prowlarr(new TimingOutHandler());

        // HttpClient reports its own timeout as a TaskCanceledException while the caller's token is untouched.
        var timeout = async () => await client.SearchAsync(ProwlarrRow(DownloadProtocol.Torrent), new ReleaseQuery("Daft Punk", "Discovery", null), CancellationToken.None);
        (await timeout.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Contain("did not answer in time");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = async () => await client.SearchAsync(ProwlarrRow(DownloadProtocol.Torrent), new ReleaseQuery("Daft Punk", "Discovery", null), cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_gazelle_timeout_is_an_indexer_failure_but_a_cancelled_caller_still_cancels()
    {
        var client = Gazelle(new TimingOutHandler());

        var timeout = async () => await client.SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        (await timeout.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Contain("did not answer in time");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = async () => await client.SearchAsync(GazelleRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_shared_indexer_http_helper_turns_a_timeout_into_an_indexer_failure()
    {
        using var http = new HttpClient(new TimingOutHandler());

        var timeout = async () => await IndexerHttp.GetAsync(http, new Uri("https://indexer.example/api"), CancellationToken.None);
        await timeout.Should().ThrowAsync<IndexerException>();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = async () => await IndexerHttp.GetAsync(http, new Uri("https://indexer.example/api"), cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Answers every request the way <c>HttpClient.Timeout</c> does: a <see cref="TaskCanceledException"/> with the caller's token untouched, unless it was cancelled.</summary>
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                : Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
    }

    private static ProwlarrSearchClient Prowlarr(HttpMessageHandler handler) =>
        new(new StaticHttpClientFactory(handler), new SecretRegistry(), NullLogger<ProwlarrSearchClient>.Instance);

    private static GazelleClient Gazelle(HttpMessageHandler handler, TimeProvider? time = null) =>
        new(new StaticHttpClientFactory(handler), new SecretRegistry(), time ?? new SkippingTime(), NullLogger<GazelleClient>.Instance);

    private static Indexer ProwlarrRow(DownloadProtocol protocol, string indexerIds = "", int? minimumSeeders = null) => new()
    {
        Id = 9,
        Name = "Prowlarr",
        Type = "prowlarr",
        Protocol = protocol,
        Settings = $$"""{"url":"http://prowlarr:9696","apiKey":"{{ProwlarrKey}}","indexerIds":"{{indexerIds}}","categories":"3000,3040"{{(minimumSeeders is { } minimum ? $",\"minimumSeeders\":{minimum}" : string.Empty)}}}""",
    };

    private static Indexer GazelleRow(bool useTokens = false) => new()
    {
        Id = 5,
        Name = "Redacted",
        Type = "gazelle",
        Protocol = DownloadProtocol.Torrent,
        Settings = $$"""{"url":"https://redacted.example","apiKey":"{{GazelleKey}}","useFreeleechTokens":{{(useTokens ? "true" : "false")}}}""",
    };

    private static HeaderHandler GazelleServer() => new(request =>
        request.RequestUri!.Query.Contains("action=browse", StringComparison.Ordinal)
            ? Fixture("gazelle-browse.json")
            : Fixture("gazelle-torrent.json"));

    private static HttpResponseMessage Fixture(string name) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(TorznabFixtures.Read(name), Encoding.UTF8, "application/json"),
    };

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Lets already-scheduled continuations run; bounded, so it cannot hang.</summary>
    private static async Task YieldAsync()
    {
        for (var turn = 0; turn < 100; turn++)
        {
            await Task.Yield();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>One request as the server saw it.</summary>
    private sealed record SeenRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Answers from a responder and keeps every request's URI and headers.</summary>
    private sealed class HeaderHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<SeenRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        private readonly List<SeenRequest> _requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                _requests.Add(new SeenRequest(request.RequestUri!, headers));
            }

            return Task.FromResult(responder(request));
        }
    }
}
