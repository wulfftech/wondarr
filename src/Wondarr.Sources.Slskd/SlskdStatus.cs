namespace Wondarr.Sources.Slskd;

/// <summary>Where the bundled slskd is in its lifecycle.</summary>
public enum SlskdState
{
    /// <summary>The supervisor has not run yet, so nothing is known about slskd.</summary>
    NotConfigured,

    /// <summary>External mode: Wondarr does not own the process.</summary>
    Disabled,

    /// <summary>The slskd binary is not where the settings say it should be.</summary>
    BinaryMissing,

    /// <summary>The process is running but has not answered its own API yet.</summary>
    Starting,

    /// <summary>The process is running and answering.</summary>
    Running,

    /// <summary>The process is being stopped and started again after a settings change.</summary>
    Restarting,

    /// <summary>The process exited on its own, or never became reachable; a restart is pending.</summary>
    Crashed,

    /// <summary>The supervisor stopped the process because the host is shutting down.</summary>
    Stopped,
}

/// <summary>An immutable snapshot of what the supervisor last observed about the bundled slskd.</summary>
/// <param name="State">Lifecycle state.</param>
/// <param name="ProcessId">Operating-system process id, when a process is running.</param>
/// <param name="Version">Version slskd reports, once it has answered.</param>
/// <param name="IsReachable">Whether slskd answered its own API the last time it was polled.</param>
/// <param name="IsLoggedIn">Whether slskd reports a logged-in Soulseek session.</param>
/// <param name="SoulseekUsername">The Soulseek username slskd reports, if any.</param>
/// <param name="PendingRestart">Whether slskd is waiting to be restarted.</param>
/// <param name="RestartCount">How many times the supervisor has (re)started the process.</param>
/// <param name="LastError">What went wrong, if anything — never a secret.</param>
/// <param name="LastCheckedAt">When the state was last updated.</param>
public sealed record SlskdStatusSnapshot(
    SlskdState State,
    int? ProcessId = null,
    string? Version = null,
    bool IsReachable = false,
    bool IsLoggedIn = false,
    string? SoulseekUsername = null,
    bool PendingRestart = false,
    int RestartCount = 0,
    string? LastError = null,
    DateTimeOffset? LastCheckedAt = null)
{
    /// <summary>The state before the supervisor has looked at anything.</summary>
    public static SlskdStatusSnapshot NotConfigured { get; } = new(SlskdState.NotConfigured);
}

/// <summary>
/// The supervisor's state, shared by the process itself and the health check. Written only by
/// <see cref="SlskdHost"/>, read by anyone; every read sees one complete snapshot.
/// </summary>
public sealed class SlskdStatus
{
    private readonly Lock _gate = new();

    private SlskdStatusSnapshot _snapshot = SlskdStatusSnapshot.NotConfigured;

    /// <summary>The last snapshot the supervisor published.</summary>
    public SlskdStatusSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Replaces the snapshot.</summary>
    public void Set(SlskdStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>
    /// Replaces the snapshot with an updated copy. <paramref name="update"/> is applied under the
    /// lock, so concurrent updates cannot lose each other's changes.
    /// </summary>
    public SlskdStatusSnapshot Update(Func<SlskdStatusSnapshot, SlskdStatusSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            _snapshot = update(_snapshot);

            return _snapshot;
        }
    }
}
