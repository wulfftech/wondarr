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

    [Fact]
    public async Task A_cooldown_set_while_requests_are_queued_holds_back_the_requests_already_waiting()
    {
        var fake = new FakeTimeProvider(Start);
        var time = new CountingTimeProvider(fake);
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        // Four requests are queued at t, t+1, t+2 and t+3. The first goes out at once and is answered
        // with Retry-After: 60 before the other three have started.
        var waiters = Enumerable.Range(0, 4).Select(_ => gate.WaitAsync(CancellationToken.None).AsTask()).ToArray();
        waiters[0].IsCompleted.Should().BeTrue();
        time.TimersCreated.Should().Be(3);

        gate.DelayUntil(Start.AddSeconds(60));

        // Their own slots (1 to 3 s) pass. Each waiter must take its new slot, which shows as a second
        // timer, before the clock moves on: the continuations run on the thread pool.
        fake.Advance(TimeSpan.FromSeconds(3));
        await UntilAsync(() => time.TimersCreated == 6);

        // Up to a second before the cooldown ends: none goes out.
        fake.Advance(TimeSpan.FromSeconds(56));
        waiters.Skip(1).Should().OnlyContain(waiter => !waiter.IsCompleted);

        // The cooldown ends: they leave one slot apart, not all at once.
        fake.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => waiters.Skip(1).Any(waiter => waiter.IsCompleted));
        waiters.Skip(1).Count(waiter => waiter.IsCompleted).Should().Be(1);

        fake.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => waiters.Skip(1).Count(waiter => waiter.IsCompleted) >= 2);
        waiters.Skip(1).Count(waiter => waiter.IsCompleted).Should().Be(2);

        fake.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_longer_cooldown_set_later_also_holds_back_a_waiter_that_was_already_moved()
    {
        var fake = new FakeTimeProvider(Start);
        var time = new CountingTimeProvider(fake);
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        await gate.WaitAsync(CancellationToken.None);
        var waiting = gate.WaitAsync(CancellationToken.None).AsTask();
        time.TimersCreated.Should().Be(1);

        gate.DelayUntil(Start.AddSeconds(30));
        fake.Advance(TimeSpan.FromSeconds(1));

        // Moved to the first cooldown's end, which shows as a second timer. The waiter takes its new slot on
        // the thread pool: a clock moved before that timer is armed would put its end past every later step.
        await UntilAsync(() => time.TimersCreated == 2);

        // Then the host asks for longer.
        gate.DelayUntil(Start.AddSeconds(90));
        fake.Advance(TimeSpan.FromSeconds(29));
        await UntilAsync(() => time.TimersCreated == 3);
        waiting.IsCompleted.Should().BeFalse();

        fake.Advance(TimeSpan.FromSeconds(60));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_public_rate_limits_keep_a_margin()
    {
        // MusicBrainz allows 1 request/s and AcoustID 3/s; network jitter can put two arrivals inside
        // one server-side second when sends are exactly at the limit.
        new MetadataOptions().MusicBrainzRequestInterval.Should().Be(TimeSpan.FromMilliseconds(1050));
        new Wondarr.Core.Metadata.AcoustId.AcoustIdOptions().RequestInterval.Should().Be(TimeSpan.FromMilliseconds(350));
        new Wondarr.Core.Metadata.AcoustId.AcoustIdOptions { RequestsPerSecond = 1 }.RequestInterval
            .Should().Be(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Waits, bounded and without sleeping, until a condition the fake clock's callbacks make true holds;
    /// a condition that never comes true fails the test instead of hanging it.
    /// </summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var guard = System.Diagnostics.Stopwatch.StartNew();

        while (!condition())
        {
            guard.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the waiters must make progress");
            await Task.Yield();
        }
    }

    /// <summary>Counts the timers the gate asks for, so a test can tell when a woken waiter has re-queued.</summary>
    private sealed class CountingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _timers;

        public int TimersCreated => Volatile.Read(ref _timers);

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted once the fake clock holds it: a test that moves the clock on seeing the count must not
            // move it before the timer's end is fixed.
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);

            return timer;
        }
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
