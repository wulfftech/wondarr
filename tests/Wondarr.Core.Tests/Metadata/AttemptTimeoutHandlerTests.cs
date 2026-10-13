using System.Net;
using Wondarr.Core.Metadata.Http;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Metadata;

/// <summary>
/// The attempt limit sits after the spacing gate: it bounds the network (headers and body) and never the
/// wait for a slot or a <c>Retry-After</c> cooldown.
/// </summary>
public sealed class AttemptTimeoutHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_times_out_at_the_limit()
    {
        var time = new FakeTimeProvider(Start);
        using var invoker = Invoker(time, new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() }));

        var send = Send(invoker);

        time.Advance(Limit - TimeSpan.FromSeconds(1));
        send.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));

        var thrown = await FluentActions.Awaiting(() => send.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<TaskCanceledException>();
        thrown.Which.Message.Should().Contain("musicbrainz did not answer within 30 s");
    }

    [Fact]
    public async Task A_network_that_never_answers_times_out_at_the_limit()
    {
        var time = new FakeTimeProvider(Start);
        using var invoker = Invoker(time, new Stub(_ => throw new InvalidOperationException("unused"), hang: true));

        var send = Send(invoker);

        time.Advance(Limit);

        await FluentActions.Awaiting(() => send.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_reported_as_a_timeout()
    {
        var time = new FakeTimeProvider(Start);
        using var invoker = Invoker(time, new Stub(_ => throw new InvalidOperationException("unused"), hang: true));
        using var cancellation = new CancellationTokenSource();

        var send = Send(invoker, cancellation.Token);
        await cancellation.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => send.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Message.Should().NotContain("did not answer");
    }

    [Fact]
    public async Task A_cooldown_longer_than_the_limit_is_waited_out_and_the_request_then_succeeds()
    {
        var time = new FakeTimeProvider(Start);
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(1), time);
        gate.DelayUntil(Start.AddSeconds(300));
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(time, stub, gate);

        var send = Send(invoker);

        // Ten times the limit passes at the gate; the attempt has not started, so nothing times out.
        time.Advance(TimeSpan.FromSeconds(299));
        send.IsCompleted.Should().BeFalse();
        stub.Requests.Should().Be(0);

        time.Advance(TimeSpan.FromSeconds(1));

        (await send.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_long_queue_at_the_gate_does_not_trip_the_limit_for_the_last_request()
    {
        var time = new FakeTimeProvider(Start);
        var gate = new RequestSpacingGate(TimeSpan.FromSeconds(5), time);
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(time, stub, gate);

        // Ten requests at 5 s spacing: the last one waits 45 s, more than the 30 s limit.
        var sends = Enumerable.Range(0, 10).Select(_ => Send(invoker)).ToArray();

        for (var step = 0; step < 9; step++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
        }

        var responses = await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(10));
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        stub.Requests.Should().Be(10);
    }

    private static Task<HttpResponseMessage> Send(HttpMessageInvoker invoker, CancellationToken cancellationToken = default) =>
        invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://musicbrainz.test/x"), cancellationToken);

    private static HttpMessageInvoker Invoker(FakeTimeProvider time, Stub stub, RequestSpacingGate? gate = null)
    {
        // Spacing first, then the limit, then the network: the order the real registration uses.
        var timeout = new AttemptTimeoutHandler(Limit, "musicbrainz", time) { InnerHandler = stub };
        var spacing = new RequestSpacingHandler(gate ?? new RequestSpacingGate(TimeSpan.Zero, time), "musicbrainz")
        {
            InnerHandler = timeout,
        };

        return new HttpMessageInvoker(spacing);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond, bool hang = false) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return respond(request);
        }
    }

    /// <summary>A body whose bytes never arrive.</summary>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.Infinite);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
