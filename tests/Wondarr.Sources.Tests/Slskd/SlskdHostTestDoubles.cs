using Wondarr.Sources.Slskd;
using Microsoft.Extensions.Logging;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// A process the supervisor can start, observe and kill without anything being started for real.
/// The test decides when it exits and what it prints.
/// </summary>
internal sealed class FakeProcess : ILaunchedProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; init; }

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    /// <summary>How many times the supervisor asked for this process to be killed.</summary>
    public int KillCount { get; private set; }

    /// <summary>Whether the last kill was asked to take the whole process tree with it.</summary>
    public bool LastKillWasTreeWide { get; private set; }

    /// <summary>How many times the supervisor asked this process to stop (SIGTERM).</summary>
    public int StopRequestCount { get; private set; }

    /// <summary>When set, the process ignores SIGTERM, as a hung slskd would; only a kill ends it.</summary>
    public bool IgnoresStopRequest { get; set; }

    public event Action<string>? OutputLine;

    public void RequestStop()
    {
        StopRequestCount++;

        if (!IgnoresStopRequest)
        {
            ExitWith(143);
        }
    }

    /// <summary>Prints a line, as slskd would.</summary>
    public void Emit(string line) => OutputLine?.Invoke(line);

    /// <summary>Exits on its own, as a crashed slskd would.</summary>
    public void ExitWith(int code)
    {
        HasExited = true;
        ExitCode = code;
        _exit.TrySetResult();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Kill(bool entireProcessTree)
    {
        KillCount++;
        LastKillWasTreeWide = entireProcessTree;

        ExitWith(137);
    }

    public void Dispose()
    {
    }
}

/// <summary>One call to <see cref="FakeProcessLauncher.Launch"/>.</summary>
/// <param name="Request">What the supervisor asked for.</param>
/// <param name="Process">The process it was given.</param>
/// <param name="At">The (fake) time the launch happened.</param>
internal sealed record LaunchRecord(ProcessLaunchRequest Request, FakeProcess Process, DateTimeOffset At);

/// <summary>Hands out <see cref="FakeProcess"/>es and remembers what it was asked to start.</summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly TimeProvider _timeProvider;

    private int _nextId = 1000;

    public FakeProcessLauncher(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public List<LaunchRecord> Launches { get; } = [];

    /// <summary>How many processes the supervisor has started.</summary>
    public int Count => Launches.Count;

    /// <summary>The most recently started process.</summary>
    public FakeProcess Latest => Launches[^1].Process;

    public ILaunchedProcess Launch(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var process = new FakeProcess { Id = ++_nextId };
        Launches.Add(new LaunchRecord(request, process, _timeProvider.GetUtcNow()));

        return process;
    }
}

/// <summary>Processes that exist outside the supervisor's own children: what a stale pid file can point at.</summary>
internal sealed class FakeProcessInspector : IProcessInspector
{
    /// <summary>The live processes by pid, with the executable each runs.</summary>
    public Dictionary<int, string> Processes { get; } = [];

    /// <summary>The pids that were killed.</summary>
    public List<int> Killed { get; } = [];

    public string? GetExecutable(int processId) => Processes.TryGetValue(processId, out var executable) ? executable : null;

    public Task<bool> KillTreeAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Killed.Add(processId);
        Processes.Remove(processId);

        return Task.FromResult(true);
    }
}

/// <summary>One log entry, whatever category and level produced it.</summary>
/// <param name="Category">The <see cref="ILogger"/> category.</param>
/// <param name="Level">The level it was written at.</param>
/// <param name="Message">The rendered message.</param>
internal sealed record LogRecord(string Category, LogLevel Level, string Message);

/// <summary>Keeps every log entry, so a test can assert on the child's output lines.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();

    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>The entries written to <paramref name="category"/>.</summary>
    public IReadOnlyList<LogRecord> For(string category) =>
        [.. Records.Where(record => string.Equals(record.Category, category, StringComparison.Ordinal))];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerProvider _provider;
        private readonly string _category;

        internal RecordingLogger(RecordingLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _provider.Add(new LogRecord(_category, logLevel, formatter(state, exception)));
        }
    }
}
