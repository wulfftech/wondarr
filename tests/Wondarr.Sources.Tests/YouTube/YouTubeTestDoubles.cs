using Wondarr.Sources.YouTube;
using Microsoft.Extensions.Options;
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
