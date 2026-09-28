using Wondarr.Core.HealthCheck;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.HealthCheck;

public class HealthCheckServiceTests
{
    [Fact]
    public async Task Results_are_cached_for_sixty_seconds()
    {
        var timeProvider = new FakeTimeProvider();
        var check = new StubHealthCheck("Counter");
        using var service = CreateService(timeProvider, check);

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(59));
        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        check.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Results_are_recomputed_once_the_cache_expires()
    {
        var timeProvider = new FakeTimeProvider();
        var check = new StubHealthCheck("Counter");
        using var service = CreateService(timeProvider, check);

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        check.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_refresh_forces_every_check_to_run_again()
    {
        var timeProvider = new FakeTimeProvider();
        var check = new StubHealthCheck("Counter");
        using var service = CreateService(timeProvider, check);

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);
        var refreshed = await service.GetResultsAsync(forceRefresh: true, cancellationToken: CancellationToken.None);

        check.Calls.Should().Be(2);
        refreshed.Should().ContainSingle().Which.Source.Should().Be("Counter");
    }

    [Fact]
    public async Task A_check_that_throws_becomes_an_error_result()
    {
        var timeProvider = new FakeTimeProvider();
        var check = new StubHealthCheck(
            "Exploding",
            () => throw new InvalidOperationException("the database is on fire"));
        using var service = CreateService(timeProvider, check);

        var results = await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Source.Should().Be("Exploding");
        results[0].Type.Should().Be(HealthCheckResult.Error);
        results[0].Message.Should().Contain("the database is on fire");
    }

    [Fact]
    public async Task Every_check_is_reported_in_registration_order()
    {
        var timeProvider = new FakeTimeProvider();
        using var service = CreateService(
            timeProvider,
            new StubHealthCheck("First"),
            new StubHealthCheck("Second"),
            new StubHealthCheck("Third"));

        var results = await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        results.Select(result => result.Source).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public async Task A_real_run_publishes_the_completion_event_once()
    {
        var timeProvider = new FakeTimeProvider();
        var aggregator = new RecordingEventAggregator();
        using var service = CreateService(timeProvider, aggregator, new StubHealthCheck("Counter"));

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        aggregator.Completed.Should().ContainSingle();
        aggregator.Completed[0].Results.Should().ContainSingle().Which.Source.Should().Be("Counter");
    }

    [Fact]
    public async Task A_cached_read_publishes_nothing()
    {
        var timeProvider = new FakeTimeProvider();
        var aggregator = new RecordingEventAggregator();
        using var service = CreateService(timeProvider, aggregator, new StubHealthCheck("Counter"));

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);
        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);

        aggregator.Completed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_forced_refresh_publishes_again()
    {
        var timeProvider = new FakeTimeProvider();
        var aggregator = new RecordingEventAggregator();
        using var service = CreateService(timeProvider, aggregator, new StubHealthCheck("Counter"));

        await service.GetResultsAsync(forceRefresh: false, cancellationToken: CancellationToken.None);
        await service.GetResultsAsync(forceRefresh: true, cancellationToken: CancellationToken.None);

        aggregator.Completed.Should().HaveCount(2);
    }

    private static HealthCheckService CreateService(TimeProvider timeProvider, params IHealthCheck[] checks) =>
        CreateService(timeProvider, new RecordingEventAggregator(), checks);

    private static HealthCheckService CreateService(
        TimeProvider timeProvider,
        RecordingEventAggregator aggregator,
        params IHealthCheck[] checks)
    {
        var services = new ServiceCollection();
        foreach (var check in checks)
        {
            services.AddSingleton(check);
        }

        return new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            aggregator,
            NullLogger<HealthCheckService>.Instance);
    }
}
