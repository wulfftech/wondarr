using FluentAssertions;
using Microsoft.Extensions.Options;
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
        var catalog = new ScheduledTaskCatalog(
            Options.Create(new SearchOptions()),
            Options.Create(new BackupOptions()));

        var task = catalog.Find(HousekeepingCommandHandler.CommandName);

        task.Should().NotBeNull();
        task!.Interval.Should().Be(TimeSpan.FromHours(24));
    }
}
