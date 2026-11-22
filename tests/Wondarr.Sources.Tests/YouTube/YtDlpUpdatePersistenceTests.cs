using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Configuration;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// M-70 item 3: the yt-dlp update status is kept in the <c>setting</c> table and read back after a restart, and an
/// overdue check is queued after the start whatever the scheduler's stored next run says. Fake runner, fake store, fake
/// clock: no network and no real yt-dlp.
/// </summary>
public sealed class YtDlpUpdatePersistenceTests : IDisposable
{
    private const string UpToDateOutput =
        "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\nyt-dlp is up to date (stable@2026.10.26 from yt-dlp/yt-dlp)\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-ytdlp-persist-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundled;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));
    private readonly MemoryStore _store = new();
    private readonly List<YtDlpBinary> _binaries = [];

    public YtDlpUpdatePersistenceTests()
    {
        _bundled = Path.Combine(_root, "usr", "yt-dlp");
        Directory.CreateDirectory(Path.GetDirectoryName(_bundled)!);
        File.WriteAllText(_bundled, "bundled-bytes");
    }

    public void Dispose()
    {
        foreach (var binary in _binaries)
        {
            binary.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is harmless.
        }
    }

    /// <summary>A new service instance over the same config folder and the same store: what a restart makes.</summary>
    private YtDlpBinary NewInstance(FakeProcessRunner runner, IYtDlpUpdateStore? store = null)
    {
        var binary = new YtDlpBinary(
            runner,
            new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions { Enabled = true }),
            new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions()),
            new WondarrPaths(Path.Combine(_root, "config")),
            new FixedLocator(_bundled),
            _time,
            NullLogger<YtDlpBinary>.Instance,
            store ?? _store);
        _binaries.Add(binary);

        return binary;
    }

    /// <summary>The two <c>--version</c> probes a fresh instance makes to pick the managed copy.</summary>
    private static FakeProcessRunner Resolving() => new FakeProcessRunner().Enqueue("2026.10.26\n").Enqueue("2026.10.26\n");

    [Fact]
    public async Task The_status_survives_a_restart()
    {
        var first = NewInstance(new FakeProcessRunner().Enqueue(UpToDateOutput));
        await first.UpdateAsync(CancellationToken.None);
        var checkedAt = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromHours(2));

        var second = NewInstance(Resolving());
        var status = await second.GetStatusAsync(CancellationToken.None);

        status.LastCheckedAt.Should().Be(checkedAt, "the card must not say there was no check since the process started");
        status.LastCheckSucceeded.Should().BeTrue();
        status.LastSuccessAt.Should().Be(checkedAt);
        status.LatestVersion.Should().Be("2026.10.26");
        status.LastCheckMessage.Should().NotBeNullOrWhiteSpace();
        _store.Saved.Should().NotBeNull();
    }

    [Fact]
    public async Task A_failed_check_is_kept_and_the_last_success_stays()
    {
        var first = NewInstance(new FakeProcessRunner().Enqueue(UpToDateOutput));
        await first.UpdateAsync(CancellationToken.None);
        var success = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromHours(30));
        var failing = NewInstance(Resolving().Enqueue(new ProcessResult(100, string.Empty, "ERROR: Unable to obtain version info", TimedOut: false)));
        var run = await failing.UpdateAsync(CancellationToken.None);
        run.Kind.Should().Be(YtDlpUpdateRunKind.Failed);
        var failedAt = _time.GetUtcNow();

        var restarted = NewInstance(Resolving());
        var status = await restarted.GetStatusAsync(CancellationToken.None);

        status.LastCheckSucceeded.Should().BeFalse();
        status.LastCheckedAt.Should().Be(failedAt);
        status.LastSuccessAt.Should().Be(success, "a failed check does not move the last success");
        status.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_leaves_the_status_empty_and_does_not_throw()
    {
        var binary = NewInstance(new FakeProcessRunner(), new ThrowingStore());

        var status = await binary.GetStatusAsync(CancellationToken.None);

        status.LastCheckedAt.Should().BeNull();
        status.LastSuccessAt.Should().BeNull();
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_does_not_fail_the_run()
    {
        var binary = NewInstance(new FakeProcessRunner().Enqueue(UpToDateOutput), new ThrowingStore());

        var run = await binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.UpToDate);
    }

    [Fact]
    public async Task A_skipped_run_keeps_nothing()
    {
        var binary = new YtDlpBinary(
            new FakeProcessRunner(),
            new TestOptionsMonitor<YouTubeOptions>(new YouTubeOptions { Enabled = false }),
            new TestOptionsMonitor<MusicVideoOptions>(new MusicVideoOptions { Enabled = false }),
            new WondarrPaths(Path.Combine(_root, "config")),
            new FixedLocator(_bundled),
            _time,
            NullLogger<YtDlpBinary>.Instance,
            _store);
        _binaries.Add(binary);

        var run = await binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Skipped);
        _store.Saved.Should().BeNull();
    }

    [Fact]
    public async Task The_settings_store_keeps_the_status_under_its_key()
    {
        var settings = new MemorySettings();
        var services = new ServiceCollection().AddSingleton<ISettingsRepository>(settings).BuildServiceProvider();
        var store = new SettingsYtDlpUpdateStore(services.GetRequiredService<IServiceScopeFactory>());
        var status = new StoredYtDlpUpdate(_time.GetUtcNow(), true, "up to date", _time.GetUtcNow(), "2026.10.26", null);

        (await store.LoadAsync(CancellationToken.None)).Should().BeNull();

        await store.SaveAsync(status, CancellationToken.None);

        settings.Values.Should().ContainKey("youtube.ytdlp_update_status");
        (await store.LoadAsync(CancellationToken.None)).Should().Be(status);
    }

    private static YtDlpUpdateStatus StatusWith(DateTimeOffset? lastSuccess, bool active = true) =>
        new(YtDlpBinarySource.Bundled, null, true, "stable", active, null, null, null, lastSuccess, null, DateTimeOffset.MinValue, null, null);

    private (YtDlpOverdueUpdateCheck Check, ICommandQueue Queue) OverdueCheck(YtDlpUpdateStatus status)
    {
        var binary = Substitute.For<IYtDlpBinary>();
        binary.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        var queue = Substitute.For<ICommandQueue>();

        return (new YtDlpOverdueUpdateCheck(binary, queue, _time, NullLogger<YtDlpOverdueUpdateCheck>.Instance), queue);
    }

    private async Task<bool> RunAfterTheDelayAsync(YtDlpOverdueUpdateCheck check)
    {
        var run = check.RunOnceAsync(CancellationToken.None);
        _time.Advance(UpdateYtDlpCommandHandler.FirstRunDelay);

        return await run;
    }

    [Fact]
    public async Task An_overdue_check_is_queued_after_the_first_run_delay()
    {
        var (check, queue) = OverdueCheck(StatusWith(_time.GetUtcNow() - TimeSpan.FromHours(30)));

        var run = check.RunOnceAsync(CancellationToken.None);
        _time.Advance(UpdateYtDlpCommandHandler.FirstRunDelay - TimeSpan.FromSeconds(1));
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
        _time.Advance(TimeSpan.FromSeconds(1));

        (await run).Should().BeTrue();
        await queue.Received(1).EnqueueAsync(UpdateYtDlpCommandHandler.CommandName, null, CommandTrigger.Scheduled, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_start_with_no_check_ever_queues_one()
    {
        var (check, queue) = OverdueCheck(StatusWith(null));

        (await RunAfterTheDelayAsync(check)).Should().BeTrue();
        await queue.Received(1).EnqueueAsync(UpdateYtDlpCommandHandler.CommandName, null, CommandTrigger.Scheduled, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_recent_check_is_not_queued_again()
    {
        var (check, queue) = OverdueCheck(StatusWith(_time.GetUtcNow() - TimeSpan.FromHours(8)));

        (await RunAfterTheDelayAsync(check)).Should().BeFalse();
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_overdue_check_is_not_queued_when_the_update_has_nothing_to_do()
    {
        var (check, queue) = OverdueCheck(StatusWith(null, active: false));

        (await RunAfterTheDelayAsync(check)).Should().BeFalse("auto-update off, a custom path or nothing using yt-dlp are skipped");
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_overdue_check_waits_for_the_persisted_status_not_the_process_start()
    {
        // A success 3 h before the restart is recent; the same instance over the store sees it and queues nothing.
        var first = NewInstance(new FakeProcessRunner().Enqueue(UpToDateOutput));
        await first.UpdateAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(3));

        var restarted = NewInstance(Resolving());
        var check = new YtDlpOverdueUpdateCheck(restarted, Substitute.For<ICommandQueue>(), _time, NullLogger<YtDlpOverdueUpdateCheck>.Instance);

        (await RunAfterTheDelayAsync(check)).Should().BeFalse();
    }

    private sealed class FixedLocator(string path) : IExecutableLocator
    {
        public string? Locate(string name) => path;
    }

    private sealed class MemoryStore : IYtDlpUpdateStore
    {
        public StoredYtDlpUpdate? Saved { get; private set; }

        public Task<StoredYtDlpUpdate?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);

        public Task SaveAsync(StoredYtDlpUpdate status, CancellationToken cancellationToken)
        {
            Saved = status;

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStore : IYtDlpUpdateStore
    {
        public Task<StoredYtDlpUpdate?> LoadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("db down");

        public Task SaveAsync(StoredYtDlpUpdate status, CancellationToken cancellationToken) => throw new InvalidOperationException("db down");
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        public Dictionary<string, object?> Values { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            Values[key] = value;

            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.Remove(key));
    }

    [Fact]
    public async Task A_stored_status_that_could_not_be_read_is_merged_not_overwritten_by_the_next_save()
    {
        var success = _time.GetUtcNow() - TimeSpan.FromHours(30);
        var store = new FlakyStore(new StoredYtDlpUpdate(success, true, "up to date", success, "2026.10.26", null), failLoads: 1);
        var binary = NewInstance(
            new FakeProcessRunner().Enqueue(new ProcessResult(100, string.Empty, "ERROR: Unable to obtain version info", TimedOut: false)),
            store);

        (await binary.UpdateAsync(CancellationToken.None)).Kind.Should().Be(YtDlpUpdateRunKind.Failed);

        store.Saved.Should().NotBeNull();
        store.Saved!.LastSuccessAt.Should().Be(success, "the stored last success is kept when a failed run is saved");
        store.Saved.LastCheckSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task While_the_stored_status_cannot_be_read_nothing_is_saved()
    {
        var store = new FlakyStore(new StoredYtDlpUpdate(_time.GetUtcNow(), true, "x", _time.GetUtcNow(), "1", null), failLoads: int.MaxValue);
        var binary = NewInstance(new FakeProcessRunner().Enqueue(UpToDateOutput), store);

        await binary.UpdateAsync(CancellationToken.None);

        store.Saved.Should().BeNull("an empty status must not replace the stored one");
    }

    private sealed class FlakyStore(StoredYtDlpUpdate stored, int failLoads) : IYtDlpUpdateStore
    {
        private int _failures = failLoads;

        public StoredYtDlpUpdate? Saved { get; private set; }

        public Task<StoredYtDlpUpdate?> LoadAsync(CancellationToken cancellationToken)
        {
            if (_failures > 0)
            {
                _failures--;

                throw new InvalidOperationException("db down");
            }

            return Task.FromResult<StoredYtDlpUpdate?>(Saved ?? stored);
        }

        public Task SaveAsync(StoredYtDlpUpdate status, CancellationToken cancellationToken)
        {
            Saved = status;

            return Task.CompletedTask;
        }
    }
}
