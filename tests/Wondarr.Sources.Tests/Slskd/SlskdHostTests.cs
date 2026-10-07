using Wondarr.Core.HealthCheck;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdHostTests
{
    [Fact]
    public async Task Starts_the_bundled_slskd_without_credentials_and_reports_it_healthy()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to reach the running state");

        // The config slskd is told to start with, rendered for an account that is not configured yet.
        (await harness.ReadConfigAsync()).Should().Contain("no_connect: true");

        harness.Launcher.Count.Should().Be(1);

        var request = harness.Launcher.Launches[0].Request;
        request.FileName.Should().Be(harness.Options.BinaryPath);
        request.Arguments.Should().BeEmpty();
        request.WorkingDirectory.Should().Be(Path.GetDirectoryName(Path.GetFullPath(harness.Options.BinaryPath)));
        request.Environment.Should().HaveCount(4);
        request.Environment["SLSKD_APP_DIR"].Should().Be(harness.Paths.SlskdDir);
        request.Environment["SLSKD_CONFIG"].Should().Be(harness.ConfigPath);
        request.Environment["SLSKD_HEADLESS"].Should().Be("true");
        request.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"].Should().Be(Path.Combine(harness.Paths.SlskdDir, ".net"));

        // The secrets only ever live in the file, never on the command line.
        File.Exists(harness.ConfigPath).Should().BeTrue();

        var status = harness.Status.Current;
        status.ProcessId.Should().Be(harness.Launcher.Latest.Id);
        status.Version.Should().Be("0.26.0.0");
        status.IsReachable.Should().BeTrue();
        status.LastError.Should().BeNull();

        var health = await harness.HealthCheck.CheckAsync(CancellationToken.None);
        health.Source.Should().Be(SlskdHealthCheck.CheckName);
        health.Type.Should().Be(HealthCheckResult.Ok);
        health.Message.Should().Be("slskd 0.26.0.0 running; Soulseek: not configured");

        // slskd rejects a configuration whose directories do not exist, so the host creates them.
        Directory.Exists(harness.Options.DownloadsDir).Should().BeTrue();
        Directory.Exists(harness.Options.IncompleteDir).Should().BeTrue();
        Directory.Exists(harness.Options.SharedFolders[0]).Should().BeTrue();
    }

    [Fact]
    public async Task An_unexpected_exit_is_restarted_after_five_seconds_and_the_next_one_after_fifteen()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "the first slskd to start");

        var first = harness.Launcher.Latest;
        first.ExitWith(9);

        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.State == SlskdState.Crashed,
            TimeSpan.FromSeconds(1),
            60,
            "the exit to be noticed");

        var firstCrash = harness.Time.GetUtcNow();
        harness.Status.Current.LastError.Should().Contain("9");
        harness.Launcher.Count.Should().Be(1);

        // Five seconds of backoff: four of them are not enough.
        harness.Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        harness.Launcher.Count.Should().Be(1);

        await harness.AdvanceUntilAsync(
            () => harness.Launcher.Count == 2,
            TimeSpan.FromSeconds(1),
            10,
            "the first restart");

        (harness.Launcher.Launches[1].At - firstCrash).Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "the restarted slskd to answer");

        // The second crash in a row waits longer: the backoff backs off.
        harness.Launcher.Latest.ExitWith(9);

        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.State == SlskdState.Crashed,
            TimeSpan.FromSeconds(1),
            60,
            "the second exit to be noticed");

        var secondCrash = harness.Time.GetUtcNow();

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        harness.Launcher.Count.Should().Be(2);

        await harness.AdvanceUntilAsync(
            () => harness.Launcher.Count == 3,
            TimeSpan.FromSeconds(1),
            10,
            "the second restart");

        (harness.Launcher.Launches[2].At - secondCrash).Should().BeCloseTo(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2));
        harness.Status.Current.RestartCount.Should().Be(3);
    }

    [Fact]
    public async Task A_new_password_restarts_the_process_exactly_once_even_when_applied_twice_at_onces()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        var next = WithCredentials(harness.Options);
        harness.CurrentOptions = next;

        await Task.WhenAll(
            harness.Host.ApplySettingsAsync(next, CancellationToken.None),
            harness.Host.ApplySettingsAsync(next, CancellationToken.None));

        harness.Launcher.Count.Should().Be(2);
        harness.Launcher.Launches[0].Process.KillCount.Should().Be(1);
        harness.Launcher.Launches[0].Process.HasExited.Should().BeTrue();

        (await harness.ReadConfigAsync()).Should().Contain("username: wondarr-soulseek");

        // The supervisor is watching the replacement, and slskd answers again.
        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            TimeSpan.FromSeconds(1),
            60,
            "the restarted slskd to answer");

        // The credentials are configured but slskd has not logged in yet: healthy, and said so.
        var health = await harness.HealthCheck.CheckAsync(CancellationToken.None);
        health.Type.Should().Be(HealthCheckResult.Ok);
        health.Message.Should().Be("slskd 0.26.0.0 running; Soulseek: logged out");
    }

    [Fact]
    public async Task A_setting_slskd_reloads_is_written_without_restarting_it()
    {
        await using var harness = new SlskdHostHarness(WithCredentials(new SoulseekOptions()));

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        var next = WithCredentials(harness.Options);
        next.UploadSpeedLimitKib = 500;
        harness.CurrentOptions = next;

        await harness.Host.ApplySettingsAsync(next, CancellationToken.None);

        (await harness.ReadConfigAsync()).Should().Contain("speed_limit: 500");
        harness.Launcher.Count.Should().Be(1);
        harness.Launcher.Latest.KillCount.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_binary_is_reported_without_starting_anything()
    {
        await using var harness = new SlskdHostHarness(
            new SoulseekOptions { Username = null, Password = null },
            binaryExists: false);

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.BinaryMissing,
            "the missing binary to be noticed");

        harness.Launcher.Count.Should().Be(0);

        var health = await harness.HealthCheck.CheckAsync(CancellationToken.None);
        health.Type.Should().Be(HealthCheckResult.Warning);
        health.Message.Should().Be(
            $"slskd binary not found at {harness.Options.BinaryPath}; the Soulseek source is unavailable");
    }

    [Fact]
    public async Task External_mode_never_starts_a_process()
    {
        await using var harness = new SlskdHostHarness(
            new SoulseekOptions { Mode = SoulseekMode.External, Username = null, Password = null });

        await harness.StartAsync();

        harness.Launcher.Count.Should().Be(0);

        // The monitor owns the status in external mode: the supervisor writes nothing.
        harness.Status.Current.State.Should().Be(SlskdState.NotConfigured);
    }

    [Fact]
    public async Task Output_lines_from_the_child_reach_the_slskd_logger_category()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        harness.Launcher.Latest.Emit("Listening on port 5030");

        harness.Logs.For(SlskdHost.LogCategory).Should().Contain(record =>
            record.Level == LogLevel.Information && record.Message == "Listening on port 5030");
    }

    [Fact]
    public async Task Host_stop_kills_the_child_and_reports_it_stopped()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        await harness.StopAsync();

        var child = harness.Launcher.Latest;
        child.KillCount.Should().Be(1);
        child.LastKillWasTreeWide.Should().BeTrue();
        child.HasExited.Should().BeTrue();
        harness.Status.Current.State.Should().Be(SlskdState.Stopped);
    }

    /// <summary>A copy of <paramref name="source"/> with a Soulseek account configured.</summary>
    private static SoulseekOptions WithCredentials(SoulseekOptions source)
    {
        var copy = new SoulseekOptions
        {
            Username = "wondarr-soulseek",
            Password = "hunter2-not-a-real-password",
            BinaryPath = source.BinaryPath,
            DownloadsDir = source.DownloadsDir,
            IncompleteDir = source.IncompleteDir,
            SharedFolders = [.. source.SharedFolders],
        };

        return copy;
    }

    [Fact]
    public async Task A_settings_change_from_the_options_monitor_restarts_slskd_after_the_debounce()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        harness.RaiseOptionsChanged(WithCredentials(harness.Options));

        // Debounced: nothing happens within the first second.
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        harness.Launcher.Count.Should().Be(1);

        await harness.AdvanceUntilAsync(
            () => harness.Launcher.Count == 2,
            TimeSpan.FromSeconds(1),
            10,
            "the debounced restart");

        (await harness.ReadConfigAsync()).Should().Contain("username: wondarr-soulseek");
        harness.Launcher.Launches[0].Process.KillCount.Should().Be(1);
    }

    [Fact]
    public async Task A_settings_restart_right_after_a_crash_leaves_the_replacement_running()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        // The process dies and, before the supervisor has handled it, a restart-requiring setting
        // replaces it.
        harness.Launcher.Latest.ExitWith(9);
        var next = WithCredentials(harness.Options);
        harness.CurrentOptions = next;
        await harness.Host.ApplySettingsAsync(next, CancellationToken.None);

        var replacement = harness.Launcher.Latest;

        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            TimeSpan.FromSeconds(1),
            90,
            "the replacement to be watched");

        replacement.KillCount.Should().Be(0, "the crash path must only stop the process it was watching");
        replacement.HasExited.Should().BeFalse();
        harness.Launcher.Latest.Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task An_unpreparable_download_folder_crashes_the_state_instead_of_the_host_and_is_retried()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        // A file stands where the downloads directory should go, so Directory.CreateDirectory fails
        // on every platform — the same shape as an unwritable /data.
        var blocker = Path.Combine(Path.GetTempPath(), $"wondarr-slskd-blocked-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, string.Empty);

        try
        {
            harness.Options.DownloadsDir = Path.Combine(blocker, "downloads", "slskd");
            harness.Options.IncompleteDir = Path.Combine(harness.Options.DownloadsDir, "incomplete");

            // StartAsync would surface a faulted ExecuteAsync, so not throwing here is half the point.
            await harness.StartAsync();

            await SlskdHostHarness.AwaitAsync(
                () => harness.Status.Current.State == SlskdState.Crashed,
                "the preparation failure to be reported");

            var status = harness.Status.Current;
            status.LastError.Should().StartWith("Cannot prepare slskd: ");
            status.LastError.Should().Contain(blocker, "the .NET message names the path it could not prepare");
            harness.Launcher.Count.Should().Be(0, "nothing can be launched without its configuration");

            // The host is still running and has scheduled the usual backoff: five seconds later it
            // tries again (and fails again), rather than the host stopping in a restart loop.
            var firstAttemptAt = status.LastCheckedAt;
            harness.Time.Advance(TimeSpan.FromSeconds(4));
            await Task.Delay(50);
            harness.Status.Current.LastCheckedAt.Should().Be(firstAttemptAt, "the backoff has not elapsed yet");

            // Under load the host may register its backoff timer only after the clock was advanced,
            // so keep nudging fake time forward until the retry shows up instead of advancing once.
            await harness.AdvanceUntilAsync(
                () => harness.Status.Current.LastCheckedAt > firstAttemptAt,
                TimeSpan.FromSeconds(1),
                steps: 60,
                "the retry to be attempted");

            harness.Status.Current.State.Should().Be(SlskdState.Crashed);
            harness.Launcher.Count.Should().Be(0);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task A_duplicate_login_kick_is_recorded_and_a_later_login_clears_it()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        harness.Launcher.Latest.Emit(
            "[23:00:14 ERR] Disconnected from the Soulseek server: another client logged in using the same username");

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.DuplicateLogin,
            "the kick to be recorded");

        harness.Status.Current.LoginProblemAt.Should().Be(harness.Time.GetUtcNow());

        harness.Launcher.Latest.Emit("[23:05:36 INF] Logged in to the Soulseek server as wondarr-soulseek");

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.None,
            "the login to clear the kick");

        harness.Status.Current.LoginProblemAt.Should().BeNull();
    }

    [Fact]
    public async Task An_invalid_login_is_recorded_and_a_later_login_clears_it()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        harness.Launcher.Latest.Emit("[23:00:14 ERR] Disconnected from the Soulseek server: invalid username or password");

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.InvalidCredentials,
            "the rejected login to be recorded");

        harness.Launcher.Latest.Emit("[23:05:36 INF] Logged in to the Soulseek server as wondarr-soulseek");

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.None,
            "the login to clear the problem");
    }

    [Fact]
    public async Task An_empty_downloads_dir_is_reported_instead_of_stopping_the_host()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        // Not a storage error at all: this throws ArgumentException, which used to escape the
        // background service and stop the whole app.
        harness.Options.DownloadsDir = string.Empty;

        await harness.StartAsync();

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Crashed,
            "the empty setting to be reported");

        harness.Status.Current.LastError.Should().StartWith("Cannot prepare slskd: ");
        harness.Launcher.Count.Should().Be(0, "nothing can be launched without a valid configuration");
    }

    [Fact]
    public async Task A_shared_folder_that_cannot_be_created_is_skipped_and_slskd_still_starts()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        // A file stands where the shared folder's parent should go; an empty string is not a path at
        // all. Neither may stop slskd from starting.
        var blocker = Path.Combine(Path.GetTempPath(), $"wondarr-slskd-share-blocked-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, string.Empty);

        var good = Path.Combine(Path.GetTempPath(), $"wondarr-slskd-share-{Guid.NewGuid():N}");
        var unusable = Path.Combine(blocker, "shared");

        try
        {
            harness.Options.ShareLibrary = true;
            harness.Options.SharedFolders = [good, unusable, string.Empty];

            await harness.StartAsync();

            await SlskdHostHarness.AwaitAsync(
                () => harness.Status.Current.State == SlskdState.Running,
                "slskd to start with a shared folder it could not create");

            harness.Launcher.Count.Should().Be(1);
            Directory.Exists(good).Should().BeTrue("the folder that can be created is still shared out of the box");
            Directory.Exists(unusable).Should().BeFalse();

            harness.Logs.For(typeof(SlskdHost).FullName!).Should().Contain(record =>
                record.Level == LogLevel.Warning && record.Message.Contains(unusable));
        }
        finally
        {
            File.Delete(blocker);

            if (Directory.Exists(good))
            {
                Directory.Delete(good, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_settings_write_that_fails_while_slskd_runs_is_retried_and_never_fakes_a_crash()
    {
        await using var harness = new SlskdHostHarness(new SoulseekOptions { Username = null, Password = null });

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        var processId = harness.Status.Current.ProcessId;

        // A directory where slskd.yml belongs: every write of it fails from now on.
        File.Delete(harness.ConfigPath);
        Directory.CreateDirectory(harness.ConfigPath);

        var next = WithCredentials(harness.Options);
        harness.CurrentOptions = next;

        await harness.Host.ApplySettingsAsync(next, CancellationToken.None);

        var failed = harness.Status.Current;
        failed.State.Should().Be(SlskdState.Running, "a configuration that was not written is not a crash");
        failed.ProcessId.Should().Be(processId);
        failed.IsReachable.Should().BeTrue();
        failed.LastError.Should().StartWith("Settings not applied: ");
        harness.Launcher.Count.Should().Be(1, "nothing is restarted on a configuration that was not written");

        // The running poll must not wipe the reason while the change is still pending.
        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.LastCheckedAt > failed.LastCheckedAt,
            TimeSpan.FromSeconds(5),
            10,
            "the running poll");

        var polled = harness.Status.Current;
        polled.State.Should().Be(SlskdState.Running);
        polled.ProcessId.Should().Be(processId);
        polled.LastError.Should().StartWith("Settings not applied: ");

        // With the path free again, the supervisor's next cycle re-applies the pending change.
        Directory.Delete(harness.ConfigPath);

        await harness.AdvanceUntilAsync(
            () => harness.Launcher.Count == 2,
            TimeSpan.FromSeconds(5),
            20,
            "the pending settings to be re-applied");

        (await harness.ReadConfigAsync()).Should().Contain("username: wondarr-soulseek");
        harness.Launcher.Launches[0].Process.KillCount.Should().Be(1);

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "the replacement to answer");

        harness.Status.Current.LastError.Should().BeNull("the pending change was applied");
    }

    [Fact]
    public async Task A_login_problem_is_cleared_once_slskd_reports_a_logged_in_session()
    {
        await using var harness = new SlskdHostHarness(WithCredentials(new SoulseekOptions()));

        await harness.StartAsync();
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Running,
            "slskd to start");

        harness.Launcher.Latest.Emit(
            "[23:00:14 ERR] Disconnected from the Soulseek server: another client logged in using the same username");

        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.DuplicateLogin,
            "the kick to be recorded");

        // slskd reconnected on its own and now reports a logged-in session: the stale problem goes.
        harness.ApplicationState = new SlskdApplicationState
        {
            Version = new SlskdVersion { Current = "0.26.0.0" },
            Server = new SlskdServer { IsLoggedIn = true },
            User = new SlskdUser { Username = "wondarr-soulseek" },
        };

        await harness.AdvanceUntilAsync(
            () => harness.Status.Current.LoginProblem == SlskdLoginProblem.None,
            TimeSpan.FromSeconds(5),
            10,
            "the next poll to clear the problem that no longer holds");

        harness.Status.Current.LoginProblemAt.Should().BeNull();
    }
}
