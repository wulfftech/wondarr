using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.MusicBrainz;
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

    [Fact]
    public async Task A_503_is_retried_once_and_the_second_answer_is_returned()
    {
        var handler = new CountingHandler(Busy(), Ok());

        await using var provider = BuildProvider(handler);
        var result = await provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        result.Recordings.Should().BeEmpty();
        handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task A_503_that_never_clears_is_retried_twice_and_then_reported()
    {
        var handler = new CountingHandler(Busy(), Busy(), Busy(), Busy());

        await using var provider = BuildProvider(handler);
        var search = () => provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        var thrown = await search.Should().ThrowAsync<MetadataProviderException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        thrown.Which.Provider.Should().Be("musicbrainz");
        handler.Requests.Should().Be(3);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_budget_is_not_waited_out()
    {
        var handler = new CountingHandler(Busy(retryAfter: TimeSpan.FromMinutes(5)), Ok());

        await using var provider = BuildProvider(handler);
        var started = DateTimeOffset.UtcNow;
        var search = () => provider.GetRequiredService<IMusicBrainzClient>()
            .SearchRecordingsAsync("recording:\"x\"", 5);

        await search.Should().ThrowAsync<MetadataProviderException>();

        handler.Requests.Should().Be(1);
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_short_retry_after_is_honoured_before_the_retry()
    {
        var handler = new CountingHandler(Busy(retryAfter: TimeSpan.FromSeconds(1)), Ok());

        await using var provider = BuildProvider(handler);
        await provider.GetRequiredService<IMusicBrainzClient>().SearchRecordingsAsync("recording:\"x\"", 5);

        handler.Requests.Should().Be(2);
        (handler.Stamps[1] - handler.Stamps[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
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

    private static ServiceProvider BuildProvider(HttpMessageHandler primary)
    {
        var services = new ServiceCollection();

        services.AddLogging();
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

        return services.BuildServiceProvider();
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

    private sealed class CountingHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;

        public int Requests => Volatile.Read(ref _next);

        public List<DateTimeOffset> Stamps { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Stamps)
            {
                Stamps.Add(DateTimeOffset.UtcNow);
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
