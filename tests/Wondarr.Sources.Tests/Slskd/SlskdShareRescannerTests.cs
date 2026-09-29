using FluentAssertions;
using Wondarr.Core.Importing;
using Wondarr.Sources.Slskd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public sealed class SlskdShareRescannerTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly SlskdStatus _status = new();
    private readonly ISlskdClient _client = Substitute.For<ISlskdClient>();
    private readonly IOptionsMonitor<SoulseekOptions> _monitor = Substitute.For<IOptionsMonitor<SoulseekOptions>>();
    private readonly List<Action<SoulseekOptions, string?>> _listeners = [];
    private readonly ServiceProvider _provider;
    private readonly SlskdShareRescanner _rescanner;
    private readonly CancellationTokenSource _stopping = new();

    private SoulseekOptions _options = new() { ShareLibrary = true, SharedFolders = ["/data/music"] };

    public SlskdShareRescannerTests()
    {
        _status.Set(new SlskdStatusSnapshot(SlskdState.Running, IsReachable: true, IsLoggedIn: true));

        _monitor.CurrentValue.Returns(_ => _options);
        _monitor.OnChange(Arg.Any<Action<SoulseekOptions, string?>>()).Returns(call =>
        {
            _listeners.Add(call.Arg<Action<SoulseekOptions, string?>>());
            return Substitute.For<IDisposable>();
        });

        _client.RescanSharesAsync(Arg.Any<CancellationToken>()).Returns(SlskdRescanOutcome.Started);

        var services = new ServiceCollection();
        services.AddScoped(_ => _client);
        _provider = services.BuildServiceProvider();

        _rescanner = new SlskdShareRescanner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _monitor,
            _status,
            _time,
            NullLogger<SlskdShareRescanner>.Instance);
    }

    [Fact]
    public async Task An_import_starts_a_rescan_once_the_library_has_been_quiet()
    {
        await _rescanner.StartAsync(_stopping.Token);

        await _rescanner.HandleAsync(new SongImportedEvent(1, 1, Upgraded: false), CancellationToken.None);

        await AdvanceAsync(SlskdShareRescanner.QuietPeriod - TimeSpan.FromSeconds(1));
        await _client.DidNotReceive().RescanSharesAsync(Arg.Any<CancellationToken>());

        await AdvanceUntilAsync(() => Scans() == 1, "the rescan after the quiet period");
        _rescanner.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task A_burst_of_imports_is_one_rescan()
    {
        await _rescanner.StartAsync(_stopping.Token);

        for (var index = 0; index < 5; index++)
        {
            _rescanner.RequestRescan();
            await AdvanceAsync(TimeSpan.FromSeconds(5));
        }

        await AdvanceAsync(SlskdShareRescanner.QuietPeriod + TimeSpan.FromSeconds(5));

        Scans().Should().Be(1);
    }

    [Fact]
    public async Task A_steady_stream_of_imports_is_still_shared_as_it_goes()
    {
        await _rescanner.StartAsync(_stopping.Token);

        // An import every 10 s never leaves the library quiet for 30 s.
        for (var elapsed = TimeSpan.Zero; elapsed < SlskdShareRescanner.MaxDelay + TimeSpan.FromSeconds(20); elapsed += TimeSpan.FromSeconds(10))
        {
            _rescanner.RequestRescan();
            await AdvanceAsync(TimeSpan.FromSeconds(10));
        }

        Scans().Should().Be(1, "the first waiting import is shared no later than MaxDelay");
    }

    [Fact]
    public async Task A_scan_already_running_is_followed_by_another()
    {
        _client.RescanSharesAsync(Arg.Any<CancellationToken>())
            .Returns(SlskdRescanOutcome.AlreadyScanning, SlskdRescanOutcome.Started);

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceUntilAsync(() => Scans() == 1, "the first attempt");
        _rescanner.IsPending.Should().BeTrue();

        await AdvanceAsync(SlskdShareRescanner.RetryDelay - TimeSpan.FromSeconds(2));
        Scans().Should().Be(1, "the retry waits RetryDelay");

        await AdvanceUntilAsync(() => Scans() == 2, "the retry");
        _rescanner.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_request_is_retried()
    {
        _client.RescanSharesAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new HttpRequestException("slskd is not answering"),
            _ => Task.FromResult(SlskdRescanOutcome.Started));

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceUntilAsync(() => Scans() == 2, "the retry after a failure");
        _rescanner.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_scanned_when_sharing_is_off()
    {
        _options = new SoulseekOptions { ShareLibrary = false, SharedFolders = ["/data/music"] };

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceUntilAsync(() => !_rescanner.IsPending, "the request to be dropped");
        await AdvanceAsync(SlskdShareRescanner.RetryDelay * 2);

        Scans().Should().Be(0);
    }

    [Theory]
    [InlineData(SlskdState.Disabled)]
    [InlineData(SlskdState.BinaryMissing)]
    [InlineData(SlskdState.Starting)]
    [InlineData(SlskdState.Restarting)]
    [InlineData(SlskdState.Crashed)]
    public async Task A_slskd_that_is_not_running_is_left_to_scan_when_it_starts(SlskdState state)
    {
        _status.Set(new SlskdStatusSnapshot(state));

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceUntilAsync(() => !_rescanner.IsPending, "the request to be dropped");
        await AdvanceAsync(SlskdShareRescanner.RetryDelay * 2);

        Scans().Should().Be(0);
    }

    [Fact]
    public async Task A_settings_reload_with_sharing_on_asks_for_a_rescan()
    {
        await _rescanner.StartAsync(_stopping.Token);
        await WaitForAsync(() => _listeners.Count > 0, "the options subscription");

        RaiseOptionsChanged(new SoulseekOptions { ShareLibrary = false, SharedFolders = ["/data/music"] });
        _rescanner.IsPending.Should().BeFalse("turning sharing off needs no scan");

        RaiseOptionsChanged(new SoulseekOptions { ShareLibrary = true, SharedFolders = ["/data/music"] });

        await AdvanceUntilAsync(() => Scans() == 1, "the rescan after the reload");
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _rescanner.StopAsync(CancellationToken.None);
        _rescanner.Dispose();
        _stopping.Dispose();
        await _provider.DisposeAsync();
    }

    private int Scans() => _client.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISlskdClient.RescanSharesAsync));

    private void RaiseOptionsChanged(SoulseekOptions next)
    {
        _options = next;
        foreach (var listener in _listeners.ToArray())
        {
            listener(next, Options.DefaultName);
        }
    }

    /// <summary>Advances fake time one second at a time, letting the rescanner's loop run in between.</summary>
    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(1))
        {
            await Settle();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Settle();
    }

    private async Task AdvanceUntilAsync(Func<bool> condition, string because)
    {
        for (var step = 0; step < 600; step++)
        {
            if (condition())
            {
                return;
            }

            await Settle();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Settle();
        condition().Should().BeTrue($"expected {because} within 600 fake seconds");
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue($"expected {because}");
    }

    // Gives the rescanner's continuations a turn on the thread pool.
    private static Task Settle() => Task.Delay(5);
}
