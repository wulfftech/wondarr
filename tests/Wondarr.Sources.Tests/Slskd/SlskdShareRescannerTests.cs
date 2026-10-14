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

    [Fact]
    public async Task A_new_import_does_not_bring_a_retry_forward()
    {
        _client.RescanSharesAsync(Arg.Any<CancellationToken>())
            .Returns(SlskdRescanOutcome.AlreadyScanning, SlskdRescanOutcome.Started);

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceUntilAsync(() => Scans() == 1, "the first attempt");

        // An import 5 s into the retry wait would be due after 35 s; the retry still waits its minute.
        await AdvanceAsync(TimeSpan.FromSeconds(5));
        _rescanner.RequestRescan();

        await AdvanceAsync(SlskdShareRescanner.RetryDelay - TimeSpan.FromSeconds(10));
        Scans().Should().Be(1);

        await AdvanceUntilAsync(() => Scans() == 2, "the retry");
    }

    [Theory]
    [InlineData(SlskdState.Starting)]
    [InlineData(SlskdState.Restarting)]
    [InlineData(SlskdState.Crashed)]
    public async Task A_slskd_that_is_starting_is_rescanned_once_it_runs(SlskdState state)
    {
        _status.Set(new SlskdStatusSnapshot(state));

        await _rescanner.StartAsync(_stopping.Token);
        _rescanner.RequestRescan();

        await AdvanceAsync(SlskdShareRescanner.QuietPeriod + SlskdShareRescanner.RetryDelay);
        Scans().Should().Be(0);
        _rescanner.IsPending.Should().BeTrue();

        _status.Set(new SlskdStatusSnapshot(SlskdState.Running, IsReachable: true, IsLoggedIn: true));

        await AdvanceUntilAsync(() => Scans() == 1, "the rescan once slskd runs");
    }

    [Theory]
    [InlineData(SlskdState.Disabled)]
    [InlineData(SlskdState.BinaryMissing)]
    [InlineData(SlskdState.Stopped)]
    [InlineData(SlskdState.NotConfigured)]
    public async Task A_slskd_that_is_not_ours_to_run_drops_the_request(SlskdState state)
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

    [Fact]
    public async Task The_daily_task_asks_slskd_for_a_rescan_at_once_with_the_same_call()
    {
        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().Be("Asked slskd to rescan its shares");
        Scans().Should().Be(1);
    }

    [Fact]
    public async Task The_daily_task_says_when_slskd_was_already_scanning()
    {
        _client.RescanSharesAsync(Arg.Any<CancellationToken>()).Returns(SlskdRescanOutcome.AlreadyScanning);

        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().Be("slskd was already scanning its shares");
    }

    [Fact]
    public async Task The_daily_task_does_nothing_and_says_so_when_sharing_is_off()
    {
        _options = new SoulseekOptions { ShareLibrary = false, SharedFolders = ["/data/music"] };

        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().StartWith("Nothing to do: sharing is off");
        Scans().Should().Be(0);
    }

    [Fact]
    public async Task The_daily_task_does_nothing_when_no_folder_is_shared()
    {
        _options = new SoulseekOptions { ShareLibrary = true, SharedFolders = [] };

        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().StartWith("Nothing to do:");
        Scans().Should().Be(0);
    }

    [Theory]
    [InlineData(SlskdState.Starting, "Nothing to do: slskd is starting")]
    [InlineData(SlskdState.Restarting, "Nothing to do: slskd is restarting")]
    [InlineData(SlskdState.Crashed, "Nothing to do: slskd crashed and is being restarted")]
    [InlineData(SlskdState.BinaryMissing, "Nothing to do: the slskd binary is missing")]
    [InlineData(SlskdState.Stopped, "Nothing to do: slskd is stopped")]
    public async Task The_daily_task_does_nothing_when_slskd_is_not_up(SlskdState state, string expected)
    {
        _status.Set(new SlskdStatusSnapshot(state));

        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().Be(expected);
        Scans().Should().Be(0);
    }

    [Fact]
    public async Task The_daily_task_reports_a_slskd_that_does_not_answer_instead_of_failing()
    {
        _client.RescanSharesAsync(Arg.Any<CancellationToken>()).Returns<Task<SlskdRescanOutcome>>(
            _ => throw new HttpRequestException("connection refused"));

        var result = await _rescanner.RescanNowAsync(CancellationToken.None);

        result.Should().Be("Nothing to do: slskd did not answer");
        Scans().Should().Be(1);
    }

    [Fact]
    public async Task The_daily_task_still_stops_when_it_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _client.RescanSharesAsync(Arg.Any<CancellationToken>()).Returns<Task<SlskdRescanOutcome>>(
            _ => throw new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rescanner.RescanNowAsync(cancelled.Token));
    }

    [Fact]
    public async Task The_daily_task_leaves_an_external_slskd_alone_unless_rescanning_it_is_on()
    {
        _options = new SoulseekOptions { Mode = SoulseekMode.External };

        (await _rescanner.RescanNowAsync(CancellationToken.None)).Should().StartWith("Nothing to do:");
        Scans().Should().Be(0);

        _options = new SoulseekOptions { Mode = SoulseekMode.External, External = new SoulseekExternalOptions { RescanShares = true } };
        _status.Set(new SlskdStatusSnapshot(SlskdState.External, IsReachable: false));

        (await _rescanner.RescanNowAsync(CancellationToken.None)).Should().Be("Nothing to do: the external slskd is not reachable");
        Scans().Should().Be(0);

        _status.Set(new SlskdStatusSnapshot(SlskdState.External, IsReachable: true));

        (await _rescanner.RescanNowAsync(CancellationToken.None)).Should().Be("Asked slskd to rescan its shares");
        Scans().Should().Be(1);
    }

    [Fact]
    public async Task The_scheduled_command_runs_the_rescan_and_returns_what_happened()
    {
        var handler = new RescanSoulseekSharesCommandHandler(_rescanner);

        var message = await handler.ExecuteAsync(new Wondarr.Core.Jobs.CommandContext(1, null, Wondarr.Core.Jobs.CommandTrigger.Scheduled, _ => Task.CompletedTask), CancellationToken.None);

        handler.Name.Should().Be("RescanSoulseekShares");
        message.Should().Be("Asked slskd to rescan its shares");
        Scans().Should().Be(1);
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
