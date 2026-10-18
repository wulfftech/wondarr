using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Wondarr.Core.Tests.Metadata;

/// <summary>
/// What the MusicBrainz HttpClient pipeline does with a 503: retry through the spacing gate, obey
/// <c>Retry-After</c>, retry at most twice, and not sit out a wait longer than a request can afford.
/// </summary>
public sealed class MusicBrainzRetryTests
{
    private const string EmptySearch = """{"count":0,"recordings":[]}""";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One round of a retry wait the test does not need to time exactly: longer than any backoff the 10 ms
    /// base delay jitters to, shorter than the 30 s an attempt may take.
    /// </summary>
    private static readonly TimeSpan BackoffStep = TimeSpan.FromSeconds(1);

    /// <summary>The longest real time the test waits for the pipeline to make progress before it fails.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_503_is_retried_once_and_the_second_answer_is_returned()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new CountingHandler(time, Busy(), Ok());

        await using var provider = BuildProvider(handler, time);
        var search = provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        await RetryUntilDoneAsync(time, search);

        (await search).Recordings.Should().BeEmpty();
        handler.Requests.Should().Be(2);
        AssertRetriedOnTheFakeClock(time);
    }

    [Fact]
    public async Task A_background_503_that_never_clears_is_retried_three_times_and_then_reported()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new CountingHandler(time, Busy(), Busy(), Busy(), Busy(), Busy());

        await using var provider = BuildProvider(handler, time);
        var search = provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        await RetryUntilDoneAsync(time, search);

        var thrown = await FluentActions.Awaiting(() => search).Should().ThrowAsync<MetadataProviderException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        thrown.Which.Provider.Should().Be("musicbrainz");
        handler.Requests.Should().Be(4);
        AssertRetriedOnTheFakeClock(time);
    }

    [Fact]
    public async Task An_interactive_503_that_never_clears_is_retried_twice_and_then_reported()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new CountingHandler(time, Busy(), Busy(), Busy(), Busy(), Busy());

        await using var provider = BuildProvider(handler, time);
        using var interactive = InteractiveRequests.Begin();
        var search = provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        await RetryUntilDoneAsync(time, search);

        await FluentActions.Awaiting(() => search).Should().ThrowAsync<MetadataProviderException>();
        handler.Requests.Should().Be(3);
        AssertRetriedOnTheFakeClock(time);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_interactive_budget_is_not_waited_out()
    {
        var time = new TrackingTimeProvider(Start);
        var handler = new CountingHandler(time, Busy(retryAfter: TimeSpan.FromMinutes(5)), Ok());

        await using var provider = BuildProvider(handler, time);
        using var interactive = InteractiveRequests.Begin();
        var search = provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        // A 300 s wait is never due within a step, so the clock stays where it is: a call that waited
        // the Retry-After out would still be pending when the guard runs out.
        await RetryUntilDoneAsync(time, search);

        var thrown = await FluentActions.Awaiting(() => search).Should().ThrowAsync<MetadataProviderException>();
        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(300));

        handler.Requests.Should().Be(1);
        (time.GetUtcNow() - Start).Should().Be(TimeSpan.Zero, "the Retry-After is surfaced at once, not waited out");
    }

    [Fact]
    public async Task A_short_retry_after_is_honoured_before_the_retry()
    {
        var time = new TrackingTimeProvider(Start);
        var retryAfter = TimeSpan.FromSeconds(1);
        var handler = new CountingHandler(time, Busy(retryAfter: retryAfter), Ok());

        await using var provider = BuildProvider(handler, time);
        var search = provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        // The clock only moves once the pipeline is parked on its retry wait: a step taken before it has
        // armed the delay is lost, and the call then waits for a retry that never comes.
        await ParkedAsync(time, search, retryAfter);

        // A plain 10 ms backoff would have retried by now; only the Retry-After holds the retry back.
        var shortOfIt = retryAfter - TimeSpan.FromMilliseconds(100);
        await time.AdvanceAndSettleAsync(shortOfIt);
        handler.Requests.Should().Be(1, "the retry waits for the Retry-After, not for the backoff");

        await time.AdvanceAndSettleAsync(retryAfter - shortOfIt, () => search.IsCompleted);
        await RetryUntilDoneAsync(time, search);

        handler.Requests.Should().Be(2);
        (handler.Stamps[1] - handler.Stamps[0]).Should().BeGreaterThanOrEqualTo(retryAfter);
    }

    private static Func<HttpResponseMessage> Busy(TimeSpan? retryAfter = null) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        }

        return response;
    };

    private static Func<HttpResponseMessage> Ok() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(EmptySearch, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// Moves the fake clock round by round until the call is over, so no retry wait costs real time. A
    /// call that stays pending with no retry wait to end fails the test instead of hanging it.
    /// </summary>
    private static async Task RetryUntilDoneAsync(TrackingTimeProvider time, Task call)
    {
        var guard = Stopwatch.StartNew();

        while (!call.IsCompleted)
        {
            guard.Elapsed.Should().BeLessThan(HangGuard, "the pipeline must make progress");

            if (time.AnyTimerDueWithin(BackoffStep))
            {
                await time.AdvanceAndSettleAsync(BackoffStep, () => call.IsCompleted);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// A pipeline wired to the system clock would also finish, on real time, with the fake clock never
    /// moved; so a fake clock that has moved shows the retry waits ran on it.
    /// </summary>
    private static void AssertRetriedOnTheFakeClock(TrackingTimeProvider time) =>
        (time.GetUtcNow() - Start).Should().BeGreaterThan(TimeSpan.Zero, "the retries waited on the fake clock");

    /// <summary>
    /// Waits, bounded, until the pipeline is parked on a fake-clock wait that ends within
    /// <paramref name="within"/>, or the call is over.
    /// </summary>
    private static async Task ParkedAsync(TrackingTimeProvider time, Task call, TimeSpan within)
    {
        var guard = Stopwatch.StartNew();

        while (!call.IsCompleted && !time.AnyTimerDueWithin(within))
        {
            guard.Elapsed.Should().BeLessThan(HangGuard, "the pipeline must reach its retry wait");

            await Task.Yield();
        }

        call.IsCompleted.Should().BeFalse("the 503 is retried, not returned");
    }

    private static ClockedServiceProvider BuildProvider(HttpMessageHandler primary, TrackingTimeProvider time)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        // Before AddWondarrMetadata, which only adds the system clock when none is registered.
        services.AddSingleton<TimeProvider>(time);
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, NoCache>();

        // A mirror is not rate limited as the public site is, and only tests lower the retry delay.
        services.Configure<MetadataOptions>(options =>
        {
            options.MusicBrainzBaseUrl = "http://musicbrainz.test/ws/2/";
            options.MusicBrainzMirrorIntervalMs = 0;
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
        });

        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubFilter(primary));

        return new ClockedServiceProvider(services.BuildServiceProvider(), time);
    }

    private sealed class StubFilter(HttpMessageHandler handler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                builder.PrimaryHandler = handler;
            };
    }

    private sealed class CountingHandler(TimeProvider time, params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;

        public int Requests => Volatile.Read(ref _next);

        public List<DateTimeOffset> Stamps { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Stamps)
            {
                Stamps.Add(time.GetUtcNow());
            }

            var index = Interlocked.Increment(ref _next) - 1;

            return Task.FromResult(script[Math.Min(index, script.Length - 1)]());
        }
    }

    private sealed class NoCache : IMetadataCache
    {
        public Task<string?> GetAsync(string provider, string key, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string provider, string key, string payload, TimeSpan ttl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
