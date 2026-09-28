using Microsoft.Extensions.Options;
using Wondarr.Core.Media;

namespace Wondarr.Core.Tests.Media;

/// <summary>One call an <see cref="IProcessRunner"/> double was asked to make.</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments, one per item.</param>
/// <param name="Timeout">The timeout the caller passed.</param>
internal sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);

/// <summary>
/// Stands in for the media tools: it records every call and answers with the recorded outputs the
/// test queued, in order.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<ProcessResult>> _answers = new();

    /// <summary>Gets every call made, in order.</summary>
    public List<ProcessCall> Calls { get; } = [];

    /// <summary>Gets or sets a hook that runs before each queued answer, for tests that watch the file system.</summary>
    public Action<ProcessCall>? OnCall { get; set; }

    /// <summary>Queues the answer for the next call.</summary>
    public FakeProcessRunner Enqueue(ProcessResult result)
    {
        _answers.Enqueue(() => result);

        return this;
    }

    /// <summary>Queues a successful answer for the next call.</summary>
    public FakeProcessRunner Enqueue(string standardOutput) =>
        Enqueue(new ProcessResult(0, standardOutput, string.Empty, TimedOut: false));

    /// <summary>Queues a failed answer for the next call.</summary>
    public FakeProcessRunner Enqueue(int exitCode, string standardError) =>
        Enqueue(new ProcessResult(exitCode, string.Empty, standardError, TimedOut: false));

    /// <summary>Queues a missing executable for the next call.</summary>
    public FakeProcessRunner EnqueueMissing(string tool)
    {
        _answers.Enqueue(() => throw new MediaToolMissingException(tool));

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
        OnCall?.Invoke(call);

        if (_answers.Count == 0)
        {
            throw new InvalidOperationException($"Nothing was queued for '{fileName}'.");
        }

        return Task.FromResult(_answers.Dequeue()());
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