using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wondarr.Sources.YouTube;
using Wondarr.Core.Media;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>One call an <see cref="IProcessRunner"/> double was asked to make.</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments, one per item.</param>
/// <param name="Timeout">The timeout the caller passed.</param>
internal sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);

/// <summary>
/// Stands in for yt-dlp: it records every call and answers with the queued outputs, in order. An
/// answer can be a delayed task, so tests can watch one download hold the runner's gate.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<CancellationToken, Task<ProcessResult>>> _answers = new();

    /// <summary>Gets every call made, in order.</summary>
    public List<ProcessCall> Calls { get; } = [];

    /// <summary>Queues the answer for the next call.</summary>
    public FakeProcessRunner Enqueue(ProcessResult result)
    {
        _answers.Enqueue(_ => Task.FromResult(result));

        return this;
    }

    /// <summary>Queues a successful answer for the next call.</summary>
    public FakeProcessRunner Enqueue(string standardOutput) =>
        Enqueue(new ProcessResult(0, standardOutput, string.Empty, TimedOut: false));

    /// <summary>Queues a failed answer for the next call.</summary>
    public FakeProcessRunner Enqueue(int exitCode, string standardError) =>
        Enqueue(new ProcessResult(exitCode, string.Empty, standardError, TimedOut: false));

    /// <summary>Queues a delayed answer for the next call, for tests that watch the gate.</summary>
    public FakeProcessRunner Enqueue(Func<CancellationToken, Task<ProcessResult>> answer)
    {
        _answers.Enqueue(answer);

        return this;
    }

    /// <summary>Queues a missing executable for the next call.</summary>
    public FakeProcessRunner EnqueueMissing(string tool)
    {
        _answers.Enqueue(_ => throw new MediaToolMissingException(tool));

        return this;
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var call = new ProcessCall(fileName, [.. arguments], timeout);
        Calls.Add(call);

        if (_answers.Count == 0)
        {
            throw new InvalidOperationException($"Nothing was queued for '{fileName}'.");
        }

        return _answers.Dequeue()(cancellationToken);
    }
}

/// <summary>A read-only <see cref="IOptionsMonitor{T}"/> over one instance, for tests that build a service by hand.</summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    /// <summary>Initialises a new instance of the <see cref="TestOptionsMonitor{T}"/> class.</summary>
    /// <param name="value">The value every caller sees.</param>
    public TestOptionsMonitor(T value) => CurrentValue = value;

    /// <inheritdoc />
    public T CurrentValue { get; }

    /// <inheritdoc />
    public T Get(string? name) => CurrentValue;

    /// <inheritdoc />
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Reads the canned yt-dlp fixtures copied next to the test binaries.</summary>
internal static class YtDlpFixtures
{
    /// <summary>Reads one fixture file under <c>tests/fixtures/ytdlp</c>.</summary>
    /// <param name="name">The file name, for example <c>bot-check.stderr</c>.</param>
    /// <returns>The file's contents.</returns>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ytdlp", name));
}

/// <summary>Serves a canned response, recording the requests it was given (the slskd double, for InnerTube).</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// Each request's body, read while the request is still alive: the client disposes the
    /// <see cref="HttpRequestMessage"/> (and its content) as soon as it has the response.
    /// </summary>
    public List<string> Bodies { get; } = [];

    public static StubHttpMessageHandler Ok(string body) => new(HttpStatusCode.OK, body);

    public static StubHttpMessageHandler Status(HttpStatusCode status, string body) => new(status, body);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());

        var response = new HttpResponseMessage(_status)
        {
            RequestMessage = request,
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }
}

/// <summary>Shared builders for the YouTube tests.</summary>
internal static class YouTubeTestData
{
    /// <summary>The date the fixtures were recorded, so the computed client version is deterministic.</summary>
    public static readonly DateTimeOffset Recorded = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    public static IOptionsMonitor<YouTubeOptions> Monitor(YouTubeOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<YouTubeOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    public static InnertubeClient Client(
        StubHttpMessageHandler handler,
        YouTubeOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            new HttpClient(handler),
            Monitor(options ?? new YouTubeOptions()),
            timeProvider ?? TimeProvider.System);

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ytmusic", name));
}
