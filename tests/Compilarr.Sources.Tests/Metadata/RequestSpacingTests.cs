using System.Globalization;
using System.Net;
using Compilarr.Core.Metadata;
using Compilarr.Core.Metadata.Http;
using Compilarr.Core.Metadata.MusicBrainz;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Sources.Tests.Metadata;

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

    [Fact]
    public async Task Pipeline_honours_retry_after_and_returns_the_retried_result()
    {
        var handler = new ScriptedHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) },
            },
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(handler, TimeSpan.FromMilliseconds(10));

        var client = provider.GetRequiredService<IMusicBrainzClient>();
        var recording = await client.GetRecordingAsync(BohemianRhapsodyId);

        recording.Should().NotBeNull();
        recording!.Length.Should().Be(355106);

        handler.Timestamps.Should().HaveCount(2);
        (handler.Timestamps[1] - handler.Timestamps[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Pipeline_puts_the_spacing_gate_inside_the_retry_loop()
    {
        // No Retry-After this time, and a retry delay of 10 ms: only the gate can explain a one
        // second gap, which is what makes this the ordering test.
        var handler = new ScriptedHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read("recording-bohemian-rhapsody.json")));

        await using var provider = BuildProvider(handler, TimeSpan.FromMilliseconds(10));

        var client = provider.GetRequiredService<IMusicBrainzClient>();
        await client.GetRecordingAsync(BohemianRhapsodyId);

        handler.Timestamps.Should().HaveCount(2);
        (handler.Timestamps[1] - handler.Timestamps[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler primaryHandler, TimeSpan retryBaseDelay)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCompilarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        // Only tests lower the retry delay; two seconds is the shipped value.
        services.Configure<MetadataOptions>(options => options.RetryBaseDelay = retryBaseDelay);

        // Swap the network out from under every client the real registration built, whatever the
        // factory named it.
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primaryHandler));

        return services.BuildServiceProvider();
    }
}