using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Tests.Support;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

public sealed class LibraryRootsTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly LibraryRoots _roots = new(new DiskOperations());
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        _temp.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Marker(string root) => Path.Combine(root, LibraryRoots.MarkerFileName);

    [Fact]
    public void A_folder_that_does_not_exist_is_missing()
    {
        _roots.Check(_temp.Full("nowhere")).Should().Be(LibraryRootStatus.Missing);
    }

    [Fact]
    public void A_folder_without_the_marker_is_unmarked_even_when_it_holds_music()
    {
        _temp.CreateFile("library/Artist/Album/track.mp3");

        _roots.Check(_temp.Full("library")).Should().Be(LibraryRootStatus.Unmarked);
    }

    [Fact]
    public void A_folder_with_the_marker_is_ready_and_the_check_is_live()
    {
        var root = TestLibraryRoots.Mark(_temp.Full("library"));

        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);

        File.Delete(Marker(root));

        _roots.Check(root).Should().Be(LibraryRootStatus.Unmarked, "nothing is cached: a share that drops stops writes at once");

        Directory.Delete(root);

        _roots.Check(root).Should().Be(LibraryRootStatus.Missing);
    }

    [Fact]
    public void Marking_creates_the_folder_and_writes_the_documented_text_without_a_byte_order_mark()
    {
        var root = _temp.Full("deep/new/library");

        _roots.Mark(root);

        var bytes = File.ReadAllBytes(Marker(root));

        Encoding.UTF8.GetString(bytes).Should().Be(
            "This folder is a Wondarr library. Wondarr writes into it only while this file is here, so an unmounted share is never filled. Delete it only when this folder is no longer a Wondarr library.");
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public void Marking_never_truncates_or_replaces_an_existing_marker()
    {
        var root = TestLibraryRoots.Mark(_temp.Full("library"));
        File.WriteAllText(Marker(root), "the user's own note, which is longer than nothing");

        _roots.Mark(root);

        File.ReadAllText(Marker(root)).Should().Be("the user's own note, which is longer than nothing");
    }

    [Fact]
    public void The_sentences_name_the_library_and_say_what_to_do()
    {
        var library = new Library { Name = "Music", RootPath = "/data/music" };

        _roots.Describe(library, LibraryRootStatus.Missing).Should().Be(
            "Library \"Music\": /data/music does not exist. Imports into it wait until it is back; if the share is unmounted, mount it.");
        _roots.Describe(library, LibraryRootStatus.Unmarked).Should().Be(
            "Library \"Music\": /data/music has no .wondarr-library file, so it looks like an unmounted share. Imports into it wait. If this is the right folder, confirm it under Settings → Library.");
    }

    // --- Adoption --------------------------------------------------------------------------------

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        var context = _database.CreateContext(_time);

        // The seeded library points at /data/music, which a test machine does not have: it gets a
        // marked folder of its own, so only the libraries a test adds are in question.
        context.Libraries.Single(library => library.Id == SeedData.DefaultLibraryId).RootPath = TestLibraryRoots.Mark(_temp.Full("seed"));
        await context.SaveChangesAsync();

        return context;
    }

    private static async Task<long> AddLibraryAsync(WondarrDbContext context, string name, string root)
    {
        var library = new Library { Name = name, RootPath = root, Layout = LibraryLayout.Flat, NamingTemplate = "{Track Title}" };
        context.Libraries.Add(library);
        await context.SaveChangesAsync();

        return library.Id;
    }

    private static async Task AddFileAsync(WondarrDbContext context, long libraryId, string path, DateTime importedAt, string sourceType = SourceTypes.Soulseek)
    {
        var artist = new Artist { Name = "Artist " + path.GetHashCode(StringComparison.Ordinal), SortName = "Artist" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = Path.GetFileNameWithoutExtension(path),
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = libraryId,
            AddedBy = "api",
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = path,
            Codec = "mp3",
            Container = "mp3",
            QualityId = 17,
            SourceType = sourceType,
            ImportedAt = importedAt,
        });
        await context.SaveChangesAsync();
    }

    private LibraryRootAdopter Adopter(WondarrDbContext context) =>
        new(context, _roots, new DiskOperations(), NullLogger<LibraryRootAdopter>.Instance);

    [Fact]
    public async Task Adoption_marks_the_root_of_a_library_that_holds_no_files()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("empty");
        Directory.CreateDirectory(root);
        context.Libraries.Single(library => library.Id == SeedData.DefaultLibraryId).RootPath = root;
        await context.SaveChangesAsync();

        var adopted = await Adopter(context).AdoptAsync(CancellationToken.None);

        adopted.Should().Be(1);
        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task Adoption_marks_a_library_when_one_of_its_recorded_files_is_on_the_disk()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("held");
        var present = _temp.CreateFile("held/Artist/Album/present.mp3");
        var id = await AddLibraryAsync(context, "Held", root);
        await AddFileAsync(context, id, present, _time.GetUtcNow().UtcDateTime);
        await AddFileAsync(context, id, Path.Combine(root, "gone.mp3"), _time.GetUtcNow().UtcDateTime.AddDays(1));

        await Adopter(context).AdoptAsync(CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task Adoption_leaves_a_library_unmarked_when_none_of_its_recorded_files_exists()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("share");
        Directory.CreateDirectory(root);
        var id = await AddLibraryAsync(context, "Share", root);
        await AddFileAsync(context, id, Path.Combine(root, "a.mp3"), _time.GetUtcNow().UtcDateTime);
        await AddFileAsync(context, id, Path.Combine(root, "b.mp3"), _time.GetUtcNow().UtcDateTime);

        var adopted = await Adopter(context).AdoptAsync(CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Unmarked, "an empty folder with files on record is an unmounted share");
        File.Exists(Marker(root)).Should().BeFalse();
        adopted.Should().Be(0);
    }

    [Fact]
    public async Task Adoption_never_creates_the_folder_of_a_library_that_has_files_on_record()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("not-mounted");
        var id = await AddLibraryAsync(context, "Gone", root);
        await AddFileAsync(context, id, Path.Combine(root, "a.mp3"), _time.GetUtcNow().UtcDateTime);

        await Adopter(context).AdoptAsync(CancellationToken.None);

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public async Task Adoption_looks_only_at_the_twenty_latest_imports()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("busy");
        Directory.CreateDirectory(root);
        var id = await AddLibraryAsync(context, "Busy", root);
        var start = _time.GetUtcNow().UtcDateTime;

        // The only file on the disk is the oldest of 21: the sample does not reach it.
        var oldest = _temp.CreateFile("busy/oldest.mp3");
        await AddFileAsync(context, id, oldest, start);

        for (var index = 1; index <= LibraryRootAdopter.SampleSize; index++)
        {
            await AddFileAsync(context, id, Path.Combine(root, $"missing-{index}.mp3"), start.AddMinutes(index));
        }

        await Adopter(context).AdoptAsync(CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Unmarked);
    }

    [Fact]
    public async Task A_reference_file_does_not_count_as_a_file_of_the_library()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("refs-only");
        Directory.CreateDirectory(root);
        var id = await AddLibraryAsync(context, "Refs", root);
        await AddFileAsync(context, id, "/somewhere/else.mp3", _time.GetUtcNow().UtcDateTime, SourceTypes.Reference);

        await Adopter(context).AdoptAsync(CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task Adoption_leaves_a_ready_root_alone()
    {
        await using var context = await ContextAsync();
        var root = TestLibraryRoots.Mark(_temp.Full("ready"));
        File.WriteAllText(Marker(root), "mine");
        context.Libraries.Single(library => library.Id == SeedData.DefaultLibraryId).RootPath = root;
        await context.SaveChangesAsync();

        (await Adopter(context).AdoptAsync(CancellationToken.None)).Should().Be(0);

        File.ReadAllText(Marker(root)).Should().Be("mine");
    }

    // --- Health check ----------------------------------------------------------------------------

    [Fact]
    public async Task The_health_check_names_every_library_that_is_not_ready_in_one_error()
    {
        await using var context = await ContextAsync();
        var missing = _temp.Full("missing");
        var unmounted = _temp.Full("unmounted");
        Directory.CreateDirectory(unmounted);
        var missingId = await AddLibraryAsync(context, "Archive", missing);
        var unmountedId = await AddLibraryAsync(context, "Share", unmounted);
        await AddFileAsync(context, missingId, Path.Combine(missing, "a.mp3"), _time.GetUtcNow().UtcDateTime);
        await AddFileAsync(context, unmountedId, Path.Combine(unmounted, "a.mp3"), _time.GetUtcNow().UtcDateTime);

        var check = new LibraryRootHealthCheck(context, _roots, Adopter(context));

        var result = await check.CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Error);
        result.Source.Should().Be("LibraryRootHealthCheck");
        result.Message.Should().Be(
            $"Library \"Archive\": {missing} does not exist. Imports into it wait until it is back; if the share is unmounted, mount it. "
            + $"Library \"Share\": {unmounted} has no .wondarr-library file, so it looks like an unmounted share. Imports into it wait. If this is the right folder, confirm it under Settings → Library.");

        // Stable: nothing in it changes between runs, so the notification does not repeat itself.
        (await check.CheckAsync(CancellationToken.None)).Message.Should().Be(result.Message);
    }

    [Fact]
    public async Task The_health_check_is_ok_when_every_library_folder_is_ready()
    {
        await using var context = await ContextAsync();
        var result = await new LibraryRootHealthCheck(context, _roots, Adopter(context)).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Message.Should().Be("Every library folder is ready");
    }

    [Fact]
    public async Task The_health_check_adopts_a_library_whose_share_has_come_back()
    {
        await using var context = await ContextAsync();
        var root = _temp.Full("share");
        var present = Path.Combine(root, "a.mp3");
        var id = await AddLibraryAsync(context, "Share", root);
        await AddFileAsync(context, id, present, _time.GetUtcNow().UtcDateTime);
        var check = new LibraryRootHealthCheck(context, _roots, Adopter(context));

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Error);

        // The share is mounted again: its files are there.
        _temp.CreateFile("share/a.mp3");

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);
        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    // --- Create and update through the service -------------------------------------------------------

    [Fact]
    public async Task Creating_a_library_marks_its_root()
    {
        await using var context = await ContextAsync();
        var service = new LibraryService(context, roots: _roots, logger: NullLogger<LibraryService>.Instance);
        var root = _temp.Full("new-library");

        await service.CreateAsync(new Library { Name = "New", RootPath = root, Layout = LibraryLayout.Flat }, CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task Moving_the_root_of_a_library_without_files_marks_the_new_root()
    {
        await using var context = await ContextAsync();
        var service = new LibraryService(context, roots: _roots, logger: NullLogger<LibraryService>.Instance);
        var root = _temp.Full("moved");
        var library = (await service.GetAsync(SeedData.DefaultLibraryId, CancellationToken.None))!;
        library.RootPath = root;

        await service.UpdateAsync(library, CancellationToken.None);

        _roots.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task Editing_a_library_without_moving_its_root_marks_nothing()
    {
        await using var context = await ContextAsync();
        var service = new LibraryService(context, roots: _roots, logger: NullLogger<LibraryService>.Instance);
        var plain = _temp.Full("plain");
        Directory.CreateDirectory(plain);
        context.Libraries.Single(candidate => candidate.Id == SeedData.DefaultLibraryId).RootPath = plain;
        await context.SaveChangesAsync();
        var library = (await service.GetAsync(SeedData.DefaultLibraryId, CancellationToken.None))!;
        library.Name = "Renamed";

        await service.UpdateAsync(library, CancellationToken.None);

        _roots.Check(plain).Should().Be(LibraryRootStatus.Unmarked, "a rename is not the user choosing the folder");
    }

    [Fact]
    public async Task A_root_that_cannot_be_marked_is_logged_not_a_validation_error()
    {
        await using var context = await ContextAsync();
        var blocker = _temp.CreateFile("a-file");
        var service = new LibraryService(context, roots: _roots, logger: NullLogger<LibraryService>.Instance);

        // A folder under a regular file can never be created.
        var created = await service.CreateAsync(
            new Library { Name = "Blocked", RootPath = Path.Combine(blocker, "library"), Layout = LibraryLayout.Flat },
            CancellationToken.None);

        created.Id.Should().BeGreaterThan(0);
        _roots.Check(created.RootPath).Should().Be(LibraryRootStatus.Missing);
    }
}
