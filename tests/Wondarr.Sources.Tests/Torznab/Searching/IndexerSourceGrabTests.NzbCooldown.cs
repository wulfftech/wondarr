using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Torznab.Clients;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The usenet grab and an indexer that refuses NZB downloads (M-53): a grab of a post whose NZB is not kept
/// honours the cool-down a search started and starts one when it is refused itself, and shares one download
/// with a search or another grab that needs the same NZB. Every clock is a fake.
/// </summary>
public sealed partial class IndexerSourceGrabTests
{
    private static readonly DateTimeOffset CooldownStart = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly NzbSource TheIndexer = new(1, "Indexer");

    // Only ever the longest a hung test waits before it fails; a passing test never reaches it.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    private const string NewznabSettings = """{"url":"https://indexer.example","apiPath":"/api","apiKey":"S3cretIndexerKey","categories":"3000"}""";

    [Fact]
    public async Task A_grab_of_an_uncached_post_from_a_cooling_indexer_fails_at_once_with_no_request_and_does_not_condemn_the_post()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(TheIndexer, TimeSpan.FromMinutes(90));
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<DownloadClientException>()).Which;
        thrown.Message.Should().Be("Indexer is cooling down until 13:30 UTC+00:00 after refusing NZB downloads");
        thrown.Should().NotBeAssignableTo<ISourceGrabFailure>("only a classified failure blocklists a candidate, and the post is fine; the indexer is resting");
        _indexer.Downloads.Should().Be(0, "no request is sent to a cooling indexer");
        _usenet.Count.Should().Be(0);

        time.Advance(TimeSpan.FromMinutes(91));
        await provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/two", CancellationToken.None);

        _indexer.Downloads.Should().Be(1, "the cool-down is over");
        _usenet.Should().ContainSingle();
    }

    [Fact]
    public async Task A_grab_of_a_post_whose_nzb_is_kept_is_not_held_back_by_a_cooling_indexer()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(TheIndexer, null);
        var candidate = UsenetCandidate("03 - Digital Love.flac");
        var cache = new NzbCache(time);
        cache.Put(NzbCache.KeyOf(candidate.Release!.IndexerId, candidate.Release.DownloadUrl, candidate.Release.ReleaseId), AlbumNzb());
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: cache, time: time, cooldowns: cooldowns);

        await provider.GrabAsync(candidate, "wondarr/one", CancellationToken.None);

        _indexer.Downloads.Should().Be(0);
        _usenet.Single().Files.Select(file => file.FileName).Should().Equal("03 - Digital Love.flac");
    }

    [Fact]
    public async Task A_cooling_upstream_behind_a_prowlarr_row_holds_back_its_own_posts_only()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(new NzbSource(1, "Prowlarr (DOGnzb)"), TimeSpan.FromHours(1));
        _indexer.Download = new IndexerDownload(AlbumNzb(), null);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var act = () => provider.GrabAsync(FromUpstream(UsenetCandidate("03 - Digital Love.flac", "dog-1"), "Prowlarr (DOGnzb)"), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message
            .Should().Be("Prowlarr (DOGnzb) is cooling down until 13:00 UTC+00:00 after refusing NZB downloads", "it names the upstream, not just the Prowlarr row");
        _indexer.Downloads.Should().Be(0);

        await provider.GrabAsync(FromUpstream(UsenetCandidate("03 - Digital Love.flac", "geek-1"), "Prowlarr (NZBgeek)"), "wondarr/two", CancellationToken.None);

        _indexer.Downloads.Should().Be(1, "the other upstream behind the same row is not resting");
    }

    [Fact]
    public async Task A_pushed_release_names_no_indexer_row_so_its_refusal_is_keyed_to_nothing()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        _indexer.Failure = new IndexerException("The indexer answered 429.", HttpStatusCode.TooManyRequests);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var act = () => provider.GrabAsync(Pushed(UsenetCandidate("03 - Digital Love.flac", "push-1")), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be("Indexer: The indexer answered 429.");
        cooldowns.TryGetUntil(new NzbSource(PushedReleaseClient.IndexerId, "Indexer"), out _).Should().BeFalse("row 0 is no indexer, and a name alone is not a key");

        await FluentActions.Awaiting(() => provider.GrabAsync(Pushed(UsenetCandidate("05 - Crescendolls.flac", "push-2")), "wondarr/two", CancellationToken.None))
            .Should().ThrowAsync<DownloadClientException>().WithMessage("Indexer: The indexer answered 429.");
        _indexer.Downloads.Should().Be(2, "nothing was cooling down, so the second pushed release was asked for too");
    }

    [Theory]
    [InlineData("429", 7200, 7200)]
    [InlineData("429", null, 3600)]
    [InlineData("403", null, 3600)]
    [InlineData("sentence", null, 3600)]
    [InlineData("account-error", null, 3600)]
    public async Task A_grab_the_indexer_refuses_starts_the_cooldown_the_way_a_search_does(string refusal, int? retryAfterSeconds, int expectedSeconds)
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        RetryConditionHeaderValue? asked = retryAfterSeconds is { } seconds ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) : null;

        switch (refusal)
        {
            case "429":
                _indexer.Failure = new IndexerException("The indexer answered 429.", HttpStatusCode.TooManyRequests, asked);
                break;
            case "403":
                _indexer.Failure = new IndexerException("The indexer answered 403.", HttpStatusCode.Forbidden);
                break;
            case "sentence":
                _indexer.Download = Plain("You already reached your daily restriction of 10 downloads.");
                break;
            default:
                _indexer.Download = Plain("<?xml version=\"1.0\"?><error code=\"500\" description=\"Daily download limit reached\"/>");
                break;
        }

        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<DownloadClientException>();
        cooldowns.TryGetUntil(TheIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddSeconds(expectedSeconds));
        _usenet.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("release-failure")]
    [InlineData("503")]
    public async Task A_grab_that_fails_for_another_reason_starts_no_cooldown(string failure)
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);

        if (failure == "503")
        {
            _indexer.Failure = new IndexerException("The indexer answered 503.", HttpStatusCode.ServiceUnavailable);
        }
        else
        {
            _indexer.Download = Plain("<?xml version=\"1.0\"?><error code=\"300\" description=\"No such item\"/>");
        }

        var provider = Provider(DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        cooldowns.TryGetUntil(TheIndexer, out _).Should().BeFalse("one bad post or a hiccup is not the indexer's quota");
    }

    [Theory]
    [InlineData(7200, 7200)]
    [InlineData(null, 3600)]
    public async Task A_429_to_a_grabs_nzb_download_is_one_request_fails_the_grab_and_cools_the_indexer_down(int? retryAfterSeconds, int expectedSeconds)
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var handler = new RecordingHandler(_ => ThrottledAnswer(retryAfterSeconds));
        await using var pipeline = NzbDownloadRetryTests.BuildProvider(time, handler);
        _indexerType = "newznab";
        _indexerSettings = NewznabSettings;
        var provider = Provider(
            DownloadProtocol.Usenet,
            client: pipeline.GetRequiredService<NewznabIndexerClient>(),
            nzbCache: new NzbCache(time),
            time: time,
            cooldowns: cooldowns);

        // A 429 is not retried, so nothing waits on the clock; the guard only turns a retry (which would wait for it) into a failure.
        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None).WaitAsync(Guard);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be("Indexer: The indexer answered 429.");
        handler.Requests.Should().ContainSingle("a retry is another doomed download");
        cooldowns.TryGetUntil(TheIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddSeconds(expectedSeconds));
        _usenet.Count.Should().Be(0);

        var next = () => provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac", "nzb-guid-2"), "wondarr/two", CancellationToken.None).WaitAsync(Guard);

        (await next.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().StartWith("Indexer is cooling down until ");
        handler.Requests.Should().ContainSingle("the next grab of that indexer is turned away without a request");
    }

    [Fact]
    public async Task After_a_grab_was_refused_a_search_of_the_same_indexer_reads_none_of_its_nzbs_and_says_it_is_cooling_down()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var client = new ScriptedIndexer
        {
            Releases = [Searched("nzb-guid-2", "https://usenet.example/get/2"), Searched("nzb-guid-3", "https://usenet.example/get/3")],
            Download = _ => throw new IndexerException("The indexer answered 429.", HttpStatusCode.TooManyRequests),
        };
        ListUsenetIndexer();
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: new NzbCache(time), time: time, cooldowns: new NzbCooldowns(time));

        await FluentActions.Awaiting(() => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None))
            .Should().ThrowAsync<DownloadClientException>().WithMessage("Indexer: The indexer answered 429.");
        client.Downloads.Should().Be(1);

        var result = await provider.SearchAsync(SearchRequest(), CancellationToken.None);

        client.Downloads.Should().Be(1, "the grab's refusal cooled the indexer down: searched, but not one NZB asked of it");
        result.Candidates.Should().BeEmpty("none of its posts is cached");
        result.Message.Should().Be("Usenet: Indexer is cooling down until 13:00 UTC+00:00 after refusing NZB downloads");
    }

    [Fact]
    public async Task A_grab_and_a_search_that_need_the_same_nzb_at_once_make_one_request()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger("sharing that download");
        var client = new ScriptedIndexer
        {
            Releases = [Searched("nzb-guid-1", "https://usenet.example/get/1")],
            Download = _ => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = () => started.Release(),
        };
        ListUsenetIndexer();
        var cache = new NzbCache(TimeProvider.System);
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: cache, logger: sharing);

        var search = provider.SearchAsync(SearchRequest(), CancellationToken.None);
        await DownloadStartedAsync(started);

        var grab = provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        var result = await search.WaitAsync(Guard);
        await grab.WaitAsync(Guard);

        client.Downloads.Should().Be(1, "the grab waited for the search's download");
        result.Candidates.Should().ContainSingle().Which.Availability.FileListKnown.Should().BeTrue();
        _usenet.Single().Files.Select(file => file.FileName).Should().Equal("03 - Digital Love.flac");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_search_that_needs_the_nzb_a_grab_is_downloading_waits_for_that_download()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger("sharing that download");
        var client = new ScriptedIndexer
        {
            Releases = [Searched("nzb-guid-1", "https://usenet.example/get/1")],
            Download = _ => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = () => started.Release(),
        };
        ListUsenetIndexer();
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: new NzbCache(TimeProvider.System), logger: sharing);

        var grab = provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await DownloadStartedAsync(started);

        var search = provider.SearchAsync(SearchRequest(), CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        await grab.WaitAsync(Guard);
        var result = await search.WaitAsync(Guard);

        client.Downloads.Should().Be(1, "the search waited for the grab's download");
        result.Candidates.Should().ContainSingle().Which.Availability.FileListKnown.Should().BeTrue("the grab's download is read for the file list too");
        _usenet.Should().ContainSingle();
    }

    [Fact]
    public async Task Two_grabs_of_one_post_at_once_make_one_request()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger("sharing that download");
        var client = new ScriptedIndexer
        {
            Download = _ => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = () => started.Release(),
        };
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: new NzbCache(TimeProvider.System), logger: sharing);

        var first = provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await DownloadStartedAsync(started);

        var second = provider.GrabAsync(UsenetCandidate("05 - Crescendolls.flac"), "wondarr/two", CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Guard);

        client.Downloads.Should().Be(1);
        _usenet.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_grab_waiting_for_a_searchs_download_makes_it_itself_when_that_search_is_cancelled()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger("sharing that download");
        var client = new ScriptedIndexer
        {
            Releases = [Searched("nzb-guid-1", "https://usenet.example/get/1")],
            Download = _ => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = () => started.Release(),
        };
        ListUsenetIndexer();
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: new NzbCache(TimeProvider.System), logger: sharing);

        using var cancelSearch = new CancellationTokenSource();
        var search = provider.SearchAsync(SearchRequest(), cancelSearch.Token);
        await DownloadStartedAsync(started);

        var grab = provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        await cancelSearch.CancelAsync();
        await FluentActions.Awaiting(() => search).Should().ThrowAsync<OperationCanceledException>();

        await DownloadStartedAsync(started);
        hold.SetResult();
        await grab.WaitAsync(Guard);

        client.Downloads.Should().Be(2, "the cancelled search's download was not the grab's failure");
        _usenet.Should().ContainSingle();
    }

    [Fact]
    public async Task A_grab_and_a_search_sharing_a_refused_download_are_both_refused_and_the_indexer_cools_down_once()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger("sharing that download");
        var client = new ScriptedIndexer
        {
            Releases = [Searched("nzb-guid-1", "https://usenet.example/get/1")],
            Download = _ => throw new IndexerException("The indexer answered 429.", HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromHours(2))),
            Hold = hold.Task,
            OnDownload = () => started.Release(),
        };
        ListUsenetIndexer();
        var provider = Provider(DownloadProtocol.Usenet, client: client, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns, logger: sharing);

        var search = provider.SearchAsync(SearchRequest(), CancellationToken.None);
        await DownloadStartedAsync(started);

        var grab = provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        var result = await search.WaitAsync(Guard);
        await FluentActions.Awaiting(() => grab.WaitAsync(Guard)).Should().ThrowAsync<DownloadClientException>().WithMessage("Indexer: The indexer answered 429.");

        client.Downloads.Should().Be(1);
        result.Message.Should().Be("Usenet: Indexer: The indexer answered 429.");
        cooldowns.TryGetUntil(TheIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddHours(2));
        _usenet.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_grab_whose_nzb_cannot_be_read_fails_with_a_sentence_and_keeps_nothing()
    {
        _indexer.Download = Plain("<?xml version=\"1.0\"?><nzb><file subject=\"x\">");
        var cache = new NzbCache(TimeProvider.System);
        var provider = Provider(DownloadProtocol.Usenet, nzbCache: cache);

        var act = () => provider.GrabAsync(UsenetCandidate("03 - Digital Love.flac"), "wondarr/one", CancellationToken.None);

        (await act.Should().ThrowAsync<DownloadClientException>()).Which.Message.Should().Be("Indexer: The NZB could not be read.");
        _usenet.Count.Should().Be(0);
        cache.Count.Should().Be(0);
    }

    /// <summary>Waits for the next download to start; a semaphore's timed wait answers false instead of throwing.</summary>
    private static async Task DownloadStartedAsync(SemaphoreSlim started) =>
        (await started.WaitAsync(Guard)).Should().BeTrue("a download should have started");

    private static IndexerDownload Plain(string body) => new(Encoding.UTF8.GetBytes(body), null);

    private static HttpResponseMessage ThrottledAnswer(int? retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static Candidate FromUpstream(Candidate candidate, string upstream) =>
        candidate with { Release = candidate.Release! with { IndexerName = upstream } };

    /// <summary>A candidate from a release pushed to <c>release/push</c>: it names no indexer row.</summary>
    private static Candidate Pushed(Candidate candidate) =>
        candidate with { Release = candidate.Release! with { IndexerId = PushedReleaseClient.IndexerId } };

    /// <summary>The release the search finds for a post: the same row, link and guid the grab's candidate carries.</summary>
    private static IndexerRelease Searched(string guid, string link) =>
        new(Album, guid, link, null, null, null, DateTimeOffset.UtcNow.AddDays(-30), null, null, 100, [3040], 1, null, DownloadProtocol.Usenet, 1, "Indexer", null);

    /// <summary>The song Discovery's third track is searched for: no MusicBrainz identity, only the album context's title.</summary>
    private static SongSearchRequest SearchRequest() =>
        new(1, "Digital Love", "Daft Punk", ["Daft Punk"], "Discovery", 301_000, VersionFlags.None)
        {
            AlbumKind = AlbumContextKind.Album,
            AlbumTrackNo = 3,
        };

    /// <summary>Makes the indexer service list the one enabled usenet indexer the search asks.</summary>
    private void ListUsenetIndexer()
    {
        Indexer[] rows = [new Indexer { Id = 1, Name = "Indexer", Type = "newznab", Settings = "{}", Protocol = DownloadProtocol.Usenet, Priority = 25 }];
        _indexerService.ListAsync(Arg.Any<CancellationToken>()).Returns(rows);
    }

    /// <summary>
    /// An indexer client that finds the releases it is given for the album "Discovery", counts every download
    /// and makes each wait for <see cref="Hold"/> before it answers, so a test decides when the answers go.
    /// </summary>
    private sealed class ScriptedIndexer : IIndexerClient
    {
        private int _downloads;

        public IReadOnlyList<IndexerRelease> Releases { get; init; } = [];

        public Func<IndexerRelease, IndexerDownload> Download { get; init; } = _ => throw new IndexerException("no download");

        /// <summary>Every download waits for this before it answers; a test completes it to let the answers go.</summary>
        public Task Hold { get; init; } = Task.CompletedTask;

        /// <summary>Called as each download starts, before it waits for <see cref="Hold"/>.</summary>
        public Action? OnDownload { get; init; }

        public int Downloads => Volatile.Read(ref _downloads);

        public Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexerRelease>>(
                query.Album == "Discovery" ? [.. Releases.Select(release => release with { IndexerId = indexer.Id, IndexerName = indexer.Name })] : []);

        public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloads);
            OnDownload?.Invoke();
            await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);

            return Download(release);
        }
    }

    /// <summary>A logger that completes <see cref="Seen"/> the first time a line holds the text it waits for.</summary>
    private sealed class SignalLogger(string needle) : ILogger<IndexerSourceProvider>
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Seen => _seen.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (formatter(state, exception).Contains(needle, StringComparison.Ordinal))
            {
                _seen.TrySetResult();
            }
        }
    }
}
