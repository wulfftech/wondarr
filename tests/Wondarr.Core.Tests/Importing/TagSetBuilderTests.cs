using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>The tag set one imported file gets (LIBRARY_OUTPUT §7.3–7.5).</summary>
public sealed class TagSetBuilderTests
{
    private const string SyntheticAlbumId = "6e2f0c1a-5d43-4b9e-9f70-3ad2a5f58c11";

    [Fact]
    public void Maps_the_song_and_its_release_context()
    {
        var artist = new Artist
        {
            Name = "Daft Punk",
            MbArtistId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56",
        };

        var featured = new Artist { Name = "Pharrell Williams" };

        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk feat. Pharrell Williams",
            MbRecordingId = "a1b2c3d4-0000-4000-8000-000000000001",
            Isrcs = ["USQX91300974", "GB0000000002"],
        };

        var album = new AlbumContext
        {
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = "Daft Punk",
            AlbumKey = SyntheticAlbumId,
            MbReleaseId = "f2e4a1c0-1111-4222-8333-444455556666",
            MbReleaseGroupId = "4b3d5e6a-1c2b-4a5f-8e9d-0a1b2c3d4e5f",
            TrackNo = 8,
            TotalTracks = 13,
            DiscNo = 1,
            Date = "2013-05-17",
            OriginalDate = "2013-05-17",
        };

        var cover = new byte[] { 0xFF, 0xD8, 0xFF };

        var tags = TagSetBuilder.Build(
            song,
            album,
            [(artist, ArtistRole.Main), (featured, ArtistRole.Featured)],
            FakeDownloadVerifier.Passed().AcoustId,
            cover);

        tags.Title.Should().Be("Get Lucky");
        tags.Artist.Should().Be("Daft Punk feat. Pharrell Williams");
        tags.Artists.Should().Equal("Daft Punk", "Pharrell Williams");
        tags.AlbumArtist.Should().Be("Daft Punk");
        tags.Album.Should().Be("Random Access Memories");

        tags.TrackNumber.Should().Be(8);
        tags.TrackTotal.Should().Be(13);
        tags.DiscNumber.Should().Be(1);

        // One disc: the total is written, because a disc number says the release has discs at all.
        tags.DiscTotal.Should().Be(1);

        tags.Date.Should().Be("2013-05-17");
        tags.OriginalDate.Should().Be("2013-05-17");

        // Only the first ISRC: a tag holds one.
        tags.Isrc.Should().Be("USQX91300974");
        tags.MbRecordingId.Should().Be("a1b2c3d4-0000-4000-8000-000000000001");

        // The folder's id, not the chosen release's: stable, and the same for every file in the folder.
        tags.MbReleaseId.Should().Be(SyntheticAlbumId);
        tags.MbReleaseGroupId.Should().Be("4b3d5e6a-1c2b-4a5f-8e9d-0a1b2c3d4e5f");
        tags.MbArtistId.Should().Be("056e4f3e-d505-4dad-8ec1-d04f521cbb56");
        tags.MbAlbumArtistId.Should().Be("056e4f3e-d505-4dad-8ec1-d04f521cbb56");

        tags.ReleaseType.Should().Be("album");
        tags.ReleaseStatus.Should().Be("official");
        tags.Compilation.Should().BeFalse();

        tags.AcoustId.Should().Be("acoustid-1");
        tags.FrontCover.Should().BeSameAs(cover);
    }

    [Fact]
    public void Leaves_the_disc_total_unset_without_a_disc_number()
    {
        var artist = new Artist { Name = "Aphex Twin" };

        var tags = TagSetBuilder.Build(
            new Song { Title = "Xtal", ArtistCredit = "Aphex Twin" },
            new AlbumContext { AlbumTitle = "Selected Ambient Works", AlbumArtist = "Aphex Twin" },
            [(artist, ArtistRole.Main)],
            FakeDownloadVerifier.Passed().AcoustId,
            null);

        tags.DiscNumber.Should().BeNull();
        tags.DiscTotal.Should().BeNull();
        tags.ReleaseStatus.Should().Be("official");
        tags.FrontCover.Should().BeNull();
        tags.MbAlbumArtistId.Should().BeNull();
    }

    [Fact]
    public void Tags_a_pseudo_album_as_a_real_album()
    {
        var artist = new Artist { Name = "Burial" };

        var tags = TagSetBuilder.Build(
            new Song { Title = "Archangel", ArtistCredit = "Burial" },
            new AlbumContext
            {
                Kind = AlbumContextKind.PseudoSingles,
                AlbumTitle = "Burial — Singles",
                AlbumArtist = "Burial",

                // A synthetic pseudo-album id: there is no release, so the folder gets its own id.
                AlbumKey = SyntheticAlbumId,
            },
            [(artist, ArtistRole.Main)],
            FakeDownloadVerifier.Passed().AcoustId,
            null);

        tags.MbReleaseId.Should().Be(SyntheticAlbumId);
        tags.ReleaseType.Should().Be("album");
    }

    [Theory]
    [InlineData(AlbumContextKind.Single, "single")]
    [InlineData(AlbumContextKind.Ep, "ep")]
    [InlineData(AlbumContextKind.Compilation, "compilation")]
    [InlineData(AlbumContextKind.Album, "album")]
    public void Maps_the_context_kind_to_a_release_type(AlbumContextKind kind, string expected)
    {
        var tags = TagSetBuilder.Build(
            new Song { Title = "One More Time", ArtistCredit = "Daft Punk" },
            new AlbumContext { Kind = kind, AlbumTitle = "One More Time", AlbumArtist = "Daft Punk" },
            [],
            FakeDownloadVerifier.Passed().AcoustId,
            null);

        tags.ReleaseType.Should().Be(expected);
    }

    [Fact]
    public void Tags_a_various_artists_release_as_a_compilation()
    {
        var artist = new Artist
        {
            Name = "Various Artists",
            MbArtistId = "11111111-1111-4111-8111-111111111111",
        };

        var tags = TagSetBuilder.Build(
            new Song { Title = "Teardrop", ArtistCredit = "Massive Attack" },
            new AlbumContext
            {
                Kind = AlbumContextKind.Compilation,
                AlbumTitle = "Late Night Tales",
                AlbumArtist = "Various Artists",
                AlbumKey = SyntheticAlbumId,
                IsVariousArtists = true,
            },
            [(artist, ArtistRole.Main)],
            FakeDownloadVerifier.Passed().AcoustId,
            null);

        // Plex reads a compilation from the album-artist MBID, not from the iTunes flag alone.
        tags.MbAlbumArtistId.Should().Be(TagSetBuilder.VariousArtistsMbId);
        tags.Compilation.Should().BeTrue();
        tags.ReleaseType.Should().Be("compilation");
        tags.AlbumArtist.Should().Be("Various Artists");
    }
}
