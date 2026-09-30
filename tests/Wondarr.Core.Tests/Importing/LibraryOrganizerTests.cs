using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Lyrics;
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

    /// <summary>The organizer's "(disc, track) already taken" warning.</summary>
    private const int TrackNumberTakenEventId = 2303;

    private static readonly byte[] SourceBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private static readonly byte[] ExistingCover = [0xFF, 0xD8, 0xFF, 0x99, 0x99];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wondarr-organizer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTagWriter _tagWriter = new();
    private readonly FakeFilePlacer _placer = new();
    private readonly FakeCoverFetcher _covers = new();
    private readonly FakeCoverImageProcessor _coverProcessor = new();
    private readonly FakeLrclibClient _lrclib = new();
    private readonly LyricsOptions _lyricsOptions = new();
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

    [Fact]
    public async Task A_cover_write_that_fails_part_way_leaves_no_partial_and_still_files_the_song()
    {
        var source = WriteSource();
        var disk = new HalfWritingDisk();

        var result = await Organizer(disk).OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.CoverJpgPath.Should().BeNull();
        disk.PartialsWritten.Should().ContainSingle();
        File.Exists(disk.PartialsWritten[0]).Should().BeFalse();
        CoverJpg().Should().BeNull();
    }

    [Fact]
    public async Task Warns_when_a_sibling_already_holds_the_same_disc_and_track()
    {
        await SeedSiblingAsync(AlbumKey, date: "2013-05-17", trackNo: 8, discNo: 1);
        var source = WriteSource();
        var logger = new WarningLogger();

        var result = await Organizer(logger: logger).OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        logger.EventIds.Should().Contain(TrackNumberTakenEventId);
    }

    [Fact]
    public async Task Never_warns_about_a_track_number_when_neither_file_has_one()
    {
        await SeedSiblingAsync(AlbumKey, date: "2013-05-17", trackNo: null, discNo: null);
        var source = WriteSource();
        var logger = new WarningLogger();
        var request = Request(source, keepSource: false);
        _album.TrackNo = null;
        _album.DiscNo = null;

        var result = await Organizer(logger: logger).OrganizeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        logger.EventIds.Should().NotContain(TrackNumberTakenEventId);
    }

    [Fact]
    public async Task Writes_the_lyrics_tag_and_an_lrc_sidecar_when_lrclib_has_synced_lyrics()
    {
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(
            LyricsLookupStatus.Found,
            "Plain line one\nPlain line two",
            "[00:01.00] Synced line\r\n[00:02.00] Second line",
            986804);

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);

        // The tag takes the unsynced text; the file's length is what the lookup was matched on.
        _tagWriter.Writes[0].Tags.Lyrics.Should().Be("Plain line one\nPlain line two");
        _lrclib.Requests.Should().ContainSingle().Which.Should().Be(("Get Lucky", "Daft Punk", 248));

        // The sidecar is the placed file's own name with the extension swapped, beside it.
        result.LyricsPath.Should().Be(Path.ChangeExtension(result.FinalPath!, ".lrc"));
        result.LyricsPath.Should().EndWith("08 - Get Lucky.lrc");

        var bytes = File.ReadAllBytes(result.LyricsPath!);
        (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            .Should().BeFalse("a sidecar is UTF-8 without a BOM");
        Encoding.UTF8.GetString(bytes).Should().Be("[00:01.00] Synced line\n[00:02.00] Second line\n");
    }

    [Fact]
    public async Task A_request_that_does_not_look_up_lyrics_asks_lrclib_nothing_and_leaves_the_tag_lyrics_alone()
    {
        // The Compact task moves a file that already carries its lyrics and brings its own sidecar.
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(LyricsLookupStatus.Found, "New words", "[00:01.00] New words", 1);

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false) with { LookUpLyrics = false },
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _lrclib.Requests.Should().BeEmpty();
        _tagWriter.Writes[0].Tags.Lyrics.Should().BeNull("a null lyrics field leaves the file's own lyrics tag as it is");
        result.LyricsPath.Should().BeNull();
    }

    [Fact]
    public async Task Writes_a_txt_sidecar_when_lrclib_only_has_plain_lyrics()
    {
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(LyricsLookupStatus.Found, "Only plain\r\nlyrics", null, 1);

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _tagWriter.Writes[0].Tags.Lyrics.Should().Be("Only plain\r\nlyrics");
        result.LyricsPath.Should().EndWith("08 - Get Lucky.txt");
        File.ReadAllText(result.LyricsPath!).Should().Be("Only plain\nlyrics\n");
    }

    [Theory]
    [InlineData(LyricsLookupStatus.Instrumental)]
    [InlineData(LyricsLookupStatus.NotFound)]
    [InlineData(LyricsLookupStatus.Unavailable)]
    public async Task A_lookup_that_is_not_a_hit_leaves_no_lyrics_anywhere(LyricsLookupStatus status)
    {
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(status, null, null, null);

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.LyricsPath.Should().BeNull();
        _tagWriter.Writes[0].Tags.Lyrics.Should().BeNull();
        LyricsFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task A_library_that_turns_sidecars_off_still_gets_the_lyrics_tag()
    {
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(LyricsLookupStatus.Found, "Plain", "[00:01.00] Synced", 1);

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, sidecarOptions: "{\"lyrics\":false}"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.LyricsPath.Should().BeNull();
        _tagWriter.Writes[0].Tags.Lyrics.Should().Be("Plain");
        LyricsFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Lyrics_are_not_looked_up_at_all_when_the_setting_is_off()
    {
        var source = WriteSource();
        _lyricsOptions.Enabled = false;

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _lrclib.Requests.Should().BeEmpty();
        _tagWriter.Writes[0].Tags.Lyrics.Should().BeNull();
        result.LyricsPath.Should().BeNull();
    }

    [Fact]
    public async Task A_file_whose_length_is_unknown_is_not_looked_up()
    {
        var source = WriteSource();
        _lrclib.Result = new LyricsLookup(LyricsLookupStatus.Found, "Plain", "[00:01.00] Synced", 1);

        var result = await Organizer().OrganizeAsync(
            Request(source, keepSource: false, durationMs: 0),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        _lrclib.Requests.Should().BeEmpty();
        _tagWriter.Writes[0].Tags.Lyrics.Should().BeNull();
        result.LyricsPath.Should().BeNull();
    }

    [Theory]
    [InlineData(".lrc", true)]
    [InlineData(".txt", false)]
    [InlineData(".lrc", false)]
    [InlineData(".txt", true)]
    public async Task Never_writes_a_sidecar_beside_one_that_is_already_there(string extension, bool synced)
    {
        var source = WriteSource();
        var existing = new byte[] { 0x6D, 0x69, 0x6E, 0x65 };
        _lrclib.Result = synced
            ? new LyricsLookup(LyricsLookupStatus.Found, "Plain", "[00:01.00] Synced", 1)
            : new LyricsLookup(LyricsLookupStatus.Found, "Plain", null, 1);

        _placer.OnPlaceAsync = request =>
        {
            var folder = CreateTargetFolder(request);
            File.WriteAllBytes(Path.Combine(folder, string.Concat("08 - Get Lucky", extension)), existing);

            return Task.CompletedTask;
        };

        var result = await Organizer().OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.LyricsPath.Should().BeNull();

        var target = Path.GetDirectoryName(result.FinalPath!)!;
        File.ReadAllBytes(Path.Combine(target, string.Concat("08 - Get Lucky", extension)))
            .Should().Equal(existing);

        // A user's own file wins whichever extension ours would have used.
        var other = string.Equals(extension, ".lrc", StringComparison.Ordinal) ? ".txt" : ".lrc";
        File.Exists(Path.Combine(target, string.Concat("08 - Get Lucky", other))).Should().BeFalse();
    }

    [Fact]
    public async Task A_lyrics_write_that_fails_part_way_leaves_no_partial_and_still_files_the_song()
    {
        var source = WriteSource();
        var disk = new HalfWritingDisk();
        _lrclib.Result = new LyricsLookup(LyricsLookupStatus.Found, "Plain", "[00:01.00] Synced", 1);

        var result = await Organizer(disk).OrganizeAsync(Request(source, keepSource: false), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.LyricsPath.Should().BeNull();

        // The cover's half-written partial and the sidecar's, each removed again.
        disk.PartialsWritten.Should().HaveCount(2);
        disk.PartialsWritten.Should().OnlyContain(path => !File.Exists(path));
        LyricsFiles().Should().BeEmpty();
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

    /// <summary>Every lyrics sidecar anywhere under the library root.</summary>
    private IEnumerable<string> LyricsFiles() =>
        Directory.Exists(LibraryRoot)
            ? Directory.EnumerateFiles(LibraryRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".lrc", StringComparison.Ordinal)
                    || path.EndsWith(".txt", StringComparison.Ordinal))
            : Enumerable.Empty<string>();

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

    private LibraryOrganizer Organizer(IDiskOperations? disk = null, ILogger<LibraryOrganizer>? logger = null) =>
        new(
            _tagWriter,
            _placer,
            _covers,
            disk ?? new DiskOperations(),
            _context,
            _coverProcessor,
            _lrclib,
            new TestOptionsMonitor<LyricsOptions>(_lyricsOptions),
            new TestOptionsMonitor<ImportOptions>(_importOptions),
            logger ?? NullLogger<LibraryOrganizer>.Instance);

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
        int durationMs = 248_000,
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

        var media = new MediaInfo("mp3", "mp3", 320, 44100, null, 2, durationMs, false, SourceBytes.Length);
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

    /// <summary>Real disk operations, except that writing a cover's partial writes half of it and fails, as
    /// a full disk would.</summary>
    private sealed class HalfWritingDisk : IDiskOperations
    {
        private readonly DiskOperations _inner = new();

        public List<string> PartialsWritten { get; } = [];

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateEmptyFile(string path) => _inner.CreateEmptyFile(path);

        public long GetFileSize(string path) => _inner.GetFileSize(path);

        public void MoveFile(string source, string target) => _inner.MoveFile(source, target);

        public void CopyFile(string source, string target) => _inner.CopyFile(source, target);

        public bool TryCreateHardLink(string source, string target) => _inner.TryCreateHardLink(source, target);

        public void WriteAllBytes(string path, byte[] bytes)
        {
            PartialsWritten.Add(path);
            _inner.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

            throw new IOException("There is not enough space on the disk.");
        }

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public void SetUnixFileMode(string path, UnixFileMode mode) => _inner.SetUnixFileMode(path, mode);

        public bool AreSameFile(string first, string second) => _inner.AreSameFile(first, second);

        public IEnumerable<string> EnumerateFiles(string directory) => _inner.EnumerateFiles(directory);

        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime utc) => _inner.SetLastWriteTimeUtc(path, utc);

        public void DeleteEmptyDirectory(string path) => _inner.DeleteEmptyDirectory(path);
    }

    /// <summary>Records the event id of every warning logged.</summary>
    private sealed class WarningLogger : ILogger<LibraryOrganizer>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                EventIds.Add(eventId.Id);
            }
        }
    }
}
