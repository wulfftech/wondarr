using FluentAssertions;
using Wondarr.Sources.Torznab.Clients;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Clients;

/// <summary>
/// The gate in front of deleting a finished usenet job's folder. It is a pure function over paths: no
/// test here deletes anything, and none names a real root other than to be refused by it.
/// </summary>
public sealed class UsenetJobFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wondarr-jobfolder-").FullName;

    private string Complete => Path.Combine(_root, "complete");

    private string Job => Path.Combine(Complete, "Daft.Punk-Discovery-CD-FLAC-2001-GRP");

    private string Staging => Path.Combine(_root, "staging", "wondarr", "one");

    [Fact]
    public void Accepts_a_folder_below_the_completed_folder()
    {
        Directory.CreateDirectory(Job);

        UsenetJobFolder.Refusal(Job, Complete, Staging, Path.Combine(Staging, "03 - Digital Love.flac")).Should().BeNull();
    }

    [Fact]
    public void Accepts_a_folder_that_is_gone_because_there_is_nothing_to_clean()
    {
        UsenetJobFolder.Refusal(Job, Complete, Staging, null).Should().BeNull();
    }

    [Fact]
    public void Accepts_a_direct_child_with_or_without_trailing_separators()
    {
        Directory.CreateDirectory(Job);

        UsenetJobFolder.Refusal(Job + Path.DirectorySeparatorChar, Complete + Path.DirectorySeparatorChar, Staging, null).Should().BeNull();
        UsenetJobFolder.Refusal(Job, Complete + Path.DirectorySeparatorChar, Staging, null).Should().BeNull();
    }

    [Fact]
    public void Refuses_a_folder_deeper_than_a_direct_child()
    {
        var deeper = Path.Combine(Complete, "music", "Job");
        Directory.CreateDirectory(deeper);

        UsenetJobFolder.Refusal(deeper, Complete, Staging, null).Should().Contain("not directly inside");
        UsenetJobFolder.Refusal(deeper + Path.DirectorySeparatorChar, Complete + Path.DirectorySeparatorChar, Staging, null).Should().Contain("not directly inside");
    }

    [Fact]
    public void Refuses_the_artist_and_album_folders_a_sabnzbd_sorter_makes_which_other_jobs_share()
    {
        var album = Path.Combine(Complete, "Daft Punk", "Discovery (2001)");
        Directory.CreateDirectory(album);

        UsenetJobFolder.Refusal(album, Complete, Staging, null).Should().Contain("not directly inside");
    }

    [Fact]
    public void Refuses_the_completed_folder_itself_with_or_without_a_trailing_separator()
    {
        Directory.CreateDirectory(Complete);

        UsenetJobFolder.Refusal(Complete, Complete, Staging, null).Should().Contain("the completed folder itself");
        UsenetJobFolder.Refusal(Complete + Path.DirectorySeparatorChar, Complete, Staging, null).Should().Contain("the completed folder itself");
    }

    [Fact]
    public void Refuses_a_filesystem_root()
    {
        var root = Path.GetPathRoot(_root)!;

        UsenetJobFolder.Refusal(root, Complete, Staging, null).Should().Contain("filesystem root");
        UsenetJobFolder.Refusal(root, root, Staging, null).Should().Contain("filesystem root", "a root vouches for nothing");
    }

    [Fact]
    public void Refuses_a_completed_folder_that_is_a_filesystem_root()
    {
        var root = Path.GetPathRoot(_root)!;

        UsenetJobFolder.Refusal(Path.Combine(root, "anything"), root, Staging, null).Should().Contain("filesystem root");
    }

    [Fact]
    public void Refuses_a_folder_outside_the_completed_folder_even_when_the_names_share_a_prefix()
    {
        var sibling = Complete + "-old";
        Directory.CreateDirectory(sibling);

        UsenetJobFolder.Refusal(sibling, Complete, Staging, null).Should().Contain("outside the completed folder");
        UsenetJobFolder.Refusal(Path.Combine(_root, "elsewhere", "Job"), Complete, Staging, null).Should().Contain("outside the completed folder");
        UsenetJobFolder.Refusal(_root, Complete, Staging, null).Should().Contain("outside the completed folder", "the parent of the completed folder is not its job");
    }

    [Fact]
    public void Refuses_a_path_with_dotdot_in_it_even_when_it_resolves_to_a_folder_inside()
    {
        var sneaky = Path.Combine(Complete, "Job", "..", "Other");
        Directory.CreateDirectory(Path.Combine(Complete, "Other"));

        UsenetJobFolder.Refusal(sneaky, Complete, Staging, null).Should().Contain("'..'");
        UsenetJobFolder.Refusal(Path.Combine(Complete, "..", "..", "etc"), Complete, Staging, null).Should().Contain("'..'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Job")]
    [InlineData("relative/Job")]
    public void Refuses_a_folder_that_is_not_an_absolute_path(string folder)
    {
        UsenetJobFolder.Refusal(folder, Complete, Staging, null).Should().Contain("not an absolute path");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Refuses_when_the_client_does_not_say_where_it_finishes_jobs(string? completed)
    {
        UsenetJobFolder.Refusal(Job, completed, Staging, null).Should().Contain("does not say");
    }

    [Fact]
    public void Refuses_when_the_completed_folder_is_not_an_absolute_path()
    {
        UsenetJobFolder.Refusal(Job, "complete", Staging, null).Should().Contain("not an absolute path");
    }

    [Fact]
    public void Refuses_a_folder_that_holds_the_grabs_staging_folder_or_the_wanted_file()
    {
        var inside = Path.Combine(Job, "staging", "wondarr", "one");
        Directory.CreateDirectory(inside);

        UsenetJobFolder.Refusal(Job, Complete, inside, null).Should().Contain("staging folder");
        UsenetJobFolder.Refusal(Job, Complete, Staging, Path.Combine(Job, "03 - Digital Love.flac")).Should().Contain("wanted file");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
