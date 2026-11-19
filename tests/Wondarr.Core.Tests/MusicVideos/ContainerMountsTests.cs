using FluentAssertions;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Core.Tests.MusicVideos;

/// <summary>The container rule: a folder on the container's own root filesystem is refused; one on a mount passes.</summary>
public sealed class ContainerMountsTests
{
    /// <summary>The mount table of the production container: overlay root, two data mounts and Docker's own.</summary>
    internal static readonly string[] Sample =
    [
        "1 0 0:50 / / rw,relatime - overlay overlay rw,lowerdir=/x",
        "2 1 0:51 / /proc rw,nosuid - proc proc rw",
        "3 1 0:52 / /dev rw,nosuid - tmpfs tmpfs rw",
        "4 3 0:53 / /dev/pts rw,nosuid - devpts devpts rw",
        "5 1 0:54 / /sys ro,nosuid - sysfs sysfs ro",
        "6 1 8:1 /var/lib/docker/containers/abc/hosts /etc/hosts rw - ext4 /dev/sda1 rw",
        "7 1 8:1 /var/lib/docker/containers/abc/resolv.conf /etc/resolv.conf rw - ext4 /dev/sda1 rw",
        "8 1 8:1 /srv/wondarr/config /config rw - ext4 /dev/sda1 rw",
        "9 1 0:60 / /data/music rw,relatime - cifs //nas/music rw",
        "10 1 8:1 /srv/downloads /data/downloads rw - ext4 /dev/sda1 rw",
    ];

    [Fact]
    public void A_folder_under_a_mount_passes_and_one_on_the_container_root_is_refused()
    {
        var mounts = InContainer(Sample);

        mounts.IsOnContainerRoot("/data/music/x").Should().BeFalse();
        mounts.IsOnContainerRoot("/data/music").Should().BeFalse();
        mounts.IsOnContainerRoot("/data/downloads/videos").Should().BeFalse();
        mounts.IsOnContainerRoot("/data/music-videos").Should().BeTrue("a sibling of the mount shares only its prefix");
        mounts.IsOnContainerRoot("/data").Should().BeTrue();
        mounts.IsOnContainerRoot("/srv/videos").Should().BeTrue();
    }

    [Fact]
    public void The_refusal_says_how_to_mount_a_folder()
    {
        var problem = InContainer(Sample).Problem("/data/music-videos");

        problem.Should().Contain("/data/music-videos is inside the container")
            .And.Contain("lost when the container is recreated")
            .And.Contain("/data/music");
        InContainer(Sample).Problem("/data/music/videos").Should().BeNull();
    }

    [Fact]
    public void The_rule_is_off_outside_a_container()
    {
        var mounts = new ContainerMounts(_ => false, () => Sample);

        mounts.InContainer.Should().BeFalse();
        mounts.IsOnContainerRoot("/data/music-videos").Should().BeFalse();
        mounts.DataMounts().Should().BeEmpty();
    }

    [Fact]
    public void The_rule_is_off_on_windows_even_with_a_marker_file()
    {
        var mounts = new ContainerMounts(_ => true, () => Sample, isLinux: false);

        mounts.IsOnContainerRoot("/data/music-videos").Should().BeFalse();
    }

    [Theory]
    [InlineData("/.dockerenv")]
    [InlineData("/run/.containerenv")]
    public void Either_marker_file_means_a_container(string marker)
    {
        new ContainerMounts(path => path == marker, () => Sample).IsOnContainerRoot("/data/music-videos").Should().BeTrue();
    }

    [Fact]
    public void An_unreadable_mount_table_leaves_the_rule_out_of_the_way()
    {
        new ContainerMounts(_ => true, () => null).IsOnContainerRoot("/data/music-videos").Should().BeFalse();
    }

    [Fact]
    public void Escaped_spaces_in_a_mount_point_are_decoded()
    {
        var mounts = InContainer(
        [
            "1 0 0:50 / / rw - overlay overlay rw",
            "2 1 0:60 / /data/my\\040videos rw - cifs //nas/v rw",
        ]);

        mounts.IsOnContainerRoot("/data/my videos/clips").Should().BeFalse();
        mounts.IsOnContainerRoot("/data/my").Should().BeTrue();
        mounts.DataMounts().Should().Equal("/data/my videos");
    }

    [Fact]
    public void The_data_mounts_leave_out_the_kernel_docker_files_and_the_root()
    {
        var mounts = new ContainerMounts(
            path => path == "/.dockerenv",
            () => Sample,
            directoryExists: path => path is not ("/etc/hosts" or "/etc/resolv.conf"));

        mounts.DataMounts().Should().Equal("/config", "/data/downloads", "/data/music");
    }

    [Fact]
    public void A_mount_that_is_a_file_is_not_a_data_mount()
    {
        var mounts = new ContainerMounts(
            _ => true,
            () => ["1 0 0:50 / / rw - overlay overlay rw", "2 1 8:1 /f /data/a.txt rw - ext4 /dev/sda1 rw"],
            directoryExists: _ => false);

        mounts.DataMounts().Should().BeEmpty();
    }

    [Fact]
    public void Dot_dot_in_a_path_cannot_climb_out_of_a_mount()
    {
        InContainer(Sample).IsOnContainerRoot("/data/music/../music-videos").Should().BeTrue();
    }

    [Fact]
    public void A_folder_on_a_tmpfs_is_refused_and_the_tmpfs_is_no_data_mount()
    {
        var mounts = InContainer(
        [
            "1 0 0:50 / / rw - overlay overlay rw",
            "2 1 0:70 / /run/secrets ro - tmpfs tmpfs ro",
            "3 1 0:71 / /dev/shm rw - tmpfs shm rw",
            "4 1 0:60 / /data rw - cifs //nas/d rw",
        ]);

        mounts.IsOnContainerRoot("/run/secrets/v").Should().BeTrue();
        mounts.IsOnContainerRoot("/dev/shm/v").Should().BeTrue();
        mounts.Problem("/run/secrets/v").Should().Contain("is inside the container");
        mounts.IsOnContainerRoot("/data/v").Should().BeFalse();
        mounts.DataMounts().Should().Equal("/data");
    }

    [Fact]
    public void A_nested_mount_decides_by_its_own_type()
    {
        var mounts = InContainer(
        [
            "1 0 0:50 / / rw - overlay overlay rw",
            "2 1 0:60 / /data rw - cifs //nas/d rw",
            "3 2 0:61 / /data/videos rw - nfs4 nas:/v rw",
            "4 2 0:62 / /data/scratch rw - tmpfs tmpfs rw",
        ]);

        mounts.IsOnContainerRoot("/data/videos/x").Should().BeFalse("the longer NFS mount is the one it lives on");
        mounts.IsOnContainerRoot("/data/scratch/x").Should().BeTrue("the longer tmpfs mount is not persistent");
        mounts.IsOnContainerRoot("/data/other").Should().BeFalse();
        mounts.DataMounts().Should().Equal("/data", "/data/videos");
    }

    private static ContainerMounts InContainer(string[] lines) => new(path => path == "/.dockerenv", () => lines);
}
