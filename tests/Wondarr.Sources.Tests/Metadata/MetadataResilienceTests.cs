using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Wondarr.Core.Logging;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Metadata.ITunes;
using Wondarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// R-029 and R-061: a long <c>Retry-After</c> is waited out rather than timed out, and a 2xx body that
/// is not the expected JSON is never cached.
/// </summary>
public sealed class MetadataResilienceTests
{
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";
    private const string HtmlBody = "<!doctype html><html><body>Sign in to the hotel wifi</body></html>";

    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("IMusicBrainzClient")]
    [InlineData("ICoverArtArchiveClient")]
    [InlineData("IDeezerClient")]
    [InlineData("IITunesClient")]
    [InlineData("IAcoustIdClient")]
    public void No_metadata_client_times_out_before_the_longest_cooldown_it_can_be_asked_to_wait_out(string clientName)
    {
        using var provider = BuildProvider(new FakeTimeProvider(Start), new ScriptedByCount());

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        // Either no HttpClient limit at all (the retried clients limit each attempt in their pipeline
        // instead), or one comfortably above the longest Retry-After.
        (client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > RetryAfterHeader.MaxWait + TimeSpan.FromSeconds(10))
            .Should().BeTrue($"{clientName} must outlast a {RetryAfterHeader.MaxWait.TotalSeconds:0} s Retry-After; it has {client.Timeout}");
    }

    [Fact]
    public async Task A_503_with_retry_after_180_is_waited_out_and_the_retry_succeeds()
    {
        var time = new FakeTimeProvider(Start);
        var stub = new ScriptedByCount(
            _ => Throttled(TimeSpan.FromSeconds(180)),
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(time, stub);

        var pending = provider.GetRequiredService<IMusicBrainzClient>().GetRecordingAsync(BohemianRhapsodyId);

        await DriveAsync(time, pending);

        (await pending)!.Length.Should().Be(355106);
        stub.Times.Should().HaveCount(2);
        (stub.Times[1] - stub.Times[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(180));
    }

    [Fact]
    public async Task A_retry_after_beyond_the_cap_is_clamped_not_obeyed_for_ever()
    {
        var time = new FakeTimeProvider(Start);
        var stub = new ScriptedByCount(
            _ => Throttled(TimeSpan.FromHours(2)),
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(time, stub);

        var pending = provider.GetRequiredService<IMusicBrainzClient>().GetRecordingAsync(BohemianRhapsodyId);

        await DriveAsync(time, pending);

        await pending;
        var gap = stub.Times[1] - stub.Times[0];
        gap.Should().BeGreaterThanOrEqualTo(RetryAfterHeader.MaxWait);
        gap.Should().BeLessThan(RetryAfterHeader.MaxWait * 2);
    }

    [Fact]
    public async Task A_musicbrainz_page_that_is_html_is_not_cached_and_the_next_call_goes_to_the_network()
    {
        var handler = new FixtureHttpMessageHandler(_ => MusicBrainzFixtures.Json(HtmlBody));
        var cache = new InMemoryMetadataCache();
        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            cache,
            NullLogger<MusicBrainzClient>.Instance);

        for (var call = 1; call <= 2; call++)
        {
            var thrown = await FluentActions
                .Awaiting(() => client.GetRecordingAsync(BohemianRhapsodyId))
                .Should().ThrowAsync<MetadataProviderException>();

            thrown.Which.Provider.Should().Be("musicbrainz");
            handler.Requests.Should().HaveCount(call);
        }

        cache.SetCount.Should().Be(0);
    }

    [Fact]
    public async Task A_deezer_page_that_is_html_is_not_cached_and_the_next_call_goes_to_the_network()
    {
        var handler = new FixtureHttpMessageHandler(_ => MusicBrainzFixtures.Json(HtmlBody));
        var cache = new InMemoryMetadataCache();
        var client = new DeezerClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/") },
            cache,
            NullLogger<DeezerClient>.Instance);

        for (var call = 1; call <= 2; call++)
        {
            var thrown = await FluentActions
                .Awaiting(() => client.GetTrackAsync(3135556))
                .Should().ThrowAsync<MetadataProviderException>();

            thrown.Which.Provider.Should().Be("deezer");
            handler.Requests.Should().HaveCount(call);
        }

        cache.SetCount.Should().Be(0);
    }

    [Fact]
    public async Task An_itunes_page_that_is_html_is_not_cached_and_the_next_call_goes_to_the_network()
    {
        var handler = new FixtureHttpMessageHandler(_ => MusicBrainzFixtures.Json(HtmlBody));
        var cache = new InMemoryMetadataCache();
        var client = new ITunesClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://itunes.apple.com/") },
            cache,
            NullLogger<ITunesClient>.Instance);

        for (var call = 1; call <= 2; call++)
        {
            var thrown = await FluentActions
                .Awaiting(() => client.SearchSongsAsync("queen bohemian rhapsody"))
                .Should().ThrowAsync<MetadataProviderException>();

            thrown.Which.Provider.Should().Be("itunes");
            handler.Requests.Should().HaveCount(call);
        }

        cache.SetCount.Should().Be(0);
    }

    [Fact]
    public async Task A_body_that_does_parse_is_still_cached()
    {
        var handler = new FixtureHttpMessageHandler(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));
        var cache = new InMemoryMetadataCache();
        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            cache);

        await client.GetRecordingAsync(BohemianRhapsodyId);
        await client.GetRecordingAsync(BohemianRhapsodyId);

        cache.SetCount.Should().Be(1);
        handler.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// Moves the fake clock a second at a time, with the pipeline's threads given a turn in between,
    /// until the call finishes. A real-time guard turns a clock the pipeline ignores into a failure
    /// instead of a hang.
    /// </summary>
    private static async Task DriveAsync(FakeTimeProvider time, Task pending)
    {
        var guard = Stopwatch.StartNew();

        while (!pending.IsCompleted)
        {
            guard.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the pipeline must run on the injected clock");

            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
    }

    private static HttpResponseMessage Throttled(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }

    private static ServiceProvider BuildProvider(FakeTimeProvider time, ScriptedByCount primary)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ISecretRegistry, SecretRegistry>();
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        // The shipped base delay is 2 s; Retry-After overrides it, but a plain retry should not be slow either.
        services.Configure<MetadataOptions>(options => options.RetryBaseDelay = TimeSpan.FromMilliseconds(10));
        primary.Clock = time;
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primary));

        return services.BuildServiceProvider();
    }

    /// <summary>Answers the n-th request with the n-th script entry, and stamps it with the fake clock.</summary>
    private sealed class ScriptedByCount : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _script;
        private int _calls;

        public ScriptedByCount(params Func<HttpRequestMessage, HttpResponseMessage>[] script) => _script = script;

        public FakeTimeProvider? Clock { get; set; }

        public List<DateTimeOffset> Times { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Times)
            {
                Times.Add(Clock!.GetUtcNow());
            }

            var index = Interlocked.Increment(ref _calls) - 1;

            return Task.FromResult(_script[index](request));
        }
    }
}
