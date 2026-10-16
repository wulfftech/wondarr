using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Logging;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// The memory of which indexers refused NZB downloads and until when (M-47): how long a refusal lasts,
/// that it ends on the clock, and that it is kept per upstream indexer.
/// </summary>
public sealed class NzbCooldownsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly NzbSource Dog = new(1, "DOGnzb");

    [Fact]
    public void Lasts_an_hour_by_default_and_never_more_than_a_day()
    {
        NzbCooldowns.DefaultCooldown.Should().Be(TimeSpan.FromHours(1));
        NzbCooldowns.MaxCooldown.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void The_host_registers_one_store_for_every_search()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRegistry>(new SecretRegistry());
        services.AddLogging();
        services.AddWondarrTorznab();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NzbCooldowns>().Should().BeSameAs(provider.GetRequiredService<NzbCooldowns>());
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData(7200, 7200)]
    [InlineData(60, 60)]
    [InlineData(86_400, 86_400)]
    [InlineData(259_200, 86_400)]
    public void A_refusal_lasts_its_retry_after_up_to_a_day_and_an_hour_without_one(int? retryAfterSeconds, int expectedSeconds)
    {
        var cooldowns = new NzbCooldowns(new FakeTimeProvider(Start));

        var until = cooldowns.Start(Dog, retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        until.Should().Be(Start.AddSeconds(expectedSeconds));
        cooldowns.TryGetUntil(Dog, out var found).Should().BeTrue();
        found.Should().Be(until);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void A_retry_after_of_zero_or_less_counts_as_absent(int seconds)
    {
        var cooldowns = new NzbCooldowns(new FakeTimeProvider(Start));

        cooldowns.Start(Dog, TimeSpan.FromSeconds(seconds)).Should().Be(Start.AddHours(1));

        cooldowns.TryGetUntil(Dog, out _).Should().BeTrue();
    }

    [Fact]
    public void Ends_on_the_clock_and_is_forgotten()
    {
        var time = new FakeTimeProvider(Start);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(Dog, TimeSpan.FromHours(2));

        time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromTicks(1));
        cooldowns.TryGetUntil(Dog, out _).Should().BeTrue("one tick is left");

        time.Advance(TimeSpan.FromTicks(1));
        cooldowns.TryGetUntil(Dog, out var until).Should().BeFalse("the end is the first instant it is over");
        until.Should().BeNull();
    }

    [Fact]
    public void A_shorter_refusal_does_not_cut_a_longer_cooldown_short_and_a_longer_one_extends_it()
    {
        var time = new FakeTimeProvider(Start);
        var cooldowns = new NzbCooldowns(time);
        cooldowns.Start(Dog, TimeSpan.FromHours(2));

        time.Advance(TimeSpan.FromMinutes(10));
        cooldowns.Start(Dog, TimeSpan.FromMinutes(5)).Should().Be(Start.AddHours(2));
        cooldowns.Start(Dog, TimeSpan.FromHours(3)).Should().Be(Start.AddMinutes(10).AddHours(3));
    }

    [Fact]
    public void Is_kept_per_indexer_row_and_per_upstream_name()
    {
        var cooldowns = new NzbCooldowns(new FakeTimeProvider(Start));

        cooldowns.Start(new NzbSource(7, "Prowlarr (DOGnzb)"), null);

        cooldowns.TryGetUntil(new NzbSource(7, "Prowlarr (DOGnzb)"), out _).Should().BeTrue();
        cooldowns.TryGetUntil(new NzbSource(7, "Prowlarr (NZBgeek)"), out _).Should().BeFalse("another upstream of the same row");
        cooldowns.TryGetUntil(new NzbSource(8, "Prowlarr (DOGnzb)"), out _).Should().BeFalse("the same name on another row");
    }
}
