using Compilarr.Core.HealthCheck;
using Compilarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Compilarr.Sources.Tests.Slskd;

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

        (await harness.ReadConfigAsync()).Should().Contain("username: compilarr-soulseek");

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
        await SlskdHostHarness.AwaitAsync(
            () => harness.Status.Current.State == SlskdState.Disabled,
            "the supervisor to stand down");

        harness.Launcher.Count.Should().Be(0);

        var health = await harness.HealthCheck.CheckAsync(CancellationToken.None);
        health.Type.Should().Be(HealthCheckResult.Ok);
        health.Message.Should().Be("External slskd mode (checked in Phase 5)");
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
            Username = "compilarr-soulseek",
            Password = "hunter2-not-a-real-password",
            BinaryPath = source.BinaryPath,
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

        (await harness.ReadConfigAsync()).Should().Contain("username: compilarr-soulseek");
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
}
