using FluentAssertions;
using Wondarr.Core.Backup;
using Wondarr.Core.Housekeeping;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Xunit;

namespace Wondarr.Core.Tests.Jobs;

public class ScheduledTaskCatalogTests
{
    [Fact]
    public void Housekeeping_is_a_scheduled_task_that_runs_every_24_hours()
    {
        var catalog = Catalog(new SearchOptions());

        var task = catalog.Find(HousekeepingCommandHandler.CommandName);

        task.Should().NotBeNull();
        task!.Interval.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Check_for_updates_runs_every_12_hours_and_first_about_two_minutes_after_startup()
    {
        var catalog = Catalog(new SearchOptions());

        var task = catalog.Find(Wondarr.Core.Updates.CheckForUpdatesCommandHandler.CommandName);

        task.Should().NotBeNull();
        task!.Interval.Should().Be(TimeSpan.FromHours(12));
        task.FirstRunDelay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void The_intervals_follow_the_settings_as_they_change()
    {
        var search = new MutableOptionsMonitor<SearchOptions>(new SearchOptions());
        var catalog = new ScheduledTaskCatalog(search, Monitor(new BackupOptions()), []);

        catalog.Find(MissingSearchCommandHandler.CommandName)!.Interval.Should().Be(TimeSpan.FromHours(6));

        search.Set(new SearchOptions { MissingIntervalHours = 2, UpgradeIntervalHours = 48 });

        catalog.Find(MissingSearchCommandHandler.CommandName)!.Interval.Should().Be(TimeSpan.FromHours(2));
        catalog.Find(UpgradeSearchCommandHandler.CommandName)!.Interval.Should().Be(TimeSpan.FromHours(48));
    }

    [Fact]
    public void A_listener_hears_about_a_changed_setting_until_it_is_disposed()
    {
        var search = new MutableOptionsMonitor<SearchOptions>(new SearchOptions());
        var backup = new MutableOptionsMonitor<BackupOptions>(new BackupOptions());
        var catalog = new ScheduledTaskCatalog(search, backup, []);
        var heard = 0;

        var subscription = catalog.OnChange(() => heard++);

        search.Set(new SearchOptions { MissingIntervalHours = 3 });
        backup.Set(new BackupOptions { IntervalDays = 3 });
        heard.Should().Be(2);

        subscription.Dispose();
        search.Set(new SearchOptions { MissingIntervalHours = 4 });
        heard.Should().Be(2);
        search.ListenerCount.Should().Be(0);
    }

    [Fact]
    public void A_source_adds_its_tasks_and_its_changes_are_heard()
    {
        var source = new FakeSource(new ScheduledTaskDefinition("Extra", TimeSpan.FromHours(24)));
        var catalog = new ScheduledTaskCatalog(Monitor(new SearchOptions()), Monitor(new BackupOptions()), [source]);
        var heard = 0;

        using var subscription = catalog.OnChange(() => heard++);

        catalog.Find("Extra")!.Interval.Should().Be(TimeSpan.FromHours(24));

        source.Change(new ScheduledTaskDefinition("Extra", TimeSpan.FromHours(12)));

        heard.Should().Be(1);
        catalog.Find("Extra")!.Interval.Should().Be(TimeSpan.FromHours(12));
    }

    private static ScheduledTaskCatalog Catalog(SearchOptions search) =>
        new(Monitor(search), Monitor(new BackupOptions()), []);

    private static MutableOptionsMonitor<T> Monitor<T>(T value) => new(value);

    private sealed class FakeSource(ScheduledTaskDefinition task) : IScheduledTaskSource
    {
        private readonly List<Action> _listeners = [];

        public IReadOnlyList<ScheduledTaskDefinition> Tasks { get; private set; } = [task];

        public IDisposable OnChange(Action listener)
        {
            _listeners.Add(listener);

            return new Stop(() => _listeners.Remove(listener));
        }

        public void Change(ScheduledTaskDefinition changed)
        {
            Tasks = [changed];

            foreach (var listener in _listeners.ToList())
            {
                listener();
            }
        }

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }
}
