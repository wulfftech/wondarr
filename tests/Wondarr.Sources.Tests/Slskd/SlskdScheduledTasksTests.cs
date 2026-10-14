using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wondarr.Core.Backup;
using Wondarr.Core.Jobs;
using Wondarr.Core.Searching;
using Wondarr.Sources.Slskd;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>Checks that the share rescan is a scheduled task, with an interval the settings decide.</summary>
public sealed class SlskdScheduledTasksTests
{
    [Fact]
    public void The_rescan_is_a_daily_task_in_the_scheduled_task_list()
    {
        var catalog = Catalog(new SoulseekOptions());

        var task = catalog.Find("RescanSoulseekShares");

        task.Should().NotBeNull("it appears in the scheduled task list");
        task!.Interval.Should().Be(TimeSpan.FromHours(24));
        catalog.Tasks.Select(definition => definition.CommandName).Should().Contain(["Housekeeping", "RescanSoulseekShares"]);
    }

    [Fact]
    public void The_interval_is_read_from_the_soulseek_settings()
    {
        var catalog = Catalog(new SoulseekOptions { ShareRescanIntervalHours = 6 });

        catalog.Find("RescanSoulseekShares")!.Interval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void A_settings_reload_is_passed_on_to_the_scheduler()
    {
        Action<SoulseekOptions, string?>? reload = null;
        var monitor = Substitute.For<IOptionsMonitor<SoulseekOptions>>();
        monitor.CurrentValue.Returns(new SoulseekOptions());
        monitor.OnChange(Arg.Any<Action<SoulseekOptions, string?>>()).Returns(call =>
        {
            reload = call.Arg<Action<SoulseekOptions, string?>>();

            return Substitute.For<IDisposable>();
        });

        var heard = 0;
        using var subscription = new SlskdScheduledTasks(monitor).OnChange(() => heard++);

        reload.Should().NotBeNull();
        reload!(new SoulseekOptions(), null);

        heard.Should().Be(1);
    }

    [Fact]
    public void The_slskd_registration_adds_the_task_and_its_handler()
    {
        var services = new ServiceCollection();

        services.AddWondarrSlskd(new ConfigurationBuilder().Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IScheduledTaskSource) && descriptor.ImplementationType == typeof(SlskdScheduledTasks));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandHandler) && descriptor.ImplementationType == typeof(RescanSoulseekSharesCommandHandler));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(168, true)]
    [InlineData(169, false)]
    public void The_interval_is_held_to_one_hour_to_one_week(int hours, bool valid)
    {
        var result = new SoulseekOptionsValidator().Validate(null, new SoulseekOptions { ShareRescanIntervalHours = hours });

        result.Succeeded.Should().Be(valid);

        if (!valid)
        {
            result.Failures.Should().ContainSingle().Which.Should().StartWith("soulseek.share_rescan_interval_hours:");
        }
    }

    private static ScheduledTaskCatalog Catalog(SoulseekOptions soulseek)
    {
        var monitor = Substitute.For<IOptionsMonitor<SoulseekOptions>>();
        monitor.CurrentValue.Returns(soulseek);

        var search = Substitute.For<IOptionsMonitor<SearchOptions>>();
        search.CurrentValue.Returns(new SearchOptions());

        var backup = Substitute.For<IOptionsMonitor<BackupOptions>>();
        backup.CurrentValue.Returns(new BackupOptions());

        return new ScheduledTaskCatalog(search, backup, [new SlskdScheduledTasks(monitor)]);
    }
}
