using Wondarr.Sources.Slskd;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

public class SlskdRestartPolicyTests
{
    [Fact]
    public void Requires_a_restart_when_the_username_changed()
    {
        RequiresRestart(before => before.Username = "someone-else").Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_the_password_changed()
    {
        RequiresRestart(before => before.Password = "a-different-password").Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_the_shared_folders_changed()
    {
        RequiresRestart(before => before.SharedFolders = ["/data/media/other"]).Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_sharing_was_switched_off()
    {
        RequiresRestart(before => before.ShareLibrary = false).Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_the_upload_slots_changed()
    {
        RequiresRestart(before => before.UploadSlots = 3).Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_the_distributed_network_changed()
    {
        RequiresRestart(before => before.DistributedNetwork = false).Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_a_directory_changed()
    {
        RequiresRestart(before => before.DownloadsDir = "/data/downloads/other").Should().BeTrue();
        RequiresRestart(before => before.IncompleteDir = "/data/downloads/other/incomplete").Should().BeTrue();
    }

    [Fact]
    public void Requires_a_restart_when_the_web_port_or_binary_changed()
    {
        RequiresRestart(before => before.WebPort = 5031).Should().BeTrue();
        RequiresRestart(before => before.BinaryPath = "/opt/slskd/slskd-next").Should().BeTrue();
    }

    [Fact]
    public void Does_not_require_a_restart_when_the_upload_speed_limit_changed()
    {
        // slskd watches its YAML file and applies speed limits live (DEPLOYMENT §9.5).
        RequiresRestart(before => before.UploadSpeedLimitKib = 0).Should().BeFalse();
    }

    [Fact]
    public void Does_not_require_a_restart_when_the_listen_port_changed()
    {
        RequiresRestart(before => before.ListenPort = 50301).Should().BeFalse();
    }

    [Fact]
    public void Does_not_require_a_restart_for_identical_options()
    {
        SlskdRestartPolicy.RequiresRestart(Baseline(), Baseline()).Should().BeFalse();
    }

    [Fact]
    public void Does_not_require_a_restart_when_only_the_shared_folder_order_changed()
    {
        var before = Baseline();
        before.SharedFolders = ["/data/media/music", "/data/media/music-old"];
        var after = Baseline();
        after.SharedFolders = ["/data/media/music-old", "/data/media/music"];

        // The same folders are shared either way, so nothing the running slskd holds is stale.
        SlskdRestartPolicy.RequiresRestart(before, after).Should().BeFalse();
    }

    private static bool RequiresRestart(Action<SoulseekOptions> change)
    {
        var before = Baseline();
        var after = Baseline();
        change(after);

        return SlskdRestartPolicy.RequiresRestart(before, after);
    }

    private static SoulseekOptions Baseline() => new()
    {
        Username = "wondarr-soulseek",
        Password = "hunter2-not-a-real-password",
        SharedFolders = ["/data/media/music"],
    };
}
