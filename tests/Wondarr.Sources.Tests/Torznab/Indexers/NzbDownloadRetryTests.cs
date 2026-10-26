using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Tests.Metadata;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// What the named indexer client's retry handler does with a quota answer (M-47): a 429 to an NZB download
/// goes straight to the caller, with the <c>Retry-After</c> it carried, while every other request keeps the
/// retry it always had (M-53: but a 5xx on an NZB download is retried once, after at most 5 s, and a pushed
/// usenet link is an NZB download too). The real handler pipeline runs on a fake clock over a stub primary
/// handler.
/// </summary>
public sealed class NzbDownloadRetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_429_to_an_nzb_download_is_answered_once_and_carries_its_retry_after()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => Throttled(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        var thrown = await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        thrown.Which.RetryAfter!.Delta.Should().Be(TimeSpan.FromHours(2));
        handler.Requests.Should().ContainSingle("a retry is another doomed download, and the Retry-After would outlast the request timeout");
    }

    [Fact]
    public async Task A_429_without_a_retry_after_to_an_nzb_download_is_not_retried_either()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        var thrown = await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        thrown.Which.RetryAfter.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_429_to_a_prowlarr_nzb_download_is_answered_once_and_carries_its_retry_after()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => Throttled(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<ProwlarrSearchClient>();

        var pending = client.DownloadAsync(TorznabFixtures.Indexer(type: "prowlarr", protocol: DownloadProtocol.Usenet), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        var thrown = await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        thrown.Which.RetryAfter!.Delta.Should().Be(TimeSpan.FromHours(2));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_429_to_a_torrent_download_is_still_retried()
    {
        var time = new TrackingTimeProvider(Start);
        var bytes = new byte[] { 1, 2, 3 };
        var calls = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<TorznabIndexerClient>();

        var pending = client.DownloadAsync(TorznabFixtures.Indexer(), Release(DownloadProtocol.Torrent), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Content.Should().Equal(bytes);
        handler.Requests.Should().HaveCount(2, "only a usenet download is a quota download");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_5xx_to_an_nzb_download_is_still_retried(HttpStatusCode status)
    {
        var time = new TrackingTimeProvider(Start);
        var calls = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(status)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Content.Should().Equal(1, 2, 3);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_nzb_download_with_a_long_retry_after_on_a_5xx_is_retried_once_and_waits_at_most_5_seconds(HttpStatusCode status)
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => Throttled(status, TimeSpan.FromSeconds(120)));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        var thrown = await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        thrown.Which.StatusCode.Should().Be(status, "the last answer reaches the caller as an answer, not as 'could not be reached'");
        handler.Requests.Should().HaveCount(2, "one retry, however many the pipeline allows other requests");
        (time.GetUtcNow() - Start).Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(IndexerHttp.NzbRetryMaxDelay, "the 120 s Retry-After is cut to 5 s");
    }

    [Fact]
    public async Task An_nzb_download_with_a_short_retry_after_on_a_5xx_waits_for_it()
    {
        var time = new TrackingTimeProvider(Start);
        var calls = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? Throttled(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(3))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Content.Should().Equal(1, 2, 3);
        handler.Requests.Should().HaveCount(2);
        (time.GetUtcNow() - Start).Should().Be(TimeSpan.FromSeconds(3), "a Retry-After under the cap is obeyed as given");
    }

    [Fact]
    public async Task An_nzb_download_that_keeps_getting_a_503_without_a_retry_after_is_asked_twice_at_most()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        handler.Requests.Should().HaveCount(2);
        (time.GetUtcNow() - Start).Should().BeLessThanOrEqualTo(IndexerHttp.NzbRetryMaxDelay);
    }

    [Fact]
    public async Task An_nzb_download_that_cannot_connect_is_asked_twice_at_most()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>().WithMessage("The indexer could not be reached.");
        handler.Requests.Should().HaveCount(2, "a transport failure gets the same single retry as a 5xx");
        (time.GetUtcNow() - Start).Should().BeLessThanOrEqualTo(IndexerHttp.NzbRetryMaxDelay);
    }

    [Fact]
    public async Task A_408_to_an_nzb_download_is_asked_twice_at_most()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestTimeout));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.DownloadAsync(NewznabRow(), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_search_request_that_cannot_connect_is_still_asked_three_times()
    {
        var time = new TrackingTimeProvider(Start);
        var searches = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal))
            {
                return TorznabFixtures.Response("caps-q-only.xml");
            }

            Interlocked.Increment(ref searches);

            throw new HttpRequestException("connection refused");
        });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.SearchAsync(NewznabRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        searches.Should().Be(3);
    }

    [Fact]
    public async Task A_search_request_with_a_long_retry_after_on_a_503_still_waits_for_all_of_it()
    {
        var time = new TrackingTimeProvider(Start);
        var searches = 0;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
                ? TorznabFixtures.Response("caps-q-only.xml")
                : Interlocked.Increment(ref searches) == 1
                    ? Throttled(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(120))
                    : TorznabFixtures.Response("results-newznab.xml"));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.SearchAsync(NewznabRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Should().HaveCount(2);
        searches.Should().Be(2);
        (time.GetUtcNow() - Start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120), "only an NZB download has its wait cut short");
    }

    [Fact]
    public async Task A_search_request_that_keeps_getting_a_503_is_still_asked_three_times()
    {
        var time = new TrackingTimeProvider(Start);
        var searches = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal))
            {
                return TorznabFixtures.Response("caps-q-only.xml");
            }

            Interlocked.Increment(ref searches);

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.SearchAsync(NewznabRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>();
        searches.Should().Be(3, "the first request and the two retries every other request gets");
    }

    [Fact]
    public async Task A_429_to_a_pushed_usenet_release_is_answered_once_and_not_retried()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new RecordingHandler(_ => Throttled(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<PushedReleaseClient>();

        var pending = client.DownloadAsync(PushedReleaseClient.Row("Pushed Indexer", DownloadProtocol.Usenet), Release(DownloadProtocol.Usenet), CancellationToken.None);
        await DriveAsync(time, pending);

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IndexerException>().WithMessage("The pushed release's link answered 429.");
        handler.Requests.Should().ContainSingle("a retry is another doomed download, and the Retry-After would outlast the request timeout");
    }

    [Fact]
    public async Task A_429_to_a_pushed_torrent_release_is_still_retried()
    {
        var time = new TrackingTimeProvider(Start);
        var calls = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<PushedReleaseClient>();

        var pending = client.DownloadAsync(PushedReleaseClient.Row("Pushed Indexer", DownloadProtocol.Torrent), Release(DownloadProtocol.Torrent), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Content.Should().Equal(1, 2, 3);
        handler.Requests.Should().HaveCount(2, "only a usenet link is a quota download");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_search_request_that_is_throttled_is_still_retried(HttpStatusCode status)
    {
        var time = new TrackingTimeProvider(Start);
        var searches = 0;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
                ? TorznabFixtures.Response("caps-q-only.xml")
                : Interlocked.Increment(ref searches) == 1
                    ? new HttpResponseMessage(status)
                    : TorznabFixtures.Response("results-newznab.xml"));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.SearchAsync(NewznabRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Should().HaveCount(2);
        searches.Should().Be(2, "the first search request was throttled and asked again");
    }

    [Fact]
    public async Task A_caps_request_that_is_throttled_is_still_retried()
    {
        var time = new TrackingTimeProvider(Start);
        var capsRequests = 0;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
                ? Interlocked.Increment(ref capsRequests) == 1
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    : TorznabFixtures.Response("caps-q-only.xml")
                : TorznabFixtures.Response("results-newznab.xml"));
        await using var provider = BuildProvider(time, handler);
        var client = provider.GetRequiredService<NewznabIndexerClient>();

        var pending = client.SearchAsync(NewznabRow(), new ReleaseQuery("Daft Punk", "Discovery", 2001), CancellationToken.None);
        await DriveAsync(time, pending);

        (await pending).Should().HaveCount(2);
        capsRequests.Should().Be(2);
    }

    /// <summary>
    /// Moves the fake clock until the call finishes, but only while the pipeline waits on it, and only as far as
    /// the wait it armed: a clock moved while the pipeline is still at work (its retry continuation runs on the
    /// thread pool) runs on past the delays these tests measure, hours past them on a loaded machine. A
    /// real-time guard turns a pipeline that never arms a wait on the injected clock into a failure, not a hang.
    /// </summary>
    internal static async Task DriveAsync(TrackingTimeProvider time, Task pending)
    {
        var guard = Stopwatch.StartNew();

        while (!pending.IsCompleted)
        {
            guard.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the pipeline must run on the injected clock");

            if (time.UntilNextTimer() is { } wait)
            {
                time.Advance(wait);
            }

            await Task.Yield();
        }
    }

    /// <summary>The real handler pipeline of the named indexer client, on a fake clock, over a stub primary handler.</summary>
    internal static ServiceProvider BuildProvider(FakeTimeProvider time, HttpMessageHandler primary)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ISecretRegistry, SecretRegistry>();
        services.AddWondarrTorznab();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primary));

        return services.BuildServiceProvider();
    }

    private static Indexer NewznabRow() => TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet);

    private static IndexerRelease Release(DownloadProtocol protocol) => new(
        "Daft Punk - Discovery [2001] [FLAC]",
        "guid-1",
        "https://indexer.example/get/1?apikey=" + TorznabFixtures.ApiKey,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        [3040],
        1,
        null,
        protocol,
        1,
        "Example Indexer",
        null);

    private static HttpResponseMessage Throttled(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }
}
