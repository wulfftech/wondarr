using Microsoft.Extensions.Options;
using Wondarr.Core.Backup;
using Wondarr.Core.Housekeeping;
using Wondarr.Core.ImportLists;
using Wondarr.Core.References;
using Wondarr.Core.Searching;
using Wondarr.Core.Updates;

namespace Wondarr.Core.Jobs;

/// <summary>
/// The tasks ARCHITECTURE §5.5 schedules. The intervals are read from the live settings on every call,
/// so <see cref="ScheduledTaskService"/> can move a task's schedule when one of them changes.
/// </summary>
public sealed class ScheduledTaskCatalog : IScheduledTaskCatalog
{
    /// <summary>How often the heartbeat runs.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the health checks run.</summary>
    public static readonly TimeSpan CheckHealthInterval = TimeSpan.FromMinutes(15);

    /// <summary>How often the missing-song search runs, when <c>search.missing_interval_hours</c> is left alone.</summary>
    public static readonly TimeSpan DefaultMissingSearchInterval = TimeSpan.FromHours(6);

    /// <summary>How often the cutoff-unmet upgrade search runs, when <c>search.upgrade_interval_hours</c> is left alone.</summary>
    public static readonly TimeSpan DefaultUpgradeSearchInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the import-list sync looks for due lists; each list has its own interval
    /// (<c>import_list.sync_interval_hours</c>), so this is only the granularity.
    /// </summary>
    public static readonly TimeSpan ImportListSyncInterval = TimeSpan.FromHours(1);

    /// <summary>How often the reference libraries are walked (ARCHITECTURE §5.5: daily).</summary>
    public static readonly TimeSpan ReferenceLibraryScanInterval = TimeSpan.FromHours(24);

    /// <summary>How often housekeeping runs (ARCHITECTURE §5.5: daily).</summary>
    public static readonly TimeSpan HousekeepingInterval = TimeSpan.FromHours(24);

    /// <summary>How often Wondarr asks GitHub for a newer release.</summary>
    public static readonly TimeSpan CheckForUpdatesInterval = TimeSpan.FromHours(12);

    private readonly IOptionsMonitor<SearchOptions> _searchOptions;
    private readonly IOptionsMonitor<BackupOptions> _backupOptions;
    private readonly IReadOnlyList<IScheduledTaskSource> _sources;

    /// <summary>Initialises a new instance of the <see cref="ScheduledTaskCatalog"/> class.</summary>
    /// <param name="searchOptions">The search settings, which decide how often the search tasks run.</param>
    /// <param name="backupOptions">The backup settings, which decide how often the backup task runs.</param>
    /// <param name="sources">Tasks defined by projects <c>Wondarr.Core</c> does not reference.</param>
    public ScheduledTaskCatalog(
        IOptionsMonitor<SearchOptions> searchOptions,
        IOptionsMonitor<BackupOptions> backupOptions,
        IEnumerable<IScheduledTaskSource> sources)
    {
        ArgumentNullException.ThrowIfNull(searchOptions);
        ArgumentNullException.ThrowIfNull(backupOptions);
        ArgumentNullException.ThrowIfNull(sources);

        _searchOptions = searchOptions;
        _backupOptions = backupOptions;
        _sources = [.. sources];
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDefinition> Tasks
    {
        get
        {
            var search = _searchOptions.CurrentValue;
            var backup = _backupOptions.CurrentValue;

            List<ScheduledTaskDefinition> tasks =
            [
                new(HeartbeatCommandHandler.CommandName, HeartbeatInterval),
                new(CheckHealthCommandHandler.CommandName, CheckHealthInterval),
                new(MissingSearchCommandHandler.CommandName, TimeSpan.FromHours(search.MissingIntervalHours)),
                new(UpgradeSearchCommandHandler.CommandName, TimeSpan.FromHours(search.UpgradeIntervalHours)),
                new(ReferenceLibraryScanCommandHandler.CommandName, ReferenceLibraryScanInterval),
                new(HousekeepingCommandHandler.CommandName, HousekeepingInterval),
                new(ImportListSyncCommandHandler.CommandName, ImportListSyncInterval),
                new(BackupCommandHandler.CommandName, TimeSpan.FromDays(backup.IntervalDays)),
                new(
                    CheckForUpdatesCommandHandler.CommandName,
                    CheckForUpdatesInterval,
                    CheckForUpdatesCommandHandler.FirstRunDelay),
            ];

            foreach (var source in _sources)
            {
                tasks.AddRange(source.Tasks);
            }

            return tasks;
        }
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        var subscriptions = new List<IDisposable>();

        if (_searchOptions.OnChange(_ => listener()) is { } search)
        {
            subscriptions.Add(search);
        }

        if (_backupOptions.OnChange(_ => listener()) is { } backup)
        {
            subscriptions.Add(backup);
        }

        subscriptions.AddRange(_sources.Select(source => source.OnChange(listener)));

        return new Subscriptions(subscriptions);
    }

    /// <inheritdoc />
    public ScheduledTaskDefinition? Find(string commandName) =>
        Tasks.FirstOrDefault(definition =>
            string.Equals(definition.CommandName, commandName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The subscriptions of one <see cref="OnChange"/> call, ended together.</summary>
    private sealed class Subscriptions(List<IDisposable> inner) : IDisposable
    {
        public void Dispose()
        {
            foreach (var subscription in inner)
            {
                subscription.Dispose();
            }
        }
    }
}
