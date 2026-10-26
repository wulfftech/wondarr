using System.Globalization;
using System.Net;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Http;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Sources.Tests.Slskd;
using Wondarr.Testing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// MusicBrainz allows one request per second per IP address, retries included. These tests pin the
/// gate and the DI composition that puts it where retries have to pass it.
/// </summary>
public sealed class RequestSpacingTests
{
    private const string BohemianRhapsodyId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";

    [Fact]
    public async Task Gate_starts_three_concurrent_waiters_one_second_apart()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        var first = gate.WaitAsync(CancellationToken.None).AsTask();
        var second = gate.WaitAsync(CancellationToken.None).AsTask();
        var third = gate.WaitAsync(CancellationToken.None).AsTask();

        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeFalse();
        third.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await second;
        third.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await third;
    }

    [Fact]
    public async Task Gate_lets_a_waiter_that_arrives_late_through_immediately()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        await gate.WaitAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(5));

        var immediate = gate.WaitAsync(CancellationToken.None).AsTask();
        immediate.IsCompleted.Should().BeTrue();
        await immediate;
    }

    [Fact]
    public async Task Gate_throws_for_a_cancelled_waiter()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);

        // The first caller takes the immediate slot; the second has to wait for the next one.
        await gate.WaitAsync(CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var waiting = gate.WaitAsync(cancellation.Token).AsTask();
        waiting.IsCompleted.Should().BeFalse();

        await cancellation.CancelAsync();

        await FluentActions
            .Awaiting(() => waiting)
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pipeline_honours_retry_after_and_returns_the_retried_result()
    {
        // The retry waits out Retry-After on the clock the container hands the pipeline: give it one the
        // test moves, so the wait costs no real time.
        var time = new TrackingTimeProvider(Start);
        var retryAfter = TimeSpan.FromSeconds(1);

        var handler = new ScriptedHttpMessageHandler(
            time,
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter) },
            },
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(handler, time, TimeSpan.FromMilliseconds(10));

        var pending = provider.GetRequiredService<IMusicBrainzClient>().GetRecordingAsync(BohemianRhapsodyId);

        await ParkedAsync(time, pending, retryAfter, "the Retry-After wait");
        pending.IsCompleted.Should().BeFalse("the 503 is retried, not returned");

        // Retry-After is over, but the retry still has to pass the gate: the next slot opens 1.05 s after
        // the first request, so the retry waits out the 50 ms left of it.
        await time.AdvanceAndSettleAsync(retryAfter);

        var gateWait = MetadataOptions.MusicBrainzPublicInterval - retryAfter;
        await ParkedAsync(time, pending, gateWait, "the gate's wait");
        await time.AdvanceAndSettleAsync(gateWait, () => pending.IsCompleted);

        var recording = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        recording.Should().NotBeNull();
        recording!.Length.Should().Be(355106);

        handler.Timestamps.Should().HaveCount(2);
        (handler.Timestamps[1] - handler.Timestamps[0]).Should().BeGreaterThanOrEqualTo(retryAfter);
    }

    [Fact]
    public async Task Pipeline_puts_the_spacing_gate_inside_the_retry_loop()
    {
        // No Retry-After this time, and a retry delay of 10 ms: only the gate can explain a one
        // second gap, which is what makes this the ordering test.
        var time = new TrackingTimeProvider(Start);

        var handler = new ScriptedHttpMessageHandler(
            time,
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(handler, time, TimeSpan.FromMilliseconds(10));

        var pending = provider.GetRequiredService<IMusicBrainzClient>().GetRecordingAsync(BohemianRhapsodyId);

        // Half a second is well past the jittered 10 ms backoff and well short of the 1.05 s gate interval.
        var pastTheBackoff = TimeSpan.FromMilliseconds(500);

        // Parked on the backoff, or already at the gate: a jittered backoff under a millisecond is no wait at all
        // (Task.Delay counts whole milliseconds), and the retry goes straight on to the gate.
        await ParkedAsync(time, pending, MetadataOptions.MusicBrainzPublicInterval, "the retry backoff or the gate");
        await time.AdvanceAndSettleAsync(pastTheBackoff);

        // The backoff is over, so the retry is at the gate; if the gate sat outside the retry loop it
        // would be at the server by now.
        var gateWait = MetadataOptions.MusicBrainzPublicInterval - pastTheBackoff;
        await ParkedAsync(time, pending, gateWait, "the gate's wait");

        pending.IsCompleted.Should().BeFalse("the retry waits for its slot");
        handler.Timestamps.Should().HaveCount(1, "the retry is held at the gate until its slot");

        await time.AdvanceAndSettleAsync(gateWait, () => pending.IsCompleted);
        await pending.WaitAsync(TimeSpan.FromSeconds(30));

        handler.Timestamps.Should().HaveCount(2);
        (handler.Timestamps[1] - handler.Timestamps[0]).Should().BeGreaterThanOrEqualTo(MetadataOptions.MusicBrainzPublicInterval);
    }

    /// <summary>
    /// Waits, bounded, until the pipeline is parked on a fake-clock wait that ends within
    /// <paramref name="within"/>, or the call is over. The clock only moves once the wait is armed: a step
    /// taken before it is lost, and the call then waits for a wakeup that never comes.
    /// </summary>
    private static Task ParkedAsync(TrackingTimeProvider time, Task call, TimeSpan within, string what) =>
        SlskdHostHarness.AwaitAsync(() => call.IsCompleted || time.AnyTimerDueWithin(within), what, 30_000);

    private static ClockedServiceProvider BuildProvider(HttpMessageHandler primaryHandler, TrackingTimeProvider time, TimeSpan retryBaseDelay)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        // Before AddWondarrMetadata, which only adds the system clock when none is registered.
        services.AddSingleton<TimeProvider>(time);
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        // Only tests lower the retry delay; two seconds is the shipped value.
        services.Configure<MetadataOptions>(options => options.RetryBaseDelay = retryBaseDelay);

        // Swap the network out from under every client the real registration built, whatever the
        // factory named it.
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primaryHandler));

        return new ClockedServiceProvider(services.BuildServiceProvider(), time);
    }
}
