using System.Net;
using System.Net.Http.Headers;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Http;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Metadata;

/// <summary>
/// A <c>Retry-After</c> on any 429 or 503 becomes a cooldown the whole host shares: a person waiting
/// on a page fails fast when it is long, a background caller waits it out.
/// </summary>
public sealed class RequestSpacingCooldownTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_long_retry_after_makes_an_interactive_second_request_fail_without_touching_the_network()
    {
        var rig = new Rig(Throttled(TimeSpan.FromSeconds(120)));

        (await rig.SendAsync()).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        rig.Gate.Cooldown.Should().Be(TimeSpan.FromSeconds(120));

        using var interactive = InteractiveRequests.Begin();
        var second = () => rig.SendAsync();

        var thrown = await second.Should().ThrowAsync<MetadataProviderException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        thrown.Which.Provider.Should().Be("musicbrainz");
        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        rig.Inner.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_background_second_request_waits_the_cooldown_out()
    {
        var rig = new Rig(Throttled(TimeSpan.FromSeconds(120)));
        await rig.SendAsync();

        var second = rig.SendAsync();

        rig.Time.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();
        rig.Inner.Requests.Should().Be(1);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        (await second).StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Inner.Requests.Should().Be(2);
    }

    [Fact]
    public async Task A_background_wait_honours_cancellation()
    {
        var rig = new Rig(Throttled(TimeSpan.FromSeconds(120)));
        await rig.SendAsync();

        using var cancel = new CancellationTokenSource();
        var second = rig.SendAsync(cancel.Token);
        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => second).Should().ThrowAsync<OperationCanceledException>();
        rig.Inner.Requests.Should().Be(1);
    }

    [Fact]
    public async Task An_http_date_retry_after_is_read_against_the_clock()
    {
        var rig = new Rig(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Start.AddSeconds(40));

            return response;
        });

        await rig.SendAsync();

        rig.Gate.Cooldown.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task A_retry_after_is_capped_at_five_minutes()
    {
        var rig = new Rig(Throttled(TimeSpan.FromHours(2)));

        await rig.SendAsync();

        rig.Gate.Cooldown.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Fifteen_seconds_is_waited_for_but_sixteen_fails_fast()
    {
        var fifteen = new Rig(Throttled(TimeSpan.FromSeconds(15)));
        await fifteen.SendAsync();

        using (InteractiveRequests.Begin())
        {
            var waiting = fifteen.SendAsync();
            await Task.Delay(50);
            waiting.IsCompleted.Should().BeFalse();

            fifteen.Time.Advance(TimeSpan.FromSeconds(15));
            (await waiting).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var sixteen = new Rig(Throttled(TimeSpan.FromSeconds(16)));
        await sixteen.SendAsync();

        using (InteractiveRequests.Begin())
        {
            await FluentActions.Awaiting(() => sixteen.SendAsync()).Should().ThrowAsync<MetadataProviderException>();
        }

        sixteen.Inner.Requests.Should().Be(1);
    }

    [Fact]
    public async Task A_cooldown_is_never_shortened_by_a_later_shorter_one()
    {
        var rig = new Rig(Throttled(TimeSpan.FromSeconds(100)));
        await rig.SendAsync();

        rig.Gate.DelayUntil(Start.AddSeconds(10));

        rig.Gate.Cooldown.Should().Be(TimeSpan.FromSeconds(100));
    }

    private static Func<int, HttpResponseMessage> Throttled(TimeSpan retryAfter) => request =>
    {
        if (request > 1)
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    };

    // Lives for one test; the invoker and handler hold nothing that needs releasing.
#pragma warning disable CA1001
    private sealed class Rig
    {
        private readonly HttpMessageInvoker _invoker;

        public Rig(Func<int, HttpResponseMessage> script)
        {
            Time = new FakeTimeProvider(Start);
            Gate = new RequestSpacingGate(TimeSpan.Zero, Time);
            Inner = new Stub(script);
            _invoker = new HttpMessageInvoker(new RequestSpacingHandler(Gate, "musicbrainz") { InnerHandler = Inner });
        }

        public FakeTimeProvider Time { get; }

        public RequestSpacingGate Gate { get; }

        public Stub Inner { get; }

        public Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default) =>
            _invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://musicbrainz.test/x"), cancellationToken);
    }

#pragma warning restore CA1001

    private sealed class Stub(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requests);

            return Task.FromResult(script(count));
        }
    }
}
