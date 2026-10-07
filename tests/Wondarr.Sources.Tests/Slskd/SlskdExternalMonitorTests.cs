using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.HealthCheck;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The external monitor: it polls the user's slskd and publishes the truth about it, and it does
/// nothing in bundled mode. The API key never appears in a message or a log line.
/// </summary>
public class SlskdExternalMonitorTests
{
    private const string ExternalKey = "external-key-0123456789abcdef";

    private static SoulseekOptions ExternalOptions() => new()
    {
        Mode = SoulseekMode.External,
        External = new SoulseekExternalOptions
        {
            Url = "http://slskd.example:5030",
            ApiKey = ExternalKey,
        },
    };

    private static readonly SlskdApplicationState LoggedIn = new()
    {
        Version = new SlskdVersion { Current = "0.26.0.0" },
        Server = new SlskdServer { IsLoggedIn = true, IsConnected = true },
        User = new SlskdUser { Username = "wondarr-external" },
        Shares = new SlskdShares { Directories = 12, Files = 340 },
        PendingRestart = true,
    };

    [Fact]
    public async Task Publishes_a_logged_in_external_snapshot()
    {
        var (monitor, status, client, _, _) = Monitor(() => Task.FromResult(LoggedIn));

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => status.Current.IsReachable);
        await monitor.StopAsync(CancellationToken.None);

        var snapshot = status.Current;
        snapshot.State.Should().Be(SlskdState.External);
        snapshot.IsReachable.Should().BeTrue();
        snapshot.IsLoggedIn.Should().BeTrue();
        snapshot.Version.Should().Be("0.26.0.0");
        snapshot.SoulseekUsername.Should().Be("wondarr-external");
        snapshot.PendingRestart.Should().BeTrue();
        snapshot.SharedDirectories.Should().Be(12);
        snapshot.SharedFiles.Should().Be(340);
        snapshot.LastError.Should().BeNull();
        snapshot.LastCheckedAt.Should().NotBeNull();

        await client.Received(1).GetApplicationStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_key_is_reported_without_the_key()
    {
        var (monitor, status, _, _, _) = Monitor(Refused);

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => status.Current.LastError == "slskd refused the API key");
        await monitor.StopAsync(CancellationToken.None);

        var snapshot = status.Current;
        snapshot.State.Should().Be(SlskdState.External);
        snapshot.IsReachable.Should().BeFalse();
        snapshot.LastError.Should().Be("slskd refused the API key");
        snapshot.LastError.Should().NotContain(ExternalKey);
    }

    [Fact]
    public async Task A_connection_failure_names_the_host_only()
    {
        var (monitor, status, _, _, _) = Monitor(Unreachable);

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => status.Current.LastError?.EndsWith("is not reachable", StringComparison.Ordinal) == true);
        await monitor.StopAsync(CancellationToken.None);

        var snapshot = status.Current;
        snapshot.IsReachable.Should().BeFalse();
        snapshot.LastError.Should().Be("slskd at slskd.example is not reachable");

        // The host only: never the full URL (it can carry a path) and never the key.
        snapshot.LastError.Should().NotContain("http://");
        snapshot.LastError.Should().NotContain(ExternalKey);
    }

    [Fact]
    public async Task The_key_never_reaches_a_log_line()
    {
        var (monitor, _, _, logger, _) = Monitor(Unreachable);

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => logger.Entries.Count > 0);
        await monitor.StopAsync(CancellationToken.None);

        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().NotContain(
            entry => entry.Message.Contains(ExternalKey),
            "the API key is a secret");
    }

    [Fact]
    public async Task Bundled_mode_never_polls()
    {
        var (monitor, _, client, _, time) = Monitor(() => Task.FromResult(LoggedIn), new SoulseekOptions());

        await monitor.StartAsync(CancellationToken.None);

        // Long enough for several poll intervals to pass.
        time.Advance(TimeSpan.FromMinutes(2));

        await monitor.StopAsync(CancellationToken.None);

        await client.DidNotReceive().GetApplicationStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_options_change_polls_immediately()
    {
        var (monitor, status, client, _, time) = Monitor(() => Task.FromResult(LoggedIn));

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => status.Current.IsReachable);

        // The wait between polls is 30 s of fake time; without a change, nothing more is polled.
        time.Advance(TimeSpan.FromSeconds(10));
        await client.Received(1).GetApplicationStateAsync(Arg.Any<CancellationToken>());

        // A settings change cancels the wait, so the next poll happens without the clock moving.
        var firstCheckedAt = status.Current.LastCheckedAt;
        Options.Set(ExternalOptions());

        await UntilAsync(() => status.Current.LastCheckedAt > firstCheckedAt);
        await client.Received(2).GetApplicationStateAsync(Arg.Any<CancellationToken>());

        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Before_the_first_answer_it_says_it_is_checking()
    {
        var never = new TaskCompletionSource<SlskdApplicationState>();
        var (monitor, status, _, _, _) = Monitor(() => never.Task);

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => status.Current.State == SlskdState.External);

        status.Current.LastError.Should().Be("checking slskd at slskd.example", "not the bundled 'not configured' state");
        status.Current.IsReachable.Should().BeFalse();

        never.SetResult(LoggedIn);
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_change_that_lands_during_a_poll_is_not_lost()
    {
        // The first poll hangs until released; the settings change arrives while it runs. Before the
        // fix the change cancelled a token source that was then thrown away, and the next poll waited
        // the full 30 s.
        var firstPoll = new TaskCompletionSource<SlskdApplicationState>();
        var calls = 0;
        var (monitor, status, client, _, _) = Monitor(() => Interlocked.Increment(ref calls) == 1
            ? firstPoll.Task
            : Task.FromResult(LoggedIn));

        await monitor.StartAsync(CancellationToken.None);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        Options.Set(ExternalOptions());
        firstPoll.SetResult(LoggedIn);

        // No fake time passes: only the remembered change can start the second poll.
        await UntilAsync(() => Volatile.Read(ref calls) >= 2, "the change to trigger a second poll");
        await monitor.StopAsync(CancellationToken.None);
    }

    private FakeOptionsMonitor<SoulseekOptions> Options { get; } = new(new SoulseekOptions());

    private (SlskdExternalMonitor Monitor, SlskdStatus Status, ISlskdClient Client, CapturingLogger Logger, FakeTimeProvider Time) Monitor(
        Func<Task<SlskdApplicationState>> response,
        SoulseekOptions? options = null)
    {
        options ??= ExternalOptions();
        var client = Substitute.For<ISlskdClient>();
        client.GetApplicationStateAsync(Arg.Any<CancellationToken>()).Returns(_ => response());

        var status = new SlskdStatus();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();

        if (options is not null)
        {
            Options.Set(options);
        }

        var services = new ServiceCollection();
        services.AddSingleton(client);

        var monitor = new SlskdExternalMonitor(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options,
            status,
            time,
            logger);

        return (monitor, status, client, logger, time);
    }

    private static Task<SlskdApplicationState> Refused() =>
        Task.FromException<SlskdApplicationState>(
            new HttpRequestException("slskd said no", null, HttpStatusCode.Unauthorized));

    private static Task<SlskdApplicationState> Unreachable() =>
        Task.FromException<SlskdApplicationState>(new HttpRequestException("No such host is known."));

    private static async Task UntilAsync(Func<bool> condition, string because = "the monitor to poll", int timeoutMs = 10_000)
    {
        using var cancellation = new CancellationTokenSource(timeoutMs);

        while (!condition())
        {
            await Task.Delay(10, CancellationToken.None);
            cancellation.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Captures every formatted log line, so a test can assert a secret never reached one.</summary>
    private sealed class CapturingLogger : ILogger<SlskdExternalMonitor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
