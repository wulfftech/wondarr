using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Media;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Importing;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// Adoption: an identified reference file is <em>copied</em> into the target library by the organizer
/// imports go through, the song's file row is repointed at the copy, the reference row becomes
/// <see cref="ReferenceFileState.Adopted"/>, and the user's own file is never touched.
/// </summary>
/// <remarks>
/// The organizer is a stand-in, not the real <see cref="LibraryOrganizer"/>: adoption's own job is
/// which file goes where and what is written back, and the real organizer's tagging is covered by
/// <c>LibraryOrganizerTests</c>. The stand-in copies the source to a path under the target library's
/// root, which is exactly what <c>KeepSource: true</c> makes the real one do, so the assertions about
/// the original file are made against a real file on disk.
/// </remarks>
public sealed class ReferenceAdopterTests : IDisposable
{
    private const string RelativePath = "Daft Punk/Random Access Memories/08 - Get Lucky.flac";
    private const string AlbumTitle = "Random Access Memories";

    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLibraryOrganizer _organizer = new();
    private readonly RecordingEventAggregator _events = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "wondarr-adopt-tests", Guid.NewGuid().ToString("N"));

    private string ReferenceRoot => Path.Combine(_temp, "reference");

    private string LibraryRoot => Path.Combine(_temp, "library");

    // --- The file ------------------------------------------------------------------------------

    [Fact]
    public async Task An_identified_reference_file_is_copied_into_the_library_and_the_song_follows()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);
        var song = await SongAsync(context, artist.Id, target.Id);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);
        var file = await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        var original = AbsolutePath(row.RelativePath);
        var bytesBefore = Hash(original);
        var modifiedBefore = File.GetLastWriteTimeUtc(original);

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(1, 0, 0));

        // What the organizer was asked to do: the user's file as the source, kept, into the target.
        var request = _organizer.Requests.Should().ContainSingle().Subject;
        request.SourcePath.Should().Be(original);
        request.KeepSource.Should().BeTrue();
        request.ReplacesPath.Should().BeNull();
        request.SourceType.Should().Be(SourceTypes.Adopted);
        request.Extension.Should().Be("flac");
        request.Library.Id.Should().Be(target.Id);
        request.AcoustId.Should().Be("ac-1");
        request.Media.DurationMs.Should().Be(248_000);
        request.Credits.Should().ContainSingle();

        var placed = PlacedPath(song.Title);
        var stored = await context.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == song.Id);

        stored.Id.Should().Be(file.Id);
        stored.Path.Should().Be(placed);
        stored.SourceType.Should().Be(SourceTypes.Adopted);
        stored.Size.Should().Be(new FileInfo(placed).Length);
        stored.QualityId.Should().Be(MeasuredQuality.FromMediaInfo(Probe(248_000)));
        stored.Codec.Should().Be("flac");
        stored.DurationMs.Should().Be(248_000);
        stored.ImportedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
        stored.TagsWritten.Should().Contain("TITLE");

        var sourceRef = JsonDocument.Parse(stored.SourceRef!).RootElement;
        sourceRef.GetProperty("referenceLibraryId").GetInt64().Should().Be(reference.Id);
        sourceRef.GetProperty("referenceFileId").GetInt64().Should().Be(row.Id);
        sourceRef.GetProperty("originalPath").GetString().Should().Be(original);

        var adopted = await context.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == row.Id);
        adopted.State.Should().Be(ReferenceFileState.Adopted);
        adopted.Message.Should().BeNull();

        var history = await context.History.AsNoTracking().SingleAsync();
        history.SongId.Should().Be(song.Id);
        history.EventType.Should().Be(HistoryEventType.Imported);

        var data = JsonDocument.Parse(history.Data).RootElement;
        data.GetProperty("path").GetString().Should().Be(placed);
        data.GetProperty("adoptedFrom").GetString().Should().Be(original);

        var imported = _events.Of<SongImportedEvent>().Should().ContainSingle().Subject;
        imported.SongId.Should().Be(song.Id);
        imported.SongFileId.Should().Be(file.Id);
        imported.Upgraded.Should().BeFalse();

        // The promise: the user's own file is only ever read.
        Hash(original).Should().Be(bytesBefore);
        File.GetLastWriteTimeUtc(original).Should().Be(modifiedBefore);
    }

    [Fact]
    public async Task A_reference_mode_library_adopts_nothing()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Reference);
        var artist = await ArtistAsync(context);
        var song = await SongAsync(context, artist.Id, target.Id);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);
        await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(0, 0, 0));
        _organizer.Requests.Should().BeEmpty();
        (await RowAsync(context, row.Id)).State.Should().Be(ReferenceFileState.Identified);
    }

    [Fact]
    public async Task A_song_that_already_holds_another_file_is_skipped_and_left_alone()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);
        var song = await SongAsync(context, artist.Id, target.Id);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);

        // The song was downloaded since: its file is a library file, not the reference file.
        var held = await SongFileAsync(context, song.Id, Path.Combine(LibraryRoot, "downloaded.flac"), SourceTypes.Soulseek);
        context.ChangeTracker.Clear();

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(0, 1, 0));
        _organizer.Requests.Should().BeEmpty();

        var stored = await context.SongFiles.AsNoTracking().SingleAsync(candidate => candidate.SongId == song.Id);
        stored.Path.Should().Be(held.Path);
        stored.SourceType.Should().Be(SourceTypes.Soulseek);

        var skipped = await RowAsync(context, row.Id);
        skipped.State.Should().Be(ReferenceFileState.Identified);
        skipped.Message.Should().BeNull();
    }

    // --- Failures ------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_organize_leaves_the_row_identified_and_saves_no_album_correction()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);
        var song = await SongAsync(context, artist.Id, target.Id);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);
        await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        // The real organizer corrects the tracked album context before it can fail; that correction
        // must not be saved with the failed file.
        _organizer.Handler = request =>
        {
            request.Album.AlbumTitle = "corrected by the folder";

            return new OrganizeResult(
                OrganizeFailure.Placement,
                "The library root is not writable.",
                null,
                null,
                new Dictionary<string, string>());
        };

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(0, 0, 1));

        var failed = await RowAsync(context, row.Id);
        failed.State.Should().Be(ReferenceFileState.Identified);
        failed.Message.Should().Be("adoption failed: The library root is not writable.");

        var album = await context.AlbumContexts.AsNoTracking().SingleAsync(candidate => candidate.SongId == song.Id);
        album.AlbumTitle.Should().Be(AlbumTitle);

        (await context.History.AsNoTracking().CountAsync()).Should().Be(0);
        _events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_fails_does_not_stop_the_next_one()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);

        var first = await SongAsync(context, artist.Id, target.Id, title: "First");
        var firstRow = await ReferenceFileAsync(context, reference.Id, first.Id, "a/first.flac");
        await SongFileAsync(context, first.Id, AbsolutePath(firstRow.RelativePath), SourceTypes.Reference);

        var second = await SongAsync(context, artist.Id, target.Id, title: "Second");
        var secondRow = await ReferenceFileAsync(context, reference.Id, second.Id, "b/second.flac");
        await SongFileAsync(context, second.Id, AbsolutePath(secondRow.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        _organizer.Handler = request => request.Song.Id == first.Id
            ? new OrganizeResult(OrganizeFailure.Placement, "The library root is not writable.", null, null, new Dictionary<string, string>())
            : null;

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(1, 0, 1));
        (await RowAsync(context, firstRow.Id)).State.Should().Be(ReferenceFileState.Identified);
        (await RowAsync(context, secondRow.Id)).State.Should().Be(ReferenceFileState.Adopted);
        _events.Of<SongImportedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task An_organizer_that_throws_fails_that_file_and_the_next_one_is_still_adopted()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);

        var first = await SongAsync(context, artist.Id, target.Id, title: "First");
        var firstRow = await ReferenceFileAsync(context, reference.Id, first.Id, "a/first.flac");
        await SongFileAsync(context, first.Id, AbsolutePath(firstRow.RelativePath), SourceTypes.Reference);

        var second = await SongAsync(context, artist.Id, target.Id, title: "Second");
        var secondRow = await ReferenceFileAsync(context, reference.Id, second.Id, "b/second.flac");
        await SongFileAsync(context, second.Id, AbsolutePath(secondRow.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        // The user deleted the original after the scan: the organizer's staging copy throws.
        _organizer.Handler = request => request.Song.Id == first.Id
            ? throw new FileNotFoundException("Could not find file 'first.flac'.")
            : null;

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(1, 0, 1));

        var failed = await RowAsync(context, firstRow.Id);
        failed.State.Should().Be(ReferenceFileState.Identified);
        failed.Message.Should().StartWith("adoption failed: Could not find file");
        (await RowAsync(context, secondRow.Id)).State.Should().Be(ReferenceFileState.Adopted);
        _events.Of<SongImportedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_song_without_an_album_context_fails_with_a_message()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);
        var song = await SongAsync(context, artist.Id, target.Id, withAlbum: false);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);
        await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        var result = await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        result.Should().Be(new ReferenceAdoptResult(0, 0, 1));
        (await RowAsync(context, row.Id)).Message.Should().Be("adoption failed: the song has no album context");
    }

    [Fact]
    public async Task A_reference_library_that_does_not_exist_is_a_programming_error()
    {
        await using var context = await ContextAsync();

        var adopt = () => Adopter(context).AdoptAsync(4242, null, CancellationToken.None);

        await adopt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*4242*");
    }

    // --- The song ------------------------------------------------------------------------------

    [Fact]
    public async Task The_song_is_moved_into_the_target_library()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);
        var other = new Library { Name = "Elsewhere", RootPath = Path.Combine(_temp, "elsewhere") };
        context.Libraries.Add(other);
        await context.SaveChangesAsync();

        var song = await SongAsync(context, artist.Id, other.Id);
        var row = await ReferenceFileAsync(context, reference.Id, song.Id);
        await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        context.ChangeTracker.Clear();

        await Adopter(context).AdoptAsync(reference.Id, null, CancellationToken.None);

        var stored = await context.Songs.AsNoTracking().SingleAsync(candidate => candidate.Id == song.Id);
        stored.LibraryId.Should().Be(target.Id);
    }

    [Fact]
    public async Task Progress_is_reported_every_twenty_five_adopted_files()
    {
        await using var context = await ContextAsync();
        var (target, reference) = await LibrariesAsync(context, ReferenceLibraryMode.Adopt);
        var artist = await ArtistAsync(context);

        for (var index = 0; index < ReferenceAdopter.ProgressEvery; index++)
        {
            var title = string.Concat("Track ", index.ToString("00"));
            var song = await SongAsync(context, artist.Id, target.Id, title: title);
            var row = await ReferenceFileAsync(context, reference.Id, song.Id, $"a/{title}.flac");
            await SongFileAsync(context, song.Id, AbsolutePath(row.RelativePath), SourceTypes.Reference);
        }

        context.ChangeTracker.Clear();

        var messages = new List<string>();
        var result = await Adopter(context)
            .AdoptAsync(
                reference.Id,
                message =>
                {
                    messages.Add(message);

                    return Task.CompletedTask;
                },
                CancellationToken.None);

        result.Adopted.Should().Be(ReferenceAdopter.ProgressEvery);
        messages.Should().Equal("Adopted 25 of 25 files");
    }

    // --- The command handler -------------------------------------------------------------------

    [Fact]
    public async Task An_empty_body_adopts_from_every_enabled_adopt_library()
    {
        await using var context = await ContextAsync();
        var adoptLibrary = await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        await BareLibraryAsync(context, ReferenceLibraryMode.Reference, enabled: true);
        await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: false);
        context.ChangeTracker.Clear();

        var adopter = Substitute.For<IReferenceAdopter>();
        adopter
            .AdoptAsync(Arg.Any<long>(), Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceAdoptResult(2, 1, 0));

        using var provider = Provider(adopter);
        var summary = await Handler(provider).ExecuteAsync(Context(null), CancellationToken.None);

        CalledIds(adopter).Should().Equal(adoptLibrary.Id);
        summary.Should().Contain("1 library").And.Contain("2 adopted, 1 skipped, 0 failed");
    }

    [Fact]
    public async Task A_body_naming_a_library_adopts_only_that_one()
    {
        await using var context = await ContextAsync();
        await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        var second = await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        context.ChangeTracker.Clear();

        var adopter = Substitute.For<IReferenceAdopter>();
        adopter
            .AdoptAsync(Arg.Any<long>(), Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceAdoptResult(1, 0, 0));

        using var provider = Provider(adopter);
        var summary = await Handler(provider)
            .ExecuteAsync(Context($"{{\"referenceLibraryId\": {second.Id}}}"), CancellationToken.None);

        CalledIds(adopter).Should().Equal(second.Id);
        summary.Should().Contain("1 adopted");
    }

    [Fact]
    public async Task A_library_that_fails_does_not_stop_the_next_one()
    {
        await using var context = await ContextAsync();
        var first = await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        var second = await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        context.ChangeTracker.Clear();

        var adopter = Substitute.For<IReferenceAdopter>();
        adopter
            .AdoptAsync(first.Id, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReferenceAdoptResult>(new InvalidOperationException("database is locked")));
        adopter
            .AdoptAsync(second.Id, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceAdoptResult(3, 0, 0));

        using var provider = Provider(adopter);
        var summary = await Handler(provider).ExecuteAsync(Context(null), CancellationToken.None);

        CalledIds(adopter).Should().Equal(first.Id, second.Id);
        summary.Should().Contain("3 adopted").And.Contain("1 unavailable");
    }

    // --- The scan handler ----------------------------------------------------------------------

    [Fact]
    public async Task The_scan_adopts_after_identifying_for_an_adopt_library_only()
    {
        await using var context = await ContextAsync();
        var adoptLibrary = await BareLibraryAsync(context, ReferenceLibraryMode.Adopt, enabled: true);
        await BareLibraryAsync(context, ReferenceLibraryMode.Reference, enabled: true);
        context.ChangeTracker.Clear();

        var adopter = Substitute.For<IReferenceAdopter>();
        adopter
            .AdoptAsync(Arg.Any<long>(), Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceAdoptResult(4, 0, 0));

        using var provider = Provider(adopter, scanner: Scanner(), identifier: Substitute.For<IReferenceIdentifier>());
        var summary = await Handler(provider).ExecuteAsync(Context(null), CancellationToken.None);

        CalledIds(adopter).Should().Equal(adoptLibrary.Id);
        summary.Should().Contain("4 adopted");
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();

        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private ReferenceAdopter Adopter(WondarrDbContext context) =>
        new(context, _organizer, _events, _time, NullLogger<ReferenceAdopter>.Instance);

    private static ReferenceAdoptCommandHandler Handler(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceAdoptCommandHandler>.Instance);

    private static CommandContext Context(string? body) =>
        new(1, body, CommandTrigger.Scheduled, _ => Task.CompletedTask);

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private string AbsolutePath(string relativePath) =>
        Path.Combine(ReferenceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string PlacedPath(string title) =>
        Path.Combine(LibraryRoot, string.Concat(title, ".flac"));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static MediaInfo Probe(int durationMs) =>
        new("flac", "flac", 900, 44100, 16, 2, durationMs, true, 25_000_000);

    private async Task<(Library Target, ReferenceLibrary Reference)> LibrariesAsync(
        WondarrDbContext context,
        ReferenceLibraryMode mode)
    {
        var target = new Library { Name = "Adopted music", RootPath = LibraryRoot };
        context.Libraries.Add(target);
        await context.SaveChangesAsync();

        var reference = new ReferenceLibrary
        {
            Name = "Old music",
            RootPath = ReferenceRoot,
            Mode = mode,
            LibraryId = target.Id,
        };
        context.ReferenceLibraries.Add(reference);
        await context.SaveChangesAsync();

        return (target, reference);
    }

    /// <summary>A reference library with no mode-specific setup, for the command-handler tests.</summary>
    private async Task<ReferenceLibrary> BareLibraryAsync(
        WondarrDbContext context,
        ReferenceLibraryMode mode,
        bool enabled)
    {
        var library = new ReferenceLibrary
        {
            Name = string.Concat(mode, " ", enabled, " ", Guid.NewGuid().ToString("N")),
            RootPath = Path.Combine(_temp, Guid.NewGuid().ToString("N")),
            Mode = mode,
            Enabled = enabled,
        };

        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    private static async Task<Artist> ArtistAsync(WondarrDbContext context)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };

        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        return artist;
    }

    private static async Task<Song> SongAsync(
        WondarrDbContext context,
        long artistId,
        long libraryId,
        string title = "Get Lucky",
        bool withAlbum = true)
    {
        var song = new Song
        {
            Title = title,
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artistId,
            LibraryId = libraryId,
            QualityProfileId = SeedData.StandardProfileId,
            AddedBy = "ui",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        if (withAlbum)
        {
            context.AlbumContexts.Add(new AlbumContext
            {
                SongId = song.Id,
                Kind = AlbumContextKind.Album,
                AlbumTitle = AlbumTitle,
                AlbumArtist = "Daft Punk",
                AlbumKey = "r-random-access-memories",
                TrackNo = 8,
            });
        }

        context.SongArtists.Add(new SongArtist
        {
            SongId = song.Id,
            ArtistId = artistId,
            Role = ArtistRole.Main,
            Position = 1,
        });

        await context.SaveChangesAsync();

        return song;
    }

    private static async Task<SongFile> SongFileAsync(
        WondarrDbContext context,
        long songId,
        string path,
        string sourceType)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 512).Select(index => (byte)index)]);

        var file = new SongFile
        {
            SongId = songId,
            Path = path,
            Size = new FileInfo(path).Length,
            Codec = "flac",
            Container = "flac",
            DurationMs = 248_000,
            QualityId = 1,
            SourceType = sourceType,
            SourceRef = "{}",
            ImportedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        context.SongFiles.Add(file);
        await context.SaveChangesAsync();

        return file;
    }

    private async Task<ReferenceFile> ReferenceFileAsync(
        WondarrDbContext context,
        long libraryId,
        long songId,
        string relativePath = RelativePath)
    {
        var absolute = AbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [.. Enumerable.Range(0, 512).Select(index => (byte)index)]);

        var row = new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            Size = new FileInfo(absolute).Length,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Probe = JsonSerializer.Serialize(Probe(248_000), StoredJson),
            AcoustId = "ac-1",
            Confidence = 1.0,
            IdentifiedBy = "tag_mbid",
            State = ReferenceFileState.Identified,
            SongId = songId,
        };

        context.ReferenceFiles.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    private static Task<ReferenceFile> RowAsync(WondarrDbContext context, long id) =>
        context.ReferenceFiles.AsNoTracking().SingleAsync(candidate => candidate.Id == id);

    private static IReferenceScanner Scanner()
    {
        var scanner = Substitute.For<IReferenceScanner>();
        scanner
            .ScanAsync(Arg.Any<long>(), Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(new ReferenceScanResult(1, 1, 0, 0, 0, 0));

        return scanner;
    }

    private ServiceProvider Provider(
        IReferenceAdopter adopter,
        IReferenceScanner? scanner = null,
        IReferenceIdentifier? identifier = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton(adopter);

        if (scanner is not null)
        {
            services.AddSingleton(scanner);
        }

        if (identifier is not null)
        {
            services.AddSingleton(identifier);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>The library ids an adopter substitute was asked to adopt from, in order.</summary>
    private static List<long> CalledIds(IReferenceAdopter adopter) =>
        [.. adopter.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IReferenceAdopter.AdoptAsync))
            .Select(call => (long)call.GetArguments()[0]!)];

    /// <summary>
    /// Stands in for <see cref="LibraryOrganizer"/>: it copies the source to a path under the target
    /// library's root — what <c>KeepSource: true</c> makes the real one do — and reports the tags it
    /// would have written. A <see cref="Handler"/> overrides the outcome for the failure cases.
    /// </summary>
    private sealed class FakeLibraryOrganizer : ILibraryOrganizer
    {
        /// <summary>Every request, in order.</summary>
        public List<OrganizeRequest> Requests { get; } = [];

        /// <summary>Replaces this call's outcome; <see langword="null"/> means "copy it and succeed".</summary>
        public Func<OrganizeRequest, OrganizeResult?>? Handler { get; set; }

        /// <inheritdoc />
        public Task<OrganizeResult> OrganizeAsync(OrganizeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (Handler?.Invoke(request) is { } overridden)
            {
                return Task.FromResult(overridden);
            }

            var target = Path.Combine(
                request.Library.RootPath,
                string.Concat(request.Song.Title, ".", request.Extension));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(request.SourcePath, target, overwrite: true);

            return Task.FromResult(new OrganizeResult(
                OrganizeFailure.None,
                null,
                target,
                null,
                new Dictionary<string, string> { ["TITLE"] = request.Song.Title }));
        }
    }
}
