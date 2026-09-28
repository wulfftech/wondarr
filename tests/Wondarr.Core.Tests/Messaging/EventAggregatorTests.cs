using Wondarr.Core.Jobs;
using Wondarr.Core.Messaging;
using Wondarr.Core.Tests.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wondarr.Core.Tests.Messaging;

public class EventAggregatorTests
{
    [Fact]
    public async Task Every_handler_sees_the_event()
    {
        var first = new RecordingEventHandler();
        var second = new RecordingEventHandler();

        await using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(first);
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(second);
        });

        var aggregator = provider.GetRequiredService<IEventAggregator>();
        var record = new CommandRecord { Id = 7, Name = "Heartbeat", Status = CommandStatus.Started };

        await aggregator.PublishAsync(new CommandUpdatedEvent(record), CancellationToken.None);

        first.Updates.Should().ContainSingle().Which.Should().Be((7, CommandStatus.Started));
        second.Updates.Should().ContainSingle().Which.Should().Be((7, CommandStatus.Started));
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_others()
    {
        var recorder = new RecordingEventHandler();

        await using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(new ThrowingEventHandler());
            services.AddSingleton<IHandle<CommandUpdatedEvent>>(recorder);
        });

        var aggregator = provider.GetRequiredService<IEventAggregator>();

        var act = async () => await aggregator.PublishAsync(
            new CommandUpdatedEvent(new CommandRecord { Id = 8, Name = "Heartbeat" }),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        recorder.Updates.Should().ContainSingle();
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEventAggregator, EventAggregator>();
        configure(services);

        return services.BuildServiceProvider();
    }
}
