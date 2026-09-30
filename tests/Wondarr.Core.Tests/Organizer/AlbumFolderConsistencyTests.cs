using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// What a file about to be placed may take from the folder it lands in, and — just as importantly —
/// what it may never take.
/// </summary>
public sealed class AlbumFolderConsistencyTests
{
    [Fact]
    public void Copies_every_folder_wide_field_that_differs_and_names_them()
    {
        var target = Context();
        var folder = Context();

        folder.AlbumTitle = "Discovery";
        folder.AlbumArtist = "Daft Punk";
        folder.Date = "2001-03-12";
        folder.MbReleaseId = "release-1";
        folder.MbReleaseGroupId = "group-2";
        folder.IsVariousArtists = true;
        folder.Kind = AlbumContextKind.Compilation;

        target.AlbumArtist = "Daft Punk";
        target.MbReleaseGroupId = "group-2";

        var changed = AlbumFolderConsistency.Align(target, folder);

        changed.Should().Equal("AlbumTitle", "Date", "MbReleaseId", "IsVariousArtists", "Kind");
        target.AlbumTitle.Should().Be("Discovery");
        target.Date.Should().Be("2001-03-12");
        target.MbReleaseId.Should().Be("release-1");
        target.IsVariousArtists.Should().BeTrue();
        target.Kind.Should().Be(AlbumContextKind.Compilation);
    }

    [Fact]
    public void Changes_nothing_and_reports_nothing_when_the_folder_already_agrees()
    {
        var target = Context();
        var folder = Context();

        AlbumFolderConsistency.Align(target, folder).Should().BeEmpty();

        target.Should().BeEquivalentTo(folder);
    }

    [Fact]
    public void Compares_text_ordinally_so_a_different_case_is_a_difference()
    {
        var target = Context();
        var folder = Context();

        folder.AlbumArtist = "DAFT PUNK";

        AlbumFolderConsistency.Align(target, folder).Should().Equal("AlbumArtist");
        target.AlbumArtist.Should().Be("DAFT PUNK");
    }

    [Fact]
    public void Never_touches_what_the_file_itself_is_or_where_its_cover_came_from()
    {
        var target = Context();
        var folder = Context();

        // Everything the folder says differently about the album at large.
        folder.AlbumTitle = "Discovery";
        folder.AlbumArtist = "Various Artists";
        folder.Date = "2001-03-12";
        folder.MbReleaseId = "release-1";
        folder.MbReleaseGroupId = "group-2";
        folder.IsVariousArtists = true;
        folder.Kind = AlbumContextKind.Compilation;

        // …and everything about this file that the folder has no say in.
        folder.TrackNo = 1;
        folder.DiscNo = 2;
        folder.TotalTracks = 14;
        folder.OriginalDate = "2000-01-01";
        folder.CoverUrl = "https://example.invalid/other.jpg";
        folder.AlbumKey = "another-album";
        folder.SongId = 99;
        folder.Sticky = false;

        AlbumFolderConsistency.Align(target, folder);

        target.TrackNo.Should().Be(7);
        target.DiscNo.Should().Be(1);
        target.TotalTracks.Should().Be(13);
        target.OriginalDate.Should().Be("1999-01-01");
        target.CoverUrl.Should().Be("https://example.invalid/front.jpg");
        target.AlbumKey.Should().Be("album-key");
        target.SongId.Should().Be(10);
        target.Sticky.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_missing_context()
    {
        var target = Context();

        var align = () => AlbumFolderConsistency.Align(target, null!);

        align.Should().Throw<ArgumentNullException>();
    }

    private static AlbumContext Context() => new()
    {
        SongId = 10,
        Kind = AlbumContextKind.Album,
        AlbumTitle = "Random Access Memories",
        AlbumArtist = "Daft Punk",
        AlbumKey = "album-key",
        MbReleaseGroupId = "group-2",
        TrackNo = 7,
        DiscNo = 1,
        TotalTracks = 13,
        Date = "2013-05-17",
        OriginalDate = "1999-01-01",
        CoverUrl = "https://example.invalid/front.jpg",
    };
}