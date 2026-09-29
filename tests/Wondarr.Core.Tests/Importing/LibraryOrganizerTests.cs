using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Media;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// The shared tag → name → place step. The import pipeline's own tests cover what it does with each
/// outcome; these pin down the organizer itself: that a kept source is never touched, that the folder
/// the file lands in decides its album-wide tags, and that the folder gets its art exactly once.
/// </summary>
public sealed class LibraryOrganizerTests : IDisposable
{
    private const string AlbumKey = "5000a285-b67e-4cfc-b54b-2b98f1810d2e";

    private static readonly byte[] SourceBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private static readonly byte[] ExistingCover = [0xFF, 0xD8, 0xFF, 0x99, 0x99];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-organizer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTagWriter _tagWriter = new();
    private readonly FakeFilePlacer _placer = new();
    private readonly FakeCoverFetcher _covers = new();
    private readonly FakeCoverImageProcessor _coverProcessor = new();
    private readonly ImportOptions _importOptions = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly WondarrDbContext _context;

    /// <summary>The album context the last <see cref="Request"/> built, so a test can see what the
    /// organizer did to the caller's own entity.</summary>
    private AlbumContext _album = null!;

    public LibraryOrganizerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        Directory.CreateDirectory(Path.Combine(_root, "source"));

        _database.MigrateAsync(TimeProvider.System).GetAwaiter().GetResult();
        _context = _database.CreateContext(TimeProvider.System);

        // The fake placer never touches the disk, so the folder its target names is made here: the
        // organizer writes cover.jpg beside the placed file, exactly as a real placement would leave it.
        _placer.OnPlaceAsync = request =>
        {
            CreateTargetFolder(request);

            return Task.CompletedTask;
        };
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
        result.CoverJpgPath.Should().BeNull();
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
        result.CoverJpgPath.Should().BeNull();
        CoverJpg().Should().BeNull();
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

    [Theory]
    [InlineData(LibraryLayout.Plexamp, true)]
    [InlineData(LibraryLayout.ArtistAlbum, true)]
    [InlineData(LibraryLayout.Flat, false)]
    [InlineData(LibraryLayout.Artist, false)]
    public async Task Writes_cover_jpg_only_for_a_layout_with_an_album_folder(LibraryLayout layout, bool expected)
    {
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, layout: layout),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);

        if (expected)
        {
            CoverJpg().Should().NotBeNull();
            File.ReadAllBytes(CoverJpg()!).Should().Equal(_covers.Bytes);
            result.CoverJpgPath.Should().Be(CoverJpg());
        }
        else
        {
            CoverJpg().Should().BeNull();
            result.CoverJpgPath.Should().BeNull();
        }
    }

    [Fact]
    public async Task Writes_no_cover_jpg_when_the_library_turns_it_off()
    {
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, sidecarOptions: "{\"coverJpg\":false}"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        CoverJpg().Should().BeNull();
        result.CoverJpgPath.Should().BeNull();
    }

    [Fact]
    public async Task Never_replaces_a_cover_jpg_that_is_already_there()
    {
        var source = WriteSource();

        _placer.OnPlaceAsync = request =>
        {
            var folder = CreateTargetFolder(request);
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), ExistingCover);

            return Task.CompletedTask;
        };

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.ReadAllBytes(CoverJpg()!).Should().Equal(ExistingCover);
        result.CoverJpgPath.Should().BeNull();
    }

    [Fact]
    public async Task Writes_no_cover_jpg_for_a_cover_that_could_not_be_converted_to_a_jpeg()
    {
        var source = WriteSource();
        _covers.Bytes = PngBytes;

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        CoverJpg().Should().BeNull();
        result.CoverJpgPath.Should().BeNull();
        _tagWriter.Writes[0].Tags.FrontCover.Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Embeds_the_cover_the_processor_returned_at_the_library_s_own_edge()
    {
        var source = WriteSource();
        var processed = new byte[] { 0xFF, 0xD8, 0xFF, 0x42, 0x42, 0x42 };
        _coverProcessor.Result = processed;

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, sidecarOptions: "{\"coverMaxEdge\":900}"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _coverProcessor.MaxEdge.Should().Be(900);
        _tagWriter.Writes.Should().ContainSingle().Which.Tags.FrontCover.Should().Equal(processed);
        File.ReadAllBytes(CoverJpg()!).Should().Equal(processed);
    }

    [Fact]
    public async Task Fetches_no_cover_at_all_when_the_album_has_no_cover_url()
    {
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, coverUrl: null),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _coverProcessor.Inputs.Should().BeEmpty();
        _tagWriter.Writes[0].Tags.FrontCover.Should().BeNull();
        CoverJpg().Should().BeNull();
    }

    [Fact]
    public async Task Takes_the_folder_s_album_date_when_a_sibling_disagrees()
    {
        var sibling = await SeedSiblingAsync(AlbumKey, date: "2012-01-01");
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        sibling.Should().BePositive();

        // The caller tracks the album context, so the correction is what its own save writes.
        _album.Date.Should().Be("2012-01-01");
        _tagWriter.Writes[0].Tags.Date.Should().Be("2012-01-01");
    }

    [Fact]
    public async Task Leaves_the_album_alone_when_the_folder_already_agrees()
    {
        await SeedSiblingAsync(AlbumKey, date: "2013-05-17");
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _album.Date.Should().Be("2013-05-17");
        _album.AlbumTitle.Should().Be("Random Access Memories");
    }

    [Fact]
    public async Task Ignores_a_sibling_in_another_library()
    {
        await SeedSiblingAsync(AlbumKey, date: "2012-01-01");
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, libraryId: 7),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _album.Date.Should().Be("2013-05-17");
    }

    [Fact]
    public async Task Ignores_a_sibling_that_has_no_file_yet()
    {
        await SeedSiblingAsync(AlbumKey, date: "2012-01-01", withFile: false);
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _album.Date.Should().Be("2013-05-17");
    }

    [Fact]
    public async Task Ignores_a_sibling_that_is_filed_under_another_album_key()
    {
        await SeedSiblingAsync("00000000-0000-0000-0000-000000000000", date: "2012-01-01");
        var source = WriteSource();

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _album.Date.Should().Be("2013-05-17");
    }

    public void Dispose()
    {
        _context.Dispose();
        _database.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The album folder's cover, wherever the layout put it, or <see langword="null"/>.</summary>
    private string? CoverJpg() =>
        Directory.Exists(LibraryRoot)
            ? Directory.EnumerateFiles(LibraryRoot, LibraryOrganizer.CoverJpgName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>Makes the folder the fake placer's target names, and returns it.</summary>
    private static string CreateTargetFolder(PlacementRequest request)
    {
        var folder = Path.GetDirectoryName(FakeFilePlacer.TargetOf(request)) ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>Inserts a song with an album context, and a file of it unless the test says otherwise.</summary>
    private async Task<long> SeedSiblingAsync(
        string albumKey,
        string? date,
        long libraryId = 1,
        bool withFile = true,
        string albumTitle = "Random Access Memories",
        string albumArtist = "Daft Punk",
        int? trackNo = 8,
        int? discNo = 1)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        _context.Artists.Add(artist);
        await _context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = libraryId,
            AddedBy = "api",
        };

        _context.Songs.Add(song);
        await _context.SaveChangesAsync();

        if (withFile)
        {
            _context.SongFiles.Add(new SongFile
            {
                SongId = song.Id,
                Path = $"{LibraryRoot}/Daft Punk/Random Access Memories/08 - Get Lucky.flac",
                Size = 30_000_000,
                Codec = "flac",
                Container = "flac",
                QualityId = 36,
                SourceType = SourceTypes.Soulseek,
                ImportedAt = DateTime.UtcNow,
            });
        }

        _context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = AlbumContextKind.Album,
            AlbumTitle = albumTitle,
            AlbumArtist = albumArtist,
            AlbumKey = albumKey,
            MbReleaseGroupId = "4b3d5e6a-1c2b-4a5f-8e9d-0a1b2c3d4e5f",
            TrackNo = trackNo,
            DiscNo = discNo,
            TotalTracks = 13,
            Date = date,
        });

        await _context.SaveChangesAsync();

        return song.Id;
    }

    private LibraryOrganizer Organizer() =>
        new(
            _tagWriter,
            _placer,
            _covers,
            new DiskOperations(),
            _context,
            _coverProcessor,
            new TestOptionsMonitor<ImportOptions>(_importOptions),
            NullLogger<LibraryOrganizer>.Instance);

    private string WriteSource()
    {
        var path = Path.Combine(_root, "source", "Daft Punk - Get Lucky.mp3");
        File.WriteAllBytes(path, SourceBytes);

        return path;
    }

    private OrganizeRequest Request(
        string source,
        bool keepSource,
        string? replaces = null,
        LibraryLayout layout = LibraryLayout.Plexamp,
        string sidecarOptions = "{}",
        long libraryId = 1,
        string? coverUrl = "https://coverartarchive.org/release/5000a285-b67e-4cfc-b54b-2b98f1810d2e/front-1200")
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

        _album = new AlbumContext
        {
            SongId = 10,
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = "Daft Punk",
            AlbumKey = AlbumKey,
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = "2013-05-17",
            CoverUrl = coverUrl,
        };

        var library = new Library
        {
            Id = libraryId,
            Name = "Music",
            RootPath = LibraryRoot,
            Layout = layout,
            SidecarOptions = sidecarOptions,
        };

        var media = new MediaInfo("mp3", "mp3", 320, 44100, null, 2, 248_000, false, SourceBytes.Length);
        var quality = new Quality { Id = 13, Name = "MP3-320" };

        return new OrganizeRequest(
            song,
            _album,
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