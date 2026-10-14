using FluentAssertions;
using Wondarr.Core.Configuration;
using Wondarr.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Configuration;

/// <summary>Which paths a folder setting may not name, and how a path is compared.</summary>
public sealed class ProtectedPathsTests
{
    private static readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "wondarr-protected-config");
    private static readonly WondarrPaths Paths = new(ConfigDir);

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void The_file_system_root_is_refused(string suffix)
    {
        var root = Path.GetPathRoot(ConfigDir)! + suffix;

        ProtectedPaths.Reason(root, Paths).Should().Be("is the root of the file system");
    }

    [Fact]
    public void The_data_directory_is_refused_with_or_without_a_trailing_separator()
    {
        ProtectedPaths.Reason(ConfigDir, Paths).Should().Be("is Wondarr's data directory");
        ProtectedPaths.Reason(ConfigDir + Path.DirectorySeparatorChar, Paths).Should().Be("is Wondarr's data directory");
    }

    [Fact]
    public void A_path_that_resolves_into_the_data_directory_through_dot_dot_is_refused()
    {
        var sneaky = Path.Combine(ConfigDir, "logs", "..", "backups");

        ProtectedPaths.Reason(sneaky, Paths).Should().Be("is inside Wondarr's data directory");
        ProtectedPaths.Reason(Path.Combine(ConfigDir, "..", "elsewhere"), Paths).Should().BeNull();
    }

    [Fact]
    public void A_sibling_that_shares_a_prefix_is_not_inside()
    {
        ProtectedPaths.Reason(ConfigDir + "-music", Paths).Should().BeNull();
        ProtectedPaths.Reason(ConfigDir + "2", Paths).Should().BeNull();
    }

    [LinuxFact]
    public void Case_is_kept_on_linux()
    {
        ProtectedPaths.Reason(ConfigDir.ToUpperInvariant(), Paths).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_value_is_left_to_the_settings_own_rules(string? value)
    {
        ProtectedPaths.Reason(value, Paths).Should().BeNull();
        ProtectedPaths.Failure("soulseek.downloads_dir", value, Paths).Should().BeNull();
    }

    [Fact]
    public void The_message_starts_with_the_yaml_key_and_names_the_reason()
    {
        ProtectedPaths.Failure("soulseek.downloads_dir", ConfigDir, Paths)
            .Should().StartWith("soulseek.downloads_dir: ").And.Contain("data directory");

        ProtectedPaths.Failure("soulseek.shared_folders", ConfigDir, Paths, sharing: true)
            .Should().StartWith("soulseek.shared_folders: sharing ").And.Contain("would publish Wondarr's API key and passwords");
    }
}
