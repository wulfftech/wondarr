using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Torznab.Indexers;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Searching;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The usenet search and an indexer that refuses NZB downloads (M-47): the cool-down that outlives the
/// search, the quota kept per upstream indexer of a Prowlarr row, the read that settles cache, cap and
/// refusal in one step, and two searches that share one download. Every clock is a fake.
/// </summary>
public sealed partial class IndexerSourceProviderTests
{
    private static readonly DateTimeOffset CooldownStart = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly NzbSource FirstIndexer = new(1, "Indexer 1");

    // Only ever the longest a hung test waits before it fails; a passing test never reaches it.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    private const string NewznabSettings = """{"url":"https://indexer.example","apiPath":"/api","apiKey":"S3cretIndexerKey","categories":"3000"}""";

    [Theory]
    [InlineData(null, 3600)]
    [InlineData(0, 3600)]
    [InlineData(7200, 7200)]
    [InlineData(259_200, 86_400)]
    public async Task A_429_cools_the_indexer_down_for_its_retry_after_up_to_a_day_and_for_an_hour_without_a_usable_one(int? retryAfterSeconds, int expectedSeconds)
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 6),
            Download = (_, _) => throw Quota(retryAfter: retryAfterSeconds is { } seconds ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) : null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);

        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddSeconds(expectedSeconds));
    }

    [Fact]
    public async Task A_retry_after_given_as_a_date_cools_the_indexer_down_until_that_date()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 6),
            Download = (_, _) => throw Quota(retryAfter: new RetryConditionHeaderValue(CooldownStart.AddMinutes(90))),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);

        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddMinutes(90));
    }

    [Fact]
    public async Task A_retry_after_date_that_has_passed_counts_as_absent_and_cools_the_indexer_down_for_an_hour()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 6),
            Download = (_, _) => throw Quota(retryAfter: new RetryConditionHeaderValue(CooldownStart.AddMinutes(-5))),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);

        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddHours(1));
    }

    [Theory]
    [InlineData("sentence")]
    [InlineData("403")]
    public async Task Any_other_refusal_cools_the_indexer_down_for_an_hour(string refusal)
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 6),
            Download = (_, _) => refusal == "sentence" ? Plain(DogLimit) : throw Quota(HttpStatusCode.Forbidden),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);

        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddHours(1));
    }

    [Fact]
    public async Task A_failure_that_is_not_a_refusal_starts_no_cooldown()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 6),
            Download = (_, _) => throw new IndexerException("The indexer answered 503.", HttpStatusCode.ServiceUnavailable),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);

        cooldowns.TryGetUntil(FirstIndexer, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_search_in_the_cooldown_makes_no_download_from_the_indexer_drops_its_uncached_posts_and_says_when_it_is_over()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 12) : [],
            Download = (_, _) => throw Quota(retryAfter: new RetryConditionHeaderValue(TimeSpan.FromHours(2))),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var first = await provider.SearchAsync(Request(), CancellationToken.None);
        var spent = client.DownloadsFrom(1);

        spent.Should().BeInRange(1, InFlightReads);
        first.Candidates.Should().BeEmpty("the reads that came after the refusal were dropped, as before");
        first.Message.Should().Be("Usenet: Indexer 1: The indexer answered 429.");

        time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(spent, "the indexer is cooling down: searched, but not one NZB asked of it");
        second.Candidates.Should().BeEmpty("none of its posts is cached, and a post listed without a file list would only fail at the grab");
        second.Message.Should().Be("Usenet: Indexer 1 is cooling down until 14:00 UTC+00:00 after refusing NZB downloads");

        time.Advance(TimeSpan.FromMinutes(90));
        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().BeGreaterThan(spent, "the cooldown is over at 14:00 and the indexer is read again");
    }

    [Fact]
    public async Task A_cooldown_that_ends_on_another_day_says_the_day_too()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 3),
            Download = (_, _) => throw Quota(retryAfter: new RetryConditionHeaderValue(TimeSpan.FromDays(3))),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);
        var second = await provider.SearchAsync(Request(), CancellationToken.None);

        second.Message.Should().Be("Usenet: Indexer 1 is cooling down until 17 Oct 12:00 UTC+00:00 after refusing NZB downloads", "a day at most, whatever the indexer asked for");
    }

    [Fact]
    public async Task The_local_time_in_the_message_is_the_hosts()
    {
        var time = new FakeTimeProvider(CooldownStart);
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(1), "test", "test"));
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(FirstIndexer, TimeSpan.FromHours(2));
        var client = new QuotaIndexerClient { Answer = (indexer, query) => UsenetReleases(indexer, query, 2) };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Message.Should().Be("Usenet: Indexer 1 is cooling down until 15:00 UTC+01:00 after refusing NZB downloads", "14:00 UTC, read in the host's own zone and labelled with its offset");
    }

    [Fact]
    public async Task A_post_in_the_cache_is_still_read_while_its_indexer_is_cooling_down()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(FirstIndexer, null);
        var nzb = AlbumNzb();
        var cache = new NzbCache(time);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 6) : [],
            Download = (_, _) => throw Quota(),
        };

        foreach (var cached in client.Answer(new Indexer { Id = 1, Name = "Indexer 1" }, new ReleaseQuery("Daft Punk", "Discovery", 2001)).Take(2))
        {
            cache.Put(NzbCache.KeyOf(1, cached.DownloadUrl, cached.ReleaseId), nzb);
        }

        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache, time: time, cooldowns: cooldowns);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(0);
        result.Candidates.Should().HaveCount(2, "the two held in the cache are read for free; the other four are dropped");
        result.Candidates.Should().OnlyContain(candidate => candidate.Availability.FileListKnown);
        result.Message.Should().Be("Usenet: Indexer 1 is cooling down until 13:00 UTC+00:00 after refusing NZB downloads");
    }

    [Fact]
    public async Task An_indexer_that_refused_in_this_search_keeps_dropping_its_posts_even_though_it_is_now_cooling_down()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => throw Quota(),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: new NzbCooldowns(time));

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().BeEmpty();
        result.Message.Should().Be("Usenet: Indexer 1: The indexer answered 429.", "the refusal's own words, not a cool-down note");
    }

    [Fact]
    public async Task One_upstream_of_a_prowlarr_row_refusing_cuts_off_that_upstream_only_and_the_message_names_it()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = ProwlarrRow(refusing: "dog-", posts: 6);
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Count(entry => entry.Guid.StartsWith("geek-", StringComparison.Ordinal)).Should().Be(6, "the other upstream is read in full");
        client.DownloadLog.Count(entry => entry.Guid.StartsWith("dog-", StringComparison.Ordinal)).Should().BeInRange(1, InFlightReads);
        result.Candidates.Should().HaveCount(6).And.OnlyContain(candidate => candidate.Provider == "Prowlarr (NZBgeek)");
        result.Message.Should().Be("Usenet: Prowlarr (DOGnzb): The indexer answered 429.", "it names the upstream, not just the Prowlarr row");
        cooldowns.TryGetUntil(new NzbSource(1, "Prowlarr (DOGnzb)"), out _).Should().BeTrue();
        cooldowns.TryGetUntil(new NzbSource(1, "Prowlarr (NZBgeek)"), out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_next_search_cools_only_the_upstream_that_refused_and_reads_the_other_from_the_cache()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var client = ProwlarrRow(refusing: "dog-", posts: 6);
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns);

        await provider.SearchAsync(Request(), CancellationToken.None);
        var downloads = client.DownloadLog.Count;

        var second = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Count.Should().Be(downloads, "one upstream is cooling down and the other's NZBs are all in the cache");
        second.Candidates.Should().HaveCount(6).And.OnlyContain(candidate => candidate.Provider == "Prowlarr (NZBgeek)" && candidate.Availability.FileListKnown);
        second.Message.Should().Be("Usenet: Prowlarr (DOGnzb) is cooling down until 13:00 UTC+00:00 after refusing NZB downloads");
    }

    [Fact]
    public async Task Each_upstream_of_a_prowlarr_row_has_its_own_cap_of_8()
    {
        var client = ProwlarrRow(refusing: null, posts: 12);
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Count(entry => entry.Guid.StartsWith("dog-", StringComparison.Ordinal)).Should().Be(8);
        client.DownloadLog.Count(entry => entry.Guid.StartsWith("geek-", StringComparison.Ordinal)).Should().Be(8);
    }

    [Fact]
    public async Task An_nzb_evicted_between_the_plan_and_its_read_cannot_push_the_downloads_past_8()
    {
        var nzb = AlbumNzb();

        // Room for one NZB: the first download that finishes pushes the cached one out.
        var cache = new NzbCache(TimeProvider.System, maxEntries: 1, maxBytes: 10_000_000, lifetime: TimeSpan.FromHours(6));
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 9) : [],
            Download = (_, _) => new IndexerDownload(nzb, null),
        };

        // The last post in the read order is the one held when the search plans its reads: 8 free slots and it.
        var last = client.Answer(new Indexer { Id = 1, Name = "Indexer 1" }, new ReleaseQuery("Daft Punk", "Discovery", 2001))[^1];
        cache.Put(NzbCache.KeyOf(1, last.DownloadUrl, last.ReleaseId), nzb);

        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(8, "the cached post was gone when its turn came, and the cap of 8 downloads holds");
        client.DownloadLog.Select(entry => entry.Guid).Should().NotContain(last.ReleaseId);
        result.Candidates.Should().HaveCount(8);
    }

    [Fact]
    public async Task Two_searches_that_need_the_same_nzb_at_once_share_one_download()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger<IndexerSourceProvider>("sharing that download");
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 1) : [],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = _ => started.Release(),
        };
        var cache = new NzbCache(TimeProvider.System);
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache, logger: sharing);

        var first = provider.SearchAsync(Request(), CancellationToken.None);
        await DownloadStartedAsync(started);

        var second = provider.SearchAsync(Request(), CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Guard);

        client.DownloadLog.Should().ContainSingle("the second search waited for the first one's download");
        results.Should().OnlyContain(result => result.Candidates.Count == 1 && result.Candidates[0].Availability.FileListKnown);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_search_waiting_for_another_searchs_download_makes_it_itself_when_that_search_is_cancelled()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger<IndexerSourceProvider>("sharing that download");
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 1) : [],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
            Hold = hold.Task,
            OnDownload = _ => started.Release(),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(TimeProvider.System), logger: sharing);

        using var cancelFirst = new CancellationTokenSource();
        var first = provider.SearchAsync(Request(), cancelFirst.Token);
        await DownloadStartedAsync(started);

        var second = provider.SearchAsync(Request(), CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        await cancelFirst.CancelAsync();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();

        await DownloadStartedAsync(started);
        hold.SetResult();
        var result = await second.WaitAsync(Guard);

        result.Candidates.Should().ContainSingle("the cancelled search's download was not the second search's failure");
        result.Message.Should().BeNull();
        client.DownloadLog.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_cooling_indexer_is_logged_once_per_search_whatever_the_search_message_shows()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(FirstIndexer, TimeSpan.FromHours(2));
        var logger = new CapturingLogger<IndexerSourceProvider>();
        var client = new QuotaIndexerClient { Answer = (indexer, query) => UsenetReleases(indexer, query, 12) };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns, logger: logger);

        await provider.SearchAsync(Request(), CancellationToken.None);

        logger.Lines.Count(line => line.Contains("Indexer 1 is cooling down until", StringComparison.Ordinal))
            .Should().Be(1, "12 posts dropped in each of three queries are one line for the search");

        await provider.SearchAsync(Request(), CancellationToken.None);

        logger.Lines.Count(line => line.Contains("Indexer 1 is cooling down until", StringComparison.Ordinal)).Should().Be(2, "one more for the second search");
    }

    [Fact]
    public async Task An_indexer_that_refused_in_this_search_is_not_also_reported_as_cooling_down()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var logger = new CapturingLogger<IndexerSourceProvider>();
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => throw Quota(),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns, logger: logger);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Message.Should().NotContain("cooling down");
        logger.Lines.Should().NotContain(line => line.Contains("cooling down", StringComparison.Ordinal));
        logger.Lines.Should().ContainSingle(line => line.Contains("refused an NZB download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_searches_sharing_a_refused_download_are_both_refused_and_the_indexer_cools_down_once()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var sharing = new SignalLogger<IndexerSourceProvider>("sharing that download");
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 1) : [],
            Download = (_, _) => throw Quota(retryAfter: new RetryConditionHeaderValue(TimeSpan.FromHours(2))),
            Hold = hold.Task,
            OnDownload = _ => started.Release(),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: new NzbCache(time), time: time, cooldowns: cooldowns, logger: sharing);

        var first = provider.SearchAsync(Request(), CancellationToken.None);
        await DownloadStartedAsync(started);

        var second = provider.SearchAsync(Request(), CancellationToken.None);
        await sharing.Seen.WaitAsync(Guard);

        hold.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Guard);

        client.DownloadLog.Should().ContainSingle();
        results.Should().OnlyContain(result => result.Message == "Usenet: Indexer 1: The indexer answered 429.");
        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddHours(2));
    }

    [Fact]
    public async Task A_429_from_a_real_indexer_client_is_one_request_per_post_and_cuts_the_indexer_off_for_two_hours()
    {
        var time = new FakeTimeProvider(CooldownStart);
        var cooldowns = new NzbCooldowns(time);
        var handler = new ConcurrentRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            return path.StartsWith("/nzb/", StringComparison.Ordinal)
                ? ThrottledResponse(TimeSpan.FromHours(2))
                : request.RequestUri.Query.Contains("t=caps", StringComparison.Ordinal)
                    ? TorznabFixtures.Response("caps-q-only.xml")
                    : Feed(Uri.UnescapeDataString(request.RequestUri.Query).Contains("Discovery", StringComparison.Ordinal) ? 12 : 0);
        });
        await using var services = NzbDownloadRetryTests.BuildProvider(time, handler);
        var provider = Provider(
            services.GetRequiredService<NewznabIndexerClient>(),
            out _,
            protocol: DownloadProtocol.Usenet,
            indexerSettings: NewznabSettings,
            nzbCache: new NzbCache(time),
            time: time,
            cooldowns: cooldowns);

        // A 429 is not retried, so nothing waits on the clock; the guard only turns a retry (which would wait for it) into a failure.
        var result = await provider.SearchAsync(Request(), CancellationToken.None).WaitAsync(Guard);

        var downloads = handler.Requests.Where(uri => uri.AbsolutePath.StartsWith("/nzb/", StringComparison.Ordinal)).ToList();
        downloads.Should().HaveCountGreaterThanOrEqualTo(1).And.HaveCountLessThanOrEqualTo(InFlightReads);
        downloads.Should().OnlyHaveUniqueItems("a 429 to an NZB download is not retried");
        result.Message.Should().Be("Usenet: Indexer 1: The indexer answered 429.");
        cooldowns.TryGetUntil(FirstIndexer, out var until).Should().BeTrue();
        until.Should().Be(CooldownStart.AddHours(2));
    }

    /// <summary>Waits for the next download to start; a semaphore's timed wait answers false instead of throwing.</summary>
    private static async Task DownloadStartedAsync(SemaphoreSlim started) =>
        (await started.WaitAsync(Guard)).Should().BeTrue("a download should have started");

    private static IndexerException Quota(HttpStatusCode status = HttpStatusCode.TooManyRequests, RetryConditionHeaderValue? retryAfter = null) =>
        new($"The indexer answered {(int)status}.", status, retryAfter);

    private static HttpResponseMessage ThrottledResponse(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }

    /// <summary>A Newznab feed with <paramref name="count"/> FLAC posts whose NZB links are <c>/nzb/n.nzb</c>.</summary>
    private static HttpResponseMessage Feed(int count)
    {
        var items = string.Concat(Enumerable.Range(0, count).Select(index => $$"""
            <item>
              <title>Daft Punk - Discovery [2001] [FLAC]</title>
              <guid isPermaLink="false">post-{{index}}</guid>
              <link>https://indexer.example/details/{{index}}</link>
              <enclosure url="https://indexer.example/nzb/{{index}}.nzb" type="application/x-nzb" length="3412345678"/>
              <newznab:attr name="grabs" value="{{100 - index}}"/>
              <newznab:attr name="category" value="3040"/>
            </item>
            """));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
                  <channel><title>Example Newznab</title>{{items}}</channel>
                </rss>
                """,
                System.Text.Encoding.UTF8,
                "application/xml"),
        };
    }

    /// <summary>
    /// A Prowlarr row (id 1) listing the same posts from two upstream indexers, "Prowlarr (DOGnzb)" for the
    /// guids that start with <c>dog-</c> and "Prowlarr (NZBgeek)" for <c>geek-</c>. The posts of the upstream
    /// named by <paramref name="refusing"/> are answered with a 429.
    /// </summary>
    private static QuotaIndexerClient ProwlarrRow(string? refusing, int posts) => new()
    {
        Answer = (_, query) => query.Album == "Discovery"
            ?
            [
                .. Enumerable.Range(0, posts).Select(index => UsenetRelease(Flac, $"dog-{index}", grabs: 100 - index)),
                .. Enumerable.Range(0, posts).Select(index => UsenetRelease(Flac, $"geek-{index}", grabs: 50 - index)),
            ]
            : [],
        NameOf = release => release.ReleaseId.StartsWith("dog-", StringComparison.Ordinal) ? "Prowlarr (DOGnzb)" : "Prowlarr (NZBgeek)",
        Download = (_, release) => refusing is not null && release.ReleaseId.StartsWith(refusing, StringComparison.Ordinal)
            ? throw Quota()
            : new IndexerDownload(AlbumNzb(), null),
    };

    /// <summary>An <see cref="HttpMessageHandler"/> that keeps the URL of every request, safe for the parallel reads.</summary>
    private sealed class ConcurrentRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();

        public IReadOnlyCollection<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!);

            return Task.FromResult(responder(request));
        }
    }

    /// <summary>A logger that completes <see cref="Seen"/> the first time a line holds the text it waits for.</summary>
    private sealed class SignalLogger<T>(string needle) : ILogger<T>
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
