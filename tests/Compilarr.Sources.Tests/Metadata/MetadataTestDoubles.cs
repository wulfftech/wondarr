using System.Diagnostics;
using System.Net;
using System.Text;
using Compilarr.Core.Metadata;
using Microsoft.Extensions.Http;

namespace Compilarr.Sources.Tests.Metadata;

/// <summary>
/// Replaces the primary handler of every client the factory builds, so a composition test can drive
/// the real handler pipeline — retry and spacing included — without touching the network.
/// </summary>
internal sealed class StubHttpMessageHandlerFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly HttpMessageHandler _handler;

    public StubHttpMessageHandlerFilter(HttpMessageHandler handler) => _handler = handler;

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
        builder =>
        {
            next(builder);

            builder.PrimaryHandler = _handler;
        };
}

/// <summary>In-memory <see cref="IMetadataCache"/>, so contract tests never touch the database.</summary>
internal sealed class InMemoryMetadataCache : IMetadataCache
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <summary>How many writes the cache has seen; a test asserts on the negative-cache path with it.</summary>
    public int SetCount { get; private set; }

    public Task<string?> GetAsync(string provider, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(Compose(provider, key), out var payload) ? payload : null);

    public Task SetAsync(
        string provider,
        string key,
        string payload,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        SetCount++;
        _entries[Compose(provider, key)] = payload;
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    private static string Compose(string provider, string key) => $"{provider}|{key}";
}

/// <summary>The recorded MusicBrainz responses, copied next to the test binaries.</summary>
internal static class MusicBrainzFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "musicbrainz", name));

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent(Read("recording-unknown.json"), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Serves canned responses and records the request URIs it was given.</summary>
internal sealed class FixtureHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FixtureHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    public List<Uri> Requests { get; } = [];

    /// <summary>A handler that answers every request with one fixture.</summary>
    public static FixtureHttpMessageHandler Serving(string fixture) =>
        new(_ => MusicBrainzFixtures.Json(MusicBrainzFixtures.Read(fixture)));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);

        return Task.FromResult(_responder(request));
    }
}

/// <summary>
/// Answers with a scripted sequence of responses and stamps each request with the elapsed time,
/// so a test can assert on the gap between two attempts.
/// </summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ScriptedHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
        _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);

    public List<TimeSpan> Timestamps { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Timestamps.Add(_clock.Elapsed);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The scripted handler was called more often than it was scripted for.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}
