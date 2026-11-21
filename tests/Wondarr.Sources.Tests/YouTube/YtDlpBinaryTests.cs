using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Configuration;
using Wondarr.Core.Media;
using Wondarr.Core.MusicVideos;
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Sources.Tests.YouTube;

/// <summary>
/// Which yt-dlp runs, and the update that keeps Wondarr's own copy current, against a fake process runner and a
/// temporary config directory: no network and no real yt-dlp.
/// </summary>
public sealed class YtDlpBinaryTests : IDisposable
{
    private const string UpToDateOutput =
        "Latest version: stable@2026.10.26 from yt-dlp/yt-dlp\nyt-dlp is up to date (stable@2026.10.26 from yt-dlp/yt-dlp)\n";

    private const string UpdatedOutput =
        "Current version: stable@2026.08.19 from yt-dlp/yt-dlp\nLatest version: stable@2026.10.26 from yt-dlp/yt-dlp\n"
        + "Current Build Hash: abc\nUpdating to stable@2026.10.26 from yt-dlp/yt-dlp ...\nUpdated yt-dlp to stable@2026.10.26 from yt-dlp/yt-dlp\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-ytdlp-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundled;
    private readonly FakeProcessRunner _runner = new();
    private readonly YouTubeOptions _options = new() { Enabled = true };
    private readonly MusicVideoOptions _musicVideos = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));
    private readonly WarningLog _log = new();
    private readonly YtDlpBinary _binary;

    public YtDlpBinaryTests()
    {
        _bundled = Path.Combine(_root, "usr", "yt-dlp");
        Directory.CreateDirectory(Path.GetDirectoryName(_bundled)!);
        File.WriteAllText(_bundled, "bundled-bytes");

        _binary = new YtDlpBinary(
            _runner,
            new TestOptionsMonitor<YouTubeOptions>(_options),
            new TestOptionsMonitor<MusicVideoOptions>(_musicVideos),
            new WondarrPaths(Path.Combine(_root, "config")),
            new FixedLocator(_bundled),
            _time,
            _log);
    }

    public void Dispose()
    {
        _binary.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is harmless.
        }
    }

    /// <summary>An updater run that swaps the managed file's contents, the way yt-dlp's own rename does.</summary>
    private Func<CancellationToken, Task<ProcessResult>> Updater(string newContent, string output) => _ =>
    {
        File.WriteAllText(_binary.ManagedPath, newContent);

        return Task.FromResult(new ProcessResult(0, output, string.Empty, TimedOut: false));
    };

    [Fact]
    public async Task The_first_run_copies_the_bundled_binary_then_runs_the_updater_on_the_copy()
    {
        _runner.Enqueue(UpToDateOutput);

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.UpToDate);
        _binary.ManagedPath.Should().Be(Path.Combine(_root, "config", "tools", "yt-dlp", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp"));
        File.ReadAllText(_binary.ManagedPath).Should().Be("bundled-bytes");
        File.ReadAllText(_bundled).Should().Be("bundled-bytes", "the bundled binary is never written over");
        _runner.Calls.Should().ContainSingle();
        _runner.Calls[0].FileName.Should().Be(_binary.ManagedPath);
        _runner.Calls[0].Arguments.Should().Equal("--update-to", "stable");
        Directory.GetFiles(Path.GetDirectoryName(_binary.ManagedPath)!).Should().ContainSingle("no temporary file is left behind");
    }

    [Fact]
    public async Task The_managed_copy_is_executable_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _runner.Enqueue(UpToDateOutput);

        await _binary.UpdateAsync(CancellationToken.None);

        File.GetUnixFileMode(_binary.ManagedPath).Should().HaveFlag(UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task An_update_to_a_newer_release_keeps_the_old_copy_as_previous()
    {
        _runner.Enqueue(Updater("new-bytes", UpdatedOutput)).Enqueue("2026.10.26\n");

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Updated);
        run.Message.Should().Contain("2026.10.26");
        File.ReadAllText(_binary.ManagedPath).Should().Be("new-bytes");
        File.ReadAllText(_binary.PreviousPath).Should().Be("bundled-bytes");
        _runner.Calls[1].Arguments.Should().Equal("--version");

        EnqueueResolution();
        var status = await _binary.GetStatusAsync(CancellationToken.None);

        status.LastCheckSucceeded.Should().BeTrue();
        status.LatestVersion.Should().Be("2026.10.26");
        status.LastSuccessAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task An_up_to_date_run_is_recorded_without_touching_the_previous_copy()
    {
        _runner.Enqueue(UpToDateOutput);

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.UpToDate);
        File.Exists(_binary.PreviousPath).Should().BeFalse();
        EnqueueResolution();
        (await _binary.GetStatusAsync(CancellationToken.None)).LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public async Task A_failed_update_keeps_the_binary_and_is_a_warning_not_an_error()
    {
        _runner.Enqueue(
            new ProcessResult(
                100,
                string.Empty,
                "ERROR: Unable to obtain version info (<urlopen error>); Please try again later or visit  https://github.com/yt-dlp/yt-dlp/releases/latest\n",
                TimedOut: false));

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Failed);
        run.Message.Should().Contain("Unable to obtain version info");
        File.ReadAllText(_binary.ManagedPath).Should().Be("bundled-bytes");
        _log.Levels.Should().Contain(LogLevel.Warning).And.NotContain(LogLevel.Error);

        EnqueueResolution();
        var status = await _binary.GetStatusAsync(CancellationToken.None);

        status.LastCheckSucceeded.Should().BeFalse();
        status.LastSuccessAt.Should().BeNull();
        Directory.GetFiles(Path.GetDirectoryName(_binary.ManagedPath)!).Should().ContainSingle("the pre-update snapshot is removed");
    }

    [Fact]
    public async Task An_updated_copy_that_does_not_answer_version_is_replaced_by_the_one_that_did()
    {
        _runner
            .Enqueue(Updater("broken-bytes", UpdatedOutput))
            .Enqueue(new ProcessResult(126, string.Empty, "Permission denied", TimedOut: false));

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Failed);
        run.Message.Should().Contain("previous copy was put back");
        File.ReadAllText(_binary.ManagedPath).Should().Be("bundled-bytes");
        _log.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task A_js_runtime_complaint_from_version_goes_into_the_status_and_the_log_at_warning()
    {
        _runner
            .Enqueue(Updater("new-bytes", UpdatedOutput))
            .Enqueue(new ProcessResult(0, "2026.10.26\n", "WARNING: Deno 1.40 is unsupported; please upgrade\n", TimedOut: false));
        EnqueueResolution();

        var run = await _binary.UpdateAsync(CancellationToken.None);
        var status = await _binary.GetStatusAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Updated);
        run.Message.Should().Contain("Deno 1.40 is unsupported");
        status.JsRuntimeWarning.Should().Contain("Deno 1.40 is unsupported");
        _log.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task An_updater_that_fails_after_the_swap_but_leaves_a_working_binary_counts_as_updated()
    {
        // update.py exits 100 with "Unable to remove the old version" after the new binary is in place.
        _runner
            .Enqueue(_ =>
            {
                File.WriteAllText(_binary.ManagedPath, "new-bytes");

                return Task.FromResult(new ProcessResult(100, string.Empty, "ERROR: Unable to remove the old version\n", TimedOut: false));
            })
            .Enqueue("2026.10.26\n");
        EnqueueResolution();

        var run = await _binary.UpdateAsync(CancellationToken.None);
        var status = await _binary.GetStatusAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Updated);
        run.Message.Should().Contain("Unable to remove the old version");
        File.ReadAllText(_binary.ManagedPath).Should().Be("new-bytes");
        File.ReadAllText(_binary.PreviousPath).Should().Be("bundled-bytes");
        status.LastCheckSucceeded.Should().BeTrue();
        status.LatestVersion.Should().Be("2026.10.26");
    }

    [Fact]
    public async Task An_updater_that_fails_after_the_swap_and_leaves_a_binary_that_does_not_run_is_rolled_back()
    {
        // update.py exits 100 with "Unable to set permissions" and leaves a file that cannot be run.
        _runner
            .Enqueue(_ =>
            {
                File.WriteAllText(_binary.ManagedPath, "not-executable");

                return Task.FromResult(new ProcessResult(100, string.Empty, "ERROR: Unable to set permissions\n", TimedOut: false));
            })
            .Enqueue(new ProcessResult(126, string.Empty, "Permission denied", TimedOut: false));

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Failed);
        run.Message.Should().Contain("previous copy was put back");
        File.ReadAllText(_binary.ManagedPath).Should().Be("bundled-bytes");
        File.Exists(_binary.PreviousPath).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(_binary.ManagedPath)!).Should().ContainSingle();
    }

    [Fact]
    public async Task An_invalidate_during_a_slow_resolution_leaves_no_stale_decision()
    {
        WriteManagedCopy();
        _runner
            .Enqueue(_ =>
            {
                // The settings page's Test (or an update) invalidates while the probes are still running.
                _binary.Invalidate();

                return Task.FromResult(new ProcessResult(0, "2026.10.26\n", string.Empty, TimedOut: false));
            })
            .Enqueue("2026.08.19\n");

        var stale = await _binary.ResolveAsync(CancellationToken.None);

        EnqueueResolution();

        var fresh = await _binary.ResolveAsync(CancellationToken.None);

        _runner.Calls.Should().HaveCount(4, "the first answer was not kept, so the second call probed again");
        fresh.Generation.Should().BeGreaterThan(stale.Generation);

        await _binary.ResolveAsync(CancellationToken.None);

        _runner.Calls.Should().HaveCount(4, "the fresh answer was kept");
    }

    [Fact]
    public async Task A_copy_that_cannot_be_started_names_the_real_cause_and_the_config_directory()
    {
        WriteManagedCopy();
        _runner.Enqueue(_ => throw new MediaToolMissingException(
            _binary.ManagedPath,
            new System.ComponentModel.Win32Exception("Permission denied")));

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.FallbackReason.Should().Contain("Permission denied").And.Contain("may not allow running programs");
        resolution.FallbackReason.Should().NotContain("not installed");
    }

    [Fact]
    public async Task The_nightly_channel_is_passed_to_the_updater()
    {
        _options.Ytdlp.UpdateChannel = YtDlpOptions.NightlyChannel;
        _runner.Enqueue(
            "Latest version: nightly@2026.10.27.232859 from yt-dlp/yt-dlp-nightly-builds\n"
            + "yt-dlp is up to date (nightly@2026.10.27.232859 from yt-dlp/yt-dlp-nightly-builds)\n");

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.UpToDate);
        _runner.Calls[0].Arguments.Should().Equal("--update-to", "nightly");
        EnqueueResolution();
        (await _binary.GetStatusAsync(CancellationToken.None)).LatestVersion.Should().Be("2026.10.27.232859");
    }

    [Fact]
    public async Task A_newer_bundled_binary_replaces_an_older_managed_copy_before_the_update()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_binary.ManagedPath)!);
        File.WriteAllText(_binary.ManagedPath, "old-managed");

        // The managed copy answers 2026.08.19, the bundled one 2026.10.26.
        _runner.Enqueue("2026.08.19\n").Enqueue("2026.10.26\n").Enqueue(UpToDateOutput);

        await _binary.UpdateAsync(CancellationToken.None);

        File.ReadAllText(_binary.ManagedPath).Should().Be("bundled-bytes");
    }

    [Fact]
    public async Task A_custom_binary_path_is_never_touched()
    {
        _options.Ytdlp.BinaryPath = "/opt/my/yt-dlp";

        var run = await _binary.UpdateAsync(CancellationToken.None);
        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Skipped);
        _runner.Calls.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "config")).Should().BeFalse();
        resolution.Source.Should().Be(YtDlpBinarySource.Custom);
        resolution.Path.Should().Be("/opt/my/yt-dlp");
    }

    [Fact]
    public async Task The_update_skips_without_a_process_when_auto_update_is_off()
    {
        _options.Ytdlp.AutoUpdate = false;

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Skipped);
        _runner.Calls.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "config")).Should().BeFalse();
    }

    [Fact]
    public async Task The_update_skips_without_a_process_when_youtube_and_music_videos_are_both_off()
    {
        _options.Enabled = false;
        _musicVideos.Enabled = false;

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.Skipped);
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Music_videos_alone_are_enough_to_update()
    {
        _options.Enabled = false;
        _musicVideos.Enabled = true;
        _runner.Enqueue(UpToDateOutput);

        var run = await _binary.UpdateAsync(CancellationToken.None);

        run.Kind.Should().Be(YtDlpUpdateRunKind.UpToDate);
    }

    [Fact]
    public async Task Without_a_managed_copy_the_bundled_binary_runs()
    {
        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Bundled);
        resolution.Path.Should().Be("yt-dlp");
        _runner.Calls.Should().BeEmpty("nothing has to be asked when there is no copy to choose");
    }

    [Fact]
    public async Task A_managed_copy_that_answers_is_the_one_that_runs()
    {
        WriteManagedCopy();
        _runner.Enqueue("2026.10.26\n").Enqueue("2026.08.19\n");

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Managed);
        resolution.Path.Should().Be(_binary.ManagedPath);
        resolution.BundledVersion.Should().Be("2026.08.19");
    }

    [Fact]
    public async Task A_managed_copy_that_fails_version_falls_back_to_the_bundled_one_with_a_warning()
    {
        WriteManagedCopy();
        _runner.Enqueue(new ProcessResult(126, string.Empty, "Permission denied", TimedOut: false));

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Bundled);
        resolution.Path.Should().Be("yt-dlp");
        resolution.FallbackReason.Should().Contain("does not run");
        _log.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task A_managed_copy_that_cannot_be_started_falls_back_too()
    {
        WriteManagedCopy();
        _runner.EnqueueMissing("yt-dlp");

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Bundled);
    }

    [Fact]
    public async Task A_newer_bundled_binary_wins_over_an_older_managed_copy()
    {
        WriteManagedCopy();
        _runner.Enqueue("2026.08.19\n").Enqueue("2026.10.26\n");

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Bundled);
        resolution.Path.Should().Be("yt-dlp");
    }

    [Fact]
    public async Task The_managed_copy_is_ignored_when_auto_update_is_off()
    {
        WriteManagedCopy();
        _options.Ytdlp.AutoUpdate = false;

        var resolution = await _binary.ResolveAsync(CancellationToken.None);

        resolution.Source.Should().Be(YtDlpBinarySource.Bundled);
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_resolution_is_remembered_until_an_update_changes_it()
    {
        WriteManagedCopy();
        _runner.Enqueue("2026.10.26\n").Enqueue("2026.08.19\n");

        var first = await _binary.ResolveAsync(CancellationToken.None);
        var second = await _binary.ResolveAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        _runner.Calls.Should().HaveCount(2, "the second call asked nobody");

        // The update compares the copy with the bundled binary, runs the updater, and the next resolution asks again.
        _runner.Enqueue("2026.10.26\n").Enqueue("2026.08.19\n").Enqueue(UpToDateOutput)
            .Enqueue("2026.10.26\n").Enqueue("2026.08.19\n");
        await _binary.UpdateAsync(CancellationToken.None);

        (await _binary.ResolveAsync(CancellationToken.None)).Generation.Should().BeGreaterThan(first.Generation);
    }

    [Fact]
    public async Task The_probe_cache_is_refreshed_after_an_update_changes_the_version()
    {
        // The availability caches one answer per generation of the resolution; an update starts a new one.
        var availability = new YtDlpAvailability(
            _runner,
            _binary,
            new TestOptionsMonitor<YouTubeOptions>(_options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<YtDlpAvailability>.Instance);

        _runner.Enqueue("2026.08.19\n").Enqueue("deno 2.9.7\n");

        (await availability.GetStatusAsync(CancellationToken.None)).Version.Should().Be("2026.08.19");

        // The first update run: copy, updater, --version of the new copy; then the probe asks again, and
        // the managed copy is compared with the bundled one.
        _runner
            .Enqueue(Updater("new-bytes", UpdatedOutput))
            .Enqueue("2026.10.26\n")
            .Enqueue("2026.10.26\n")
            .Enqueue("2026.08.19\n")
            .Enqueue("2026.10.26\n")
            .Enqueue("deno 2.9.7\n");

        await _binary.UpdateAsync(CancellationToken.None);

        var status = await availability.GetStatusAsync(CancellationToken.None);

        status.Version.Should().Be("2026.10.26");
        _runner.Calls.Last(call => call.Arguments[0] == "--version" && call.FileName != "deno")
            .FileName.Should().Be(_binary.ManagedPath, "the probe now asks the updated copy");
    }

    /// <summary>The two <c>--version</c> answers a resolution asks for once a managed copy exists.</summary>
    private void EnqueueResolution() => _runner.Enqueue("2026.10.26\n").Enqueue("2026.08.19\n");

    private void WriteManagedCopy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_binary.ManagedPath)!);
        File.WriteAllText(_binary.ManagedPath, "managed-bytes");
    }

    private sealed class FixedLocator(string path) : IExecutableLocator
    {
        public string? Locate(string name) => path;
    }

    /// <summary>Records the level of every line logged.</summary>
    private sealed class WarningLog : ILogger<YtDlpBinary>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
