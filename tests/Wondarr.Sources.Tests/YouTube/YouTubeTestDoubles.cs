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

/// <summary>
/// An <see cref="IYtDlpBinary"/> that always names one binary, for tests of the code that runs yt-dlp rather than of
/// the choice of binary.
/// </summary>
internal sealed class FixedYtDlpBinary : IYtDlpBinary
{
    private readonly string _path;
    private readonly YtDlpBinarySource _source;
    private readonly YtDlpUpdateStatus _status;

    /// <summary>Initialises a new instance of the <see cref="FixedYtDlpBinary"/> class.</summary>
    /// <param name="path">The binary to name.</param>
    /// <param name="status">What the update status says; the default is "bundled, nothing to update".</param>
    /// <param name="source">Which kind of binary the path is.</param>
    public FixedYtDlpBinary(string path = "yt-dlp", YtDlpUpdateStatus? status = null, YtDlpBinarySource source = YtDlpBinarySource.Bundled)
    {
        _path = path;
        _source = source;
        _status = status ?? new YtDlpUpdateStatus(
            YtDlpBinarySource.Bundled,
            null,
            AutoUpdate: true,
            YtDlpOptions.StableChannel,
            UpdateActive: false,
            LastCheckedAt: null,
            LastCheckSucceeded: null,
            LastCheckMessage: null,
            LastSuccessAt: null,
            LatestVersion: null,
            DateTimeOffset.UnixEpoch,
            JsRuntimeWarning: null,
            FallbackReason: null);
    }

    /// <inheritdoc />
    public int Generation => 0;

    /// <inheritdoc />
    public Task<YtDlpResolution> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new YtDlpResolution(_path, _source, null, null, 0));

    /// <inheritdoc />
    public Task<YtDlpUpdateStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(_status);

    /// <inheritdoc />
    public void Invalidate()
    {
    }

    /// <inheritdoc />
    public Task<YtDlpUpdateRun> UpdateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new YtDlpUpdateRun(YtDlpUpdateRunKind.Skipped, "fixed"));
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

    /// <summary>Gets the <c>Retry-After</c> header every response carries, when set.</summary>
    public System.Net.Http.Headers.RetryConditionHeaderValue? RetryAfter { get; init; }

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

        response.Headers.RetryAfter = RetryAfter;

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
        TimeProvider? timeProvider = null,
        InnertubePause? pause = null) =>
        new(
            new HttpClient(handler),
            Monitor(options ?? new YouTubeOptions()),
            timeProvider ?? TimeProvider.System,
            pause ?? new InnertubePause());

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ytmusic", name));
}
