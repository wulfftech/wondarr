using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The shared tag → name → place step. The import pipeline's own tests cover what it does with each
/// outcome; these pin down the organizer itself, above all that a kept source is never touched.
/// </summary>
public sealed class LibraryOrganizerTests : IDisposable
{
    private static readonly byte[] SourceBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-organizer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTagWriter _tagWriter = new();
    private readonly FakeFilePlacer _placer = new();
    private readonly FakeCoverFetcher _covers = new();

    public LibraryOrganizerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        Directory.CreateDirectory(Path.Combine(_root, "source"));
    }

    private string LibraryRoot => Path.Combine(_root, "library");

    private string StagingFolder => Path.Combine(LibraryRoot, LibraryOrganizer.StagingFolderName);

    [Fact]
    public async Task Tags_and_moves_the_source_itself_when_it_is_not_kept()
    {
        var source = WriteSource();
        var request = Request(source, keepSource: false, replaces: "/music/old.mp3");

        var result = await Organizer().OrganizeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Failure.Should().Be(OrganizeFailure.None);
        result.TagsWritten.Should().ContainKey("Title");
        _tagWriter.Writes.Should().ContainSingle().Which.Path.Should().Be(source);
        _tagWriter.Writes[0].Tags.AcoustId.Should().Be("acoustid-7");
        _tagWriter.Writes[0].Tags.FrontCover.Should().NotBeNull();

        var placement = _placer.Requests.Should().ContainSingle().Subject;
        placement.SourcePath.Should().Be(source);
        placement.Mode.Should().Be(TransferMode.Move);
        placement.ReplacesPath.Should().Be("/music/old.mp3");
        placement.RelativePathWithoutExtension.Should().Be("Daft Punk/Random Access Memories/08 - Get Lucky");
        result.FinalPath.Should().Be(FakeFilePlacer.TargetOf(placement));
    }

    [Fact]
    public async Task A_kept_source_is_copied_tagged_and_placed_but_never_touched()
    {
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: true), CancellationToken.None);

        result.Success.Should().BeTrue();
        var tagged = _tagWriter.Writes.Should().ContainSingle().Subject.Path;
        tagged.Should().NotBe(source);
        Path.GetDirectoryName(tagged).Should().Be(StagingFolder);
        tagged.Should().EndWith(".mp3");
        _placer.Requests.Should().ContainSingle().Which.SourcePath.Should().Be(tagged);

        File.ReadAllBytes(source).Should().Equal(SourceBytes);

        // The fake placer does not move the copy, so the organizer cleans it up: a staged file never lingers.
        File.Exists(tagged).Should().BeFalse();
    }

    [Fact]
    public async Task A_tagging_failure_places_nothing_and_leaves_no_staged_copy()
    {
        var source = WriteSource();
        _tagWriter.Success = false;
        _tagWriter.Error = "The file is not readable audio.";

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: true), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Failure.Should().Be(OrganizeFailure.Tagging);
        result.Error.Should().Be("The file is not readable audio.");
        result.FinalPath.Should().BeNull();
        _placer.Requests.Should().BeEmpty();
        Directory.EnumerateFiles(StagingFolder).Should().BeEmpty();
        File.ReadAllBytes(source).Should().Equal(SourceBytes);
    }

    [Fact]
    public async Task A_placement_failure_reports_the_placer_error_and_what_was_tagged()
    {
        var source = WriteSource();
        _placer.Success = false;
        _placer.Error = "The library root is not writable.";

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: true), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Failure.Should().Be(OrganizeFailure.Placement);
        result.Error.Should().Be("The library root is not writable.");
        result.TagsWritten.Should().ContainKey("Title");
        Directory.EnumerateFiles(StagingFolder).Should().BeEmpty();
        File.ReadAllBytes(source).Should().Equal(SourceBytes);
    }

    [Fact]
    public void The_primary_artist_is_the_one_the_song_points_at_then_the_first_main_credit()
    {
        var featured = new Artist { Id = 1, Name = "Pharrell Williams" };
        var main = new Artist { Id = 2, Name = "Daft Punk" };
        var other = new Artist { Id = 3, Name = "Nile Rodgers" };

        LibraryOrganizer.PrimaryArtist(new Song { PrimaryArtistId = 3 }, [(featured, ArtistRole.Featured), (main, ArtistRole.Main), (other, ArtistRole.Featured)])
            .Should().BeSameAs(other);
        LibraryOrganizer.PrimaryArtist(new Song { PrimaryArtistId = 99 }, [(featured, ArtistRole.Featured), (main, ArtistRole.Main)])
            .Should().BeSameAs(main);
        LibraryOrganizer.PrimaryArtist(new Song { PrimaryArtistId = 99 }, [(featured, ArtistRole.Featured)])
            .Should().BeSameAs(featured);

        var none = () => LibraryOrganizer.PrimaryArtist(new Song { Id = 5 }, []);
        none.Should().Throw<InvalidOperationException>().WithMessage("*5*no credited artist*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LibraryOrganizer Organizer() =>
        new(_tagWriter, _placer, _covers, new DiskOperations(), NullLogger<LibraryOrganizer>.Instance);

    private string WriteSource()
    {
        var path = Path.Combine(_root, "source", "Daft Punk - Get Lucky.mp3");
        File.WriteAllBytes(path, SourceBytes);

        return path;
    }

    private OrganizeRequest Request(string source, bool keepSource, string? replaces = null)
    {
        var artist = new Artist { Id = 1, Name = "Daft Punk", SortName = "Daft Punk" };
        var song = new Song
        {
            Id = 10,
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = 1,
            MbRecordingId = "b1a9c0e9-d987-4042-ae91-78d6a3267d69",
        };
        var album = new AlbumContext
        {
            SongId = 10,
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = "Daft Punk",
            AlbumKey = "5000a285-b67e-4cfc-b54b-2b98f1810d2e",
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = "2013-05-17",
            CoverUrl = "https://coverartarchive.org/release/5000a285-b67e-4cfc-b54b-2b98f1810d2e/front-1200",
        };
        var library = new Library
        {
            Name = "Music",
            RootPath = LibraryRoot,
            Layout = LibraryLayout.Plexamp,
        };
        var media = new MediaInfo("mp3", "mp3", 320, 44100, null, 2, 248_000, false, SourceBytes.Length);
        var quality = new Quality { Id = 13, Name = "MP3-320" };

        return new OrganizeRequest(
            song,
            album,
            [(artist, ArtistRole.Main)],
            library,
            source,
            "mp3",
            media,
            quality,
            "slskd",
            "acoustid-7",
            keepSource,
            replaces);
    }
}
