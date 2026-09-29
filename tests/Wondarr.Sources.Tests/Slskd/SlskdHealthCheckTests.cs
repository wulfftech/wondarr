using Wondarr.Core.HealthCheck;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdHealthCheckTests
{
    [Fact]
    public async Task Running_without_credentials_is_healthy_and_says_so()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(SlskdState.Running, Version: "0.26.0.0", IsReachable: true),
            new SoulseekOptions { Username = null, Password = null });

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("slskd 0.26.0.0 running; Soulseek: not configured");
    }

    [Fact]
    public async Task Running_and_logged_in_names_the_account()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(
                SlskdState.Running,
                Version: "0.26.0.0",
                IsReachable: true,
                IsLoggedIn: true,
                SoulseekUsername: "wondarr-soulseek"),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("slskd 0.26.0.0 running; Soulseek: logged in as wondarr-soulseek");
    }

    [Fact]
    public async Task Running_with_credentials_that_are_not_logged_in_is_still_healthy()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(SlskdState.Running, Version: "0.26.0.0", IsReachable: true),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("slskd 0.26.0.0 running; Soulseek: logged out");
    }

    [Fact]
    public async Task A_rejected_login_is_an_error_naming_the_account_but_never_the_password()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(
                SlskdState.Running,
                Version: "0.26.0.0",
                IsReachable: true,
                SoulseekUsername: "wondarr-soulseek",
                LoginProblem: SlskdLoginProblem.InvalidCredentials,
                LoginProblemAt: DateTimeOffset.UnixEpoch),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Be("Soulseek rejected the login for wondarr-soulseek: invalid username or password");
        result.Message.Should().NotContain("hunter2-not-a-real-password");
    }

    [Fact]
    public async Task A_duplicate_login_kick_is_an_error_that_says_what_to_do()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(
                SlskdState.Running,
                Version: "0.26.0.0",
                IsReachable: true,
                SoulseekUsername: "wondarr-soulseek",
                LoginProblem: SlskdLoginProblem.DuplicateLogin,
                LoginProblemAt: DateTimeOffset.UnixEpoch),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Be(
            "Soulseek disconnected: another client logged in as wondarr-soulseek. "
            + "Use a dedicated Soulseek account for Wondarr.");
    }

    [Fact]
    public async Task A_login_problem_falls_back_to_the_configured_username()
    {
        // slskd reports no username when the account never got as far as logging in.
        var result = await CheckAsync(
            new SlskdStatusSnapshot(
                SlskdState.Running,
                IsReachable: true,
                LoginProblem: SlskdLoginProblem.InvalidCredentials),
            Configured());

        result.Message.Should().Be("Soulseek rejected the login for wondarr-soulseek: invalid username or password");
    }

    [Fact]
    public async Task Running_but_unreachable_is_an_error()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(
                SlskdState.Running,
                IsReachable: false,
                LastError: "slskd is not answering"),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("slskd is not answering");
    }

    [Theory]
    [InlineData(SlskdState.Starting)]
    [InlineData(SlskdState.Restarting)]
    public async Task Starting_and_restarting_are_notices(SlskdState state)
    {
        var result = await CheckAsync(new SlskdStatusSnapshot(state), Configured());

        result.Type.Should().Be(HealthCheckResult.Notice);
    }

    [Fact]
    public async Task A_crashed_slskd_is_an_error_that_repeats_why()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(SlskdState.Crashed, LastError: "slskd exited unexpectedly (exit code 9)"),
            Configured());

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Message.Should().Contain("exit code 9");
    }

    [Fact]
    public async Task A_missing_binary_is_a_warning_naming_the_path()
    {
        var options = Configured();
        options.BinaryPath = "/opt/slskd/slskd";

        var result = await CheckAsync(new SlskdStatusSnapshot(SlskdState.BinaryMissing), options);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be("slskd binary not found at /opt/slskd/slskd; the Soulseek source is unavailable");
    }

    [Fact]
    public async Task External_mode_is_healthy_because_Wondarr_owns_no_process()
    {
        var result = await CheckAsync(
            new SlskdStatusSnapshot(SlskdState.Disabled),
            new SoulseekOptions { Mode = SoulseekMode.External });

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("External slskd mode (checked in Phase 5)");
    }

    [Fact]
    public async Task Every_result_is_reported_under_the_slskd_name()
    {
        var result = await CheckAsync(new SlskdStatusSnapshot(SlskdState.Disabled), Configured());

        result.Source.Should().Be(SlskdHealthCheck.CheckName);
        result.WikiUrl.Should().BeNull();
    }

    private static SoulseekOptions Configured()
    {
        var options = new SoulseekOptions
        {
            Username = "wondarr-soulseek",
            Password = "hunter2-not-a-real-password",
        };

        return options;
    }

    private static Task<HealthCheck> CheckAsync(SlskdStatusSnapshot snapshot, SoulseekOptions options)
    {
        var status = new SlskdStatus();
        status.Set(snapshot);

        var check = new SlskdHealthCheck(status, SlskdTestData.Monitor(options));

        return check.CheckAsync(CancellationToken.None);
    }
}
