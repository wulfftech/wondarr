using FluentAssertions;
using Wondarr.Core.Importing;
using Wondarr.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The gate in front of every recursive delete of a leftover folder. Each test works in a temp folder
/// of its own; the filesystem-root cases only ask the gate and never delete.
/// </summary>
public sealed class LeftoverFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wondarr-leftover-").FullName;

    [Fact]
    public void A_folder_with_files_and_subfolders_is_accepted_and_deleted_with_everything_in_it()
    {
        var folder = Path.Combine(_root, "grab");
        var nested = Write(Path.Combine(folder, "CD1", "track.flac"));
        Write(Path.Combine(folder, "cover.jpg"));
        Write(Path.Combine(folder, "track.flac.incomplete"));
        var beside = Write(Path.Combine(_root, "beside", "keep.flac"));

        LeftoverFolder.Refusal(folder).Should().BeNull();

        LeftoverFolder.Delete(folder);

        Directory.Exists(folder).Should().BeFalse();
        File.Exists(nested).Should().BeFalse();
        File.Exists(beside).Should().BeTrue("a folder beside it is not touched");
        Directory.Exists(_root).Should().BeTrue("neither is its parent");
    }

    [Fact]
    public void A_folder_that_is_not_there_is_not_refused_and_deleting_it_is_a_no_op()
    {
        var folder = Path.Combine(_root, "gone");

        LeftoverFolder.Refusal(folder).Should().BeNull();
        LeftoverFolder.Delete(folder);
    }

    [Fact]
    public void A_filesystem_root_is_refused()
    {
        LeftoverFolder.Refusal(Path.GetPathRoot(_root)!).Should().Contain("filesystem root");
        LeftoverFolder.Refusal(Path.GetPathRoot(_root)!, _root).Should().Contain("filesystem root");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("grab")]
    [InlineData("a/b")]
    public void A_folder_without_an_absolute_path_is_refused(string folder)
    {
        LeftoverFolder.Refusal(folder).Should().NotBeNull();
    }

    [Fact]
    public void A_path_with_dotdot_in_it_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "grab"));

        LeftoverFolder.Refusal(Path.Combine(_root, "grab", "..", "grab")).Should().Contain("'..'");
    }

    [SymlinkFact]
    public void A_folder_that_is_itself_a_link_is_refused_and_what_it_points_at_survives()
    {
        var real = Write(Path.Combine(_root, "real", "keep.flac"));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(real)!);

        LeftoverFolder.Refusal(link).Should().Contain("is a link");
        File.Exists(real).Should().BeTrue();
    }

    [SymlinkFact]
    public void A_link_anywhere_inside_the_folder_refuses_it()
    {
        var folder = Path.Combine(_root, "grab");
        Write(Path.Combine(folder, "one.flac"));
        var real = Write(Path.Combine(_root, "real", "keep.flac"));
        Directory.CreateDirectory(Path.Combine(folder, "CD1"));
        Directory.CreateSymbolicLink(Path.Combine(folder, "CD1", "link"), Path.GetDirectoryName(real)!);

        LeftoverFolder.Refusal(folder).Should().Contain("inside it is a link");
        File.Exists(real).Should().BeTrue();
    }

    [SymlinkFact]
    public void A_link_between_the_folder_and_the_stop_is_refused_and_one_above_the_stop_is_not_looked_at()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(Path.Combine(real, "wondarr", "guid"));
        var linked = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linked, real);
        var folder = Path.Combine(linked, "wondarr", "guid");

        // The link is above the stop, which is the folder the caller vouches for: not looked at.
        LeftoverFolder.Refusal(folder, linked).Should().BeNull();

        // The link is between the folder and the stop: refused.
        LeftoverFolder.Refusal(folder, _root).Should().Contain("is a link");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        return path;
    }
}
