using System.Globalization;
using System.Text;
using Wondarr.Core.Configuration;
using Wondarr.Core.Searching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Owns the bundled slskd: writes its configuration, starts it, forwards its output into this app's
/// log, watches it through its own API, and restarts it when it dies or when a setting that slskd
/// only reads at start changes (<c>docs/architecture/DEPLOYMENT.md</c> §9.5).
/// </summary>
/// <remarks>
/// Registered after the migration hosted service, so the settings table (and with it the generated
/// slskd API key) exists by the time the first <c>slskd.yml</c> is rendered.
/// Secrets are never passed as arguments: the Soulseek password and the API key live only in
/// <c>slskd.yml</c>, which is written with owner-only permissions.
/// </remarks>
public sealed partial class SlskdHost : BackgroundService
{
    /// <summary>Logger category the child's own output is written to.</summary>
    public const string LogCategory = "slskd";

    /// <summary>Name of the rendered slskd configuration file.</summary>
    public const string ConfigFileName = "slskd.yml";

    /// <summary>How long slskd gets to answer its API before it counts as crashed.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How often slskd's API is polled while it is starting.</summary>
    public static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often slskd's API is polled once it is up.</summary>
    public static readonly TimeSpan RunningPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a settings change settles before it is applied.</summary>
    public static readonly TimeSpan SettingsDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long slskd gets to exit after SIGTERM, to log out and flush its transfer state, before its
    /// process tree is killed.
    /// </summary>
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a killed process gets to exit before the supervisor stops waiting. Together with
    /// <see cref="GracefulStopTimeout"/> this is the worst case of one stop: 7 s of the 8 s the host
    /// gives a shutdown (<c>docs/architecture/DEPLOYMENT.md</c>).
    /// </summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a leftover slskd from a crashed Wondarr gets to die before the new one starts anyway.</summary>
    public static readonly TimeSpan OrphanKillTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many polls in a row must fail, after slskd has been seen running, before it is restarted.
    /// One slow answer is not a dead slskd.
    /// </summary>
    public const int FailedPollLimit = 3;

    /// <summary>Name of the file that holds the pid of the slskd Wondarr started, inside the slskd directory.</summary>
    public const string PidFileName = "slskd.pid";

    /// <summary>Uptime after which the restart backoff starts from the beginning again.</summary>
    public static readonly TimeSpan BackoffResetUptime = TimeSpan.FromMinutes(5);

    /// <summary>Waits between restarts, in order; the last entry repeats.</summary>
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProcessLauncher _launcher;
    private readonly IProcessInspector _inspector;
    private readonly SlskdConfigRenderer _renderer;
    private readonly IOptionsMonitor<SoulseekOptions> _options;
    private readonly IOptionsMonitor<ServerOptions> _serverOptions;
    private readonly WondarrPaths _paths;
    private readonly SlskdStatus _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlskdHost> _logger;
    private readonly ILogger _slskdLogger;

    /// <summary>Serialises everything that starts, stops or replaces the child process.</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private readonly Lock _processGate = new();

    private ILaunchedProcess? _child;
    private long _generation;
    private SoulseekOptions? _runningOptions;

    /// <summary>
    /// Why the settings could not be written, while they still have to be re-applied. A failed write
    /// is not a crash: it is kept here so the supervisor retries, and so
    /// <see cref="PollAsync"/> does not wipe the reason with its next successful poll.
    /// </summary>
    private string? _settingsWriteError;
    private CancellationToken _stoppingToken;
    private int _backoffIndex;
    private int _settingsRevision;
    private int _modeSeen;

    // Completed by every settings change so an idle supervisor (external mode, or no binary) looks
    // again; replaced once it has been seen. Captured before the mode is read, so a change that lands
    // in between is never lost.
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initialises a new instance of the <see cref="SlskdHost"/> class.</summary>
    public SlskdHost(
        IServiceScopeFactory scopeFactory,
        IProcessLauncher launcher,
        IProcessInspector inspector,
        SlskdConfigRenderer renderer,
        IOptionsMonitor<SoulseekOptions> options,
        IOptionsMonitor<ServerOptions> serverOptions,
        WondarrPaths paths,
        SlskdStatus status,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _scopeFactory = scopeFactory;
        _launcher = launcher;
        _inspector = inspector;
        _renderer = renderer;
        _options = options;
        _serverOptions = serverOptions;
        _paths = paths;
        _status = status;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<SlskdHost>();
        _slskdLogger = loggerFactory.CreateLogger(LogCategory);
    }

    /// <summary>The rendered configuration file's path.</summary>
    public string ConfigPath => Path.Combine(_paths.SlskdDir, ConfigFileName);

    private string PidPath => Path.Combine(_paths.SlskdDir, PidFileName);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // Subscribed first and for the life of the service: the mode can change at any time, and a
        // supervisor that is idle (external mode, no binary) has to hear about it.
        _modeSeen = (int)_options.CurrentValue.Mode;
        using var subscription = _options.OnChange(OnSettingsChanged);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wake = Volatile.Read(ref _wake).Task;
                var options = _options.CurrentValue;

                if (options.Mode == SoulseekMode.External)
                {
                    // The external monitor owns the status in external mode; nothing here describes a
                    // process of ours. Wait for the mode to change back.
                    await IdleAsync(wake, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!File.Exists(options.BinaryPath))
                {
                    // Normal on a Windows dev box, where the Linux slskd binary is not present.
                    LogBinaryMissing(_logger, options.BinaryPath);
                    _status.Set(new SlskdStatusSnapshot(
                        SlskdState.BinaryMissing,
                        LastError: $"slskd binary not found at {options.BinaryPath}; the Soulseek source is unavailable",
                        LastCheckedAt: Now));

                    await IdleAsync(wake, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await SuperviseOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!CancellationRules.IsCallerCancellation(exception, stoppingToken))
                {
                    // Anything unforeseen (a launch that throws, a timeout nobody classified) is a
                    // crash like any other: report it, wait out the backoff, try again. It must never
                    // end the supervisor or the host.
                    LogSupervisorFailed(_logger, exception);
                    SetCrashed($"The slskd supervisor failed: {exception.Message}");

                    await Task.Delay(NextBackoff(), _timeProvider, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down; StopAsync stops the child.
        }
    }

    /// <summary>Waits for a settings change, then arms the next wait.</summary>
    private async Task IdleAsync(Task wake, CancellationToken cancellationToken)
    {
        await wake.WaitAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// One pass of supervision: make sure a child runs, watch it until it is replaced or dies, and
    /// wait out the restart backoff after a crash. Returns when the child was replaced (by a settings
    /// restart or a mode switch), so the caller looks at the mode again.
    /// </summary>
    private async Task SuperviseOnceAsync(CancellationToken stoppingToken)
    {
        var generation = await EnsureChildAsync(stoppingToken).ConfigureAwait(false);
        var startedAt = Now;

        if (_options.CurrentValue.Mode == SoulseekMode.External)
        {
            return;
        }

        if (generation is not null)
        {
            if (await MonitorAsync(generation.Value, stoppingToken).ConfigureAwait(false) == MonitorOutcome.Replaced)
            {
                // A settings change replaced the process, or the mode changed; the caller decides.
                return;
            }

            // The crash path takes the lifecycle gate like every other start/stop, and only stops
            // the process it was watching: an ApplySettingsAsync that ran between the crash and
            // here may already have started a replacement, which must not be killed.
            bool replacedMeanwhile;
            await _lifecycle.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                replacedMeanwhile = CurrentGeneration != generation.Value;
                if (!replacedMeanwhile)
                {
                    await StopChildAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (replacedMeanwhile)
            {
                return;
            }
        }

        // Either a child crashed, or none could be started because its directories could not
        // be prepared. Both are waited out and retried: an unwritable /data must not stop the
        // host (BackgroundServiceExceptionBehavior.StopHost), least of all in a restart loop.

        if (Now - startedAt >= BackoffResetUptime)
        {
            _backoffIndex = 0;
        }

        var delay = NextBackoff();
        LogRestartScheduled(_logger, delay.TotalSeconds);

        await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // An in-flight settings restart holds the gate; the shutdown waits for its turn rather than
        // leaving a second slskd behind.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await StopChildAsync(CancellationToken.None).ConfigureAwait(false);

            _status.Update(snapshot => new SlskdStatusSnapshot(
                SlskdState.Stopped,
                Version: snapshot.Version,
                SoulseekUsername: snapshot.SoulseekUsername,
                RestartCount: snapshot.RestartCount,
                LastError: snapshot.LastError,
                LastCheckedAt: Now));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Re-renders <c>slskd.yml</c> and restarts the child exactly once when
    /// <paramref name="next"/> changes something slskd only reads at start. Concurrent calls with
    /// the same change result in a single restart; a process that is not running is left alone.
    /// </summary>
    public async Task ApplySettingsAsync(SoulseekOptions next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (next.Mode == SoulseekMode.External)
        {
            // Switching to the user's own slskd: ours is stopped at once, through the same gate a
            // settings restart uses, and the external monitor takes over the status. No slskd.yml is
            // rendered for a slskd that is not ours.
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (next.Mode == SoulseekMode.External && Child is not null)
                {
                    LogModeSwitchedToExternal(_logger);
                    await StopChildAsync(cancellationToken).ConfigureAwait(false);
                }

                Volatile.Write(ref _runningOptions, null);
                Volatile.Write(ref _settingsWriteError, null);
            }
            finally
            {
                _lifecycle.Release();
            }

            return;
        }

        if (Volatile.Read(ref _runningOptions) is null && Child is null)
        {
            // Nothing of ours is running (an idle supervisor, or start-up not reached yet): the next
            // launch renders the new settings. Writing the file here as well would race the launch,
            // which writes it under the lifecycle gate.
            return;
        }

        var failure = await TryWriteConfigAsync(next, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            // A settings change that could not be written is not a crash. The state, the process id
            // and reachability all still describe the slskd that is really there — whether that is a
            // running child which keeps the configuration it was started with, or no child at all.
            // Only the reason is published; the pending change is re-applied by the supervisor.
            var reason = $"Settings not applied: {failure}";

            LogSettingsApplyFailed(_logger, failure);

            Volatile.Write(ref _settingsWriteError, reason);

            _status.Update(snapshot => snapshot with
            {
                LastError = reason,
                LastCheckedAt = Now,
            });

            return;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var running = Volatile.Read(ref _runningOptions);

            // Nothing of ours is running (external mode, a missing binary, or start-up not reached
            // yet): the next launch renders the new settings anyway.
            if (running is null || !SlskdRestartPolicy.RequiresRestart(running, next))
            {
                return;
            }

            LogRestartForSettings(_logger);

            // The replacement starts from the new settings, so whatever the log last said about the
            // previous account no longer applies.
            _status.Update(snapshot => snapshot with
            {
                State = SlskdState.Restarting,
                LoginProblem = SlskdLoginProblem.None,
                LoginProblemAt = null,
                LastCheckedAt = Now,
            });

            await StopChildAsync(cancellationToken).ConfigureAwait(false);
            await StartChildAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Makes sure a child process is running, and returns the generation that identifies it — or
    /// <see langword="null"/> when it could not be prepared at all (an unwritable <c>/data</c>, say).
    /// The check is repeated under the gate, because a settings restart may have started one while
    /// this caller was waiting for it.
    /// </summary>
    private async Task<long?> EnsureChildAsync(CancellationToken cancellationToken)
    {
        if (Child is not null)
        {
            return CurrentGeneration;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_options.CurrentValue.Mode == SoulseekMode.External)
            {
                // Switched to the user's own slskd while this caller waited for the gate.
                return null;
            }

            return Child is null
                ? await StartChildAsync(_options.CurrentValue, cancellationToken).ConfigureAwait(false)
                : CurrentGeneration;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Watches one child process until it is replaced or exits. Assumes the gate is free.
    /// <see cref="StartupTimeout"/> applies only until slskd has answered once; after that a restart
    /// needs <see cref="FailedPollLimit"/> failed polls in a row, and any answer resets the count.
    /// </summary>
    private async Task<MonitorOutcome> MonitorAsync(long generation, CancellationToken cancellationToken)
    {
        var startingDeadline = Now + StartupTimeout;
        var seenRunning = false;
        var failedPolls = 0;

        while (true)
        {
            var child = ChildFor(generation);
            if (child is null)
            {
                return MonitorOutcome.Replaced;
            }

            if (child.HasExited)
            {
                var exitCode = child.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
                LogExitedUnexpectedly(_logger, generation, exitCode);
                SetCrashed($"slskd exited unexpectedly (exit code {exitCode})");

                return MonitorOutcome.Crashed;
            }

            if (await PollAsync(cancellationToken).ConfigureAwait(false))
            {
                seenRunning = true;
                failedPolls = 0;

                // A settings change whose configuration could not be written is still pending: the
                // child is running the settings it was started with, so this cycle re-applies them.
                if (Volatile.Read(ref _settingsWriteError) is not null)
                {
                    await ApplySettingsAsync(_options.CurrentValue, cancellationToken).ConfigureAwait(false);

                    if (ChildFor(generation) is null)
                    {
                        return MonitorOutcome.Replaced;
                    }
                }

                await Task.Delay(RunningPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (ChildFor(generation) is null)
            {
                // Replaced or stopped while the poll was in flight (a mode switch, say): its failure
                // says nothing about the process now running.
                return MonitorOutcome.Replaced;
            }

            if (!seenRunning)
            {
                if (Now >= startingDeadline)
                {
                    SetCrashed("slskd did not answer its API within 60 seconds of starting");

                    return MonitorOutcome.Crashed;
                }

                await Task.Delay(StartupPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // It has answered before, so a failed poll is a hiccup until it repeats.
            failedPolls++;

            if (failedPolls >= FailedPollLimit)
            {
                SetCrashed($"slskd stopped answering its API ({FailedPollLimit} failed polls in a row)");

                return MonitorOutcome.Crashed;
            }

            var notAnswering = string.Create(
                CultureInfo.InvariantCulture,
                $"slskd not answering ({failedPolls}/{FailedPollLimit})");

            PublishIfBundled(snapshot => snapshot with { LastError = notAnswering });

            // The next look comes soon: three misses in a row are what decides, and waiting out the
            // normal interval between them would leave a dead slskd unattended for minutes.
            await Task.Delay(StartupPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads slskd's application state and publishes it, returning whether slskd answered. The
    /// client is resolved from a scope, because it uses the settings repository.
    /// </summary>
    private async Task<bool> PollAsync(CancellationToken cancellationToken)
    {
        var now = Now;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<ISlskdClient>();
            var state = await client.GetApplicationStateAsync(cancellationToken).ConfigureAwait(false);

            PublishIfBundled(snapshot => snapshot with
            {
                State = SlskdState.Running,
                IsReachable = true,
                IsLoggedIn = state.Server.IsLoggedIn,
                SoulseekUsername = string.IsNullOrWhiteSpace(state.User.Username) ? null : state.User.Username,
                Version = string.IsNullOrWhiteSpace(state.Version.Current) ? snapshot.Version : state.Version.Current,
                PendingRestart = state.PendingRestart,

                // What is actually shared right now — the settings page shows this next to the
                // "share my library" toggle, which is how the Phase 2 gate is checked.
                SharedDirectories = state.Shares.Directories,
                SharedFiles = state.Shares.Files,

                // A logged-in slskd means whatever the log said about the login no longer holds: a
                // kick or a rejected account that has since been corrected must not stay on the page.
                LoginProblem = state.Server.IsLoggedIn ? SlskdLoginProblem.None : snapshot.LoginProblem,
                LoginProblemAt = state.Server.IsLoggedIn ? null : snapshot.LoginProblemAt,

                // A settings change that has not been written yet is still the outstanding problem.
                LastError = Volatile.Read(ref _settingsWriteError),
                LastCheckedAt = now,
            });

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own 5 s timeout: a failed poll, never a shutdown.
            PublishIfBundled(snapshot => snapshot with
            {
                IsReachable = false,
                LastError = $"slskd did not answer within {SlskdClient.PollTimeout.TotalSeconds:0} s",
                LastCheckedAt = now,
            });

            return false;
        }
        catch (Exception exception) when (!CancellationRules.IsCallerCancellation(exception, cancellationToken))
        {
            PublishIfBundled(snapshot => snapshot with
            {
                IsReachable = false,
                LastError = exception.Message,
                LastCheckedAt = now,
            });

            return false;
        }
    }

    /// <summary>
    /// Publishes a snapshot, but only while bundled mode is the active one: after a switch to the
    /// user's own slskd the external monitor owns the status, and a poll that was in flight must not
    /// overwrite it.
    /// </summary>
    private void PublishIfBundled(Func<SlskdStatusSnapshot, SlskdStatusSnapshot> update) =>
        _status.Update(snapshot => _options.CurrentValue.Mode == SoulseekMode.Bundled ? update(snapshot) : snapshot);

    /// <summary>
    /// Writes the configuration and launches the child, or reports why it could not be prepared.
    /// </summary>
    /// <returns>The generation that identifies the new process, or <see langword="null"/> when
    /// nothing was started because the configuration could not be written.</returns>
    private async Task<long?> StartChildAsync(SoulseekOptions options, CancellationToken cancellationToken)
    {
        var failure = await TryWriteConfigAsync(options, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            // Reported as a crash so the caller waits out the backoff and tries again, rather than
            // letting the exception end the host.
            SetCrashed($"Cannot prepare slskd: {failure}");

            return null;
        }

        // A slskd left behind by a Wondarr that crashed would hold the ports and answer our polls
        // with the same API key; it goes before the replacement starts.
        await KillOrphanAsync(cancellationToken).ConfigureAwait(false);

        var child = _launcher.Launch(BuildRequest(options));

        WritePidFile(child.Id);

        long generation;
        lock (_processGate)
        {
            _generation++;
            generation = _generation;
            _child = child;
        }

        Volatile.Write(ref _runningOptions, options);

        _status.Update(snapshot => new SlskdStatusSnapshot(
            SlskdState.Starting,
            ProcessId: child.Id,
            Version: snapshot.Version,
            RestartCount: snapshot.RestartCount + 1,
            LastCheckedAt: Now));

        // Subscribed only now: this snapshot replaces the whole status, so a login line that arrived
        // between the launch and here would otherwise be recorded and then overwritten by it.
        child.OutputLine += OnChildOutput;

        LogStarted(_logger, child.Id);

        return generation;
    }

    /// <summary>
    /// Stops the child and forgets it: SIGTERM first, so slskd can log out and flush its state, and
    /// only when it has not exited within <see cref="GracefulStopTimeout"/> the whole process tree is
    /// killed (waiting up to <see cref="StopTimeout"/> for that).
    /// </summary>
    private async Task StopChildAsync(CancellationToken cancellationToken)
    {
        ILaunchedProcess? child;

        lock (_processGate)
        {
            child = _child;
            _child = null;
        }

        if (child is null)
        {
            return;
        }

        child.OutputLine -= OnChildOutput;

        try
        {
            var exited = child.HasExited;

            if (!exited)
            {
                child.RequestStop();
                exited = await WaitForExitAsync(child, GracefulStopTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (!exited)
            {
                LogTermTimedOut(_logger, GracefulStopTimeout.TotalSeconds);

                child.Kill(entireProcessTree: true);
                exited = await WaitForExitAsync(child, StopTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (exited)
            {
                // A clean stop. A process that would not die keeps its pid file, so the next start
                // looks for it.
                DeletePidFile();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogKillFailed(_logger, exception.Message);
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>
    /// Waits for <paramref name="child"/> to exit, for at most <paramref name="timeout"/>. A cancelled
    /// <paramref name="cancellationToken"/> ends the wait too (the caller then goes on to kill), so
    /// it reports "not exited" rather than throwing.
    /// </summary>
    private async Task<bool> WaitForExitAsync(ILaunchedProcess child, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var exit = child.WaitForExitAsync(wait.Token);
        var delay = Task.Delay(timeout, _timeProvider, wait.Token);

        try
        {
            await Task.WhenAny(exit, delay).ConfigureAwait(false);
        }
        finally
        {
            await wait.CancelAsync().ConfigureAwait(false);
        }

        return child.HasExited;
    }

    /// <summary>
    /// Kills a slskd that an earlier run of Wondarr left behind, found through the pid file. Only a
    /// process whose executable is called slskd is touched: a pid that has since been reused by
    /// something else is left alone. Failures are logged, never thrown — the new slskd is started either way.
    /// </summary>
    private async Task KillOrphanAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(PidPath))
            {
                return;
            }

            var text = await File.ReadAllTextAsync(PidPath, cancellationToken).ConfigureAwait(false);

            if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                && processId > 0
                && processId != Environment.ProcessId
                && _inspector.GetExecutable(processId) is { } executable)
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "slskd", StringComparison.OrdinalIgnoreCase))
                {
                    LogPidNotSlskd(_logger, processId);
                }
                else
                {
                    LogKillingOrphan(_logger, processId);

                    if (!await _inspector.KillTreeAsync(processId, OrphanKillTimeout, cancellationToken).ConfigureAwait(false))
                    {
                        LogOrphanSurvived(_logger, processId);

                        // Kept: the next start tries again.
                        return;
                    }
                }
            }

            // Stale (no such process), not ours to kill, or dead now.
            DeletePidFile();
        }
        catch (Exception exception) when (!CancellationRules.IsCallerCancellation(exception, cancellationToken))
        {
            LogOrphanCheckFailed(_logger, exception.Message);
        }
    }

    private void WritePidFile(int processId)
    {
        try
        {
            File.WriteAllText(PidPath, processId.ToString(CultureInfo.InvariantCulture), Utf8WithoutBom);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPidWriteFailed(_logger, exception.Message);
        }
    }

    private void DeletePidFile()
    {
        try
        {
            File.Delete(PidPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPidWriteFailed(_logger, exception.Message);
        }
    }

    /// <summary>
    /// Where slskd calls Wondarr back when a file finishes. slskd runs on this machine, so loopback
    /// and Wondarr's own port are always right — even when the user reaches Wondarr through a proxy —
    /// and the URL base is already normalised to <c>""</c> or <c>/base</c>.
    /// </summary>
    private string WebhookUrl()
    {
        var server = _serverOptions.CurrentValue;

        return $"http://127.0.0.1:{server.Port}{server.UrlBase}/api/v1/slskd/webhook";
    }

    private ProcessLaunchRequest BuildRequest(SoulseekOptions options) => new(
        options.BinaryPath,
        [],

        // slskd resolves its content root relative to the binary, so run it from there.
        Path.GetDirectoryName(Path.GetFullPath(options.BinaryPath)) ?? _paths.SlskdDir,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SLSKD_APP_DIR"] = _paths.SlskdDir,
            ["SLSKD_CONFIG"] = ConfigPath,
            ["SLSKD_HEADLESS"] = "true",
            ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(_paths.SlskdDir, ".net"),
        });

    /// <summary>
    /// Prepares slskd's directories and renders and writes <c>slskd.yml</c>. The document is written
    /// to a temporary file and moved over the target, so slskd's file watcher never sees a
    /// half-written file.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> on success, or the reason the directories or the file could not be
    /// written. Storage failures are returned, never thrown: an unwritable volume must not stop the
    /// host, and the message ends up in the status's <c>LastError</c>.
    /// </returns>
    private async Task<string?> TryWriteConfigAsync(SoulseekOptions options, CancellationToken cancellationToken)
    {
        var failure = TryPrepareDirectories(options);

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            SlskdRuntimeSecrets secrets;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<SlskdSecretsStore>();
                secrets = await store.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            }

            var yaml = _renderer.Render(options, secrets, WebhookUrl());

            // A unique name per write: two concurrent settings changes must not write the same
            // temporary file.
            var temporary = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";

            await File.WriteAllTextAsync(temporary, yaml, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);

            // The file holds the Soulseek password and the API key: readable by the app user only.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, ConfigPath, overwrite: true);

            // Whatever was pending has just been written.
            Volatile.Write(ref _settingsWriteError, null);

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not only storage errors: the secrets store, the renderer and the database behind them
            // can fail too, and none of it may reach the host's caller — an exception out of the
            // background service stops the whole app (BackgroundServiceExceptionBehavior.StopHost).
            LogPrepareFailed(_logger, exception.Message);

            return exception.Message;
        }
    }

    /// <summary>
    /// Creates the directories slskd insists on. slskd refuses to start ("Invalid configuration")
    /// when one is missing, which is the normal state of a fresh <c>/data</c> volume — found by the
    /// Phase 0 image smoke test.
    /// </summary>
    /// <returns><see langword="null"/> when they all exist, else the reason they do not.</returns>
    private string? TryPrepareDirectories(SoulseekOptions options)
    {
        try
        {
            Directory.CreateDirectory(_paths.SlskdDir);
            Directory.CreateDirectory(options.DownloadsDir);
            Directory.CreateDirectory(options.IncompleteDir);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A malformed setting throws ArgumentException or NotSupportedException rather than
            // UnauthorizedAccessException; none of them may stop the host.
            // The .NET message names the path, e.g. "Access to the path '/data/downloads' is denied."
            LogPrepareFailed(_logger, exception.Message);

            return exception.Message;
        }

        if (options.ShareLibrary)
        {
            foreach (var folder in options.SharedFolders)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Sharing one folder less is not worth refusing to start over — and a folder
                    // setting that is not a usable path at all (empty, or a bad drive) is only one
                    // folder less, too.
                    LogSharedFolderFailed(_logger, folder, exception.Message);
                }
            }
        }

        return null;
    }

    private void OnChildOutput(string line)
    {
        LogChildOutput(_slskdLogger, line);

        // slskd's API shows a rejected account and a duplicate-login kick as a plain "logged out",
        // so the only place they can be seen is its own log, which arrives here.
        if (SlskdLogWatcher.Classify(line) is not { } signal)
        {
            return;
        }

        _status.Update(snapshot => signal switch
        {
            SlskdLogSignal.LoggedIn => snapshot with
            {
                LoginProblem = SlskdLoginProblem.None,
                LoginProblemAt = null,
            },
            SlskdLogSignal.InvalidCredentials => snapshot with
            {
                LoginProblem = SlskdLoginProblem.InvalidCredentials,
                LoginProblemAt = Now,
            },
            _ => snapshot with
            {
                LoginProblem = SlskdLoginProblem.DuplicateLogin,
                LoginProblemAt = Now,
            },
        });
    }

    private void OnSettingsChanged(SoulseekOptions next)
    {
        var revision = Interlocked.Increment(ref _settingsRevision);

        // A new mode is applied at once; everything else settles first, so a burst of edits restarts
        // slskd once.
        var modeChanged = Interlocked.Exchange(ref _modeSeen, (int)next.Mode) != (int)next.Mode;

        // Wakes an idle supervisor (external -> bundled); a busy one ignores it.
        Volatile.Read(ref _wake).TrySetResult();

        _ = ApplyAfterDebounceAsync(next, revision, modeChanged);
    }

    private async Task ApplyAfterDebounceAsync(SoulseekOptions next, int revision, bool immediately)
    {
        try
        {
            if (!immediately)
            {
                await Task.Delay(SettingsDebounce, _timeProvider, _stoppingToken).ConfigureAwait(false);
            }

            // A newer change arrived while this one was settling; that one applies instead.
            if (Volatile.Read(ref _settingsRevision) != revision)
            {
                return;
            }

            await ApplySettingsAsync(next, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSettingsApplyFailed(_logger, exception.Message);
        }
    }

    private void SetCrashed(string reason) =>
        _status.Update(snapshot => new SlskdStatusSnapshot(
            SlskdState.Crashed,
            Version: snapshot.Version,
            SoulseekUsername: snapshot.SoulseekUsername,
            PendingRestart: snapshot.PendingRestart,
            RestartCount: snapshot.RestartCount,
            LastError: reason,
            LastCheckedAt: Now,
            LoginProblem: snapshot.LoginProblem,
            LoginProblemAt: snapshot.LoginProblemAt));

    private TimeSpan NextBackoff()
    {
        var delay = RestartBackoff[_backoffIndex];

        if (_backoffIndex < RestartBackoff.Length - 1)
        {
            _backoffIndex++;
        }

        return delay;
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private long CurrentGeneration
    {
        get
        {
            lock (_processGate)
            {
                return _generation;
            }
        }
    }

    private ILaunchedProcess? Child
    {
        get
        {
            lock (_processGate)
            {
                return _child;
            }
        }
    }

    /// <summary>The child for <paramref name="generation"/>, or <see langword="null"/> when that
    /// process has been replaced or stopped.</summary>
    private ILaunchedProcess? ChildFor(long generation)
    {
        lock (_processGate)
        {
            return _generation == generation ? _child : null;
        }
    }

    // Every message is generated against an explicit logger: the supervisor logs to two categories
    // (its own and the child's), so there is no single instance logger to bind to.

    [LoggerMessage(Level = LogLevel.Warning, Message = "slskd binary not found at {BinaryPath}; the Soulseek source is unavailable")]
    private static partial void LogBinaryMissing(ILogger logger, string binaryPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started slskd (pid {ProcessId})")]
    private static partial void LogStarted(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "slskd (generation {Generation}) exited unexpectedly with code {ExitCode}")]
    private static partial void LogExitedUnexpectedly(ILogger logger, long generation, string exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarting slskd in {DelaySeconds} seconds")]
    private static partial void LogRestartScheduled(ILogger logger, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "A setting that slskd reads at start changed; restarting it")]
    private static partial void LogRestartForSettings(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not apply the Soulseek settings: {Reason}")]
    private static partial void LogSettingsApplyFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Soulseek mode switched to external; stopping the bundled slskd")]
    private static partial void LogModeSwitchedToExternal(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The slskd supervisor hit an unexpected error")]
    private static partial void LogSupervisorFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "slskd did not exit within {Seconds} s of SIGTERM; killing its process tree")]
    private static partial void LogTermTimedOut(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing slskd (pid {ProcessId}) left over from an earlier run of Wondarr")]
    private static partial void LogKillingOrphan(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The leftover slskd (pid {ProcessId}) did not exit; starting a new one anyway")]
    private static partial void LogOrphanSurvived(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The slskd pid file names process {ProcessId}, which is not slskd; leaving it alone")]
    private static partial void LogPidNotSlskd(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check for a leftover slskd: {Reason}")]
    private static partial void LogOrphanCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the slskd pid file: {Reason}")]
    private static partial void LogPidWriteFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not kill the slskd process: {Reason}")]
    private static partial void LogKillFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot prepare slskd: {Reason}")]
    private static partial void LogPrepareFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the shared folder {Folder}, so it is not shared: {Reason}")]
    private static partial void LogSharedFolderFailed(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogChildOutput(ILogger logger, string line);

    private enum MonitorOutcome
    {
        /// <summary>The process was replaced by a settings restart.</summary>
        Replaced,

        /// <summary>The process exited, or never became reachable.</summary>
        Crashed,
    }
}
