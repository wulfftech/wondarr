using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>The rules of a reference library, against a real migrated SQLite database.</summary>
public class ReferenceLibraryServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_library_that_follows_every_rule_is_stored()
    {
        await using var context = await ContextAsync();

        var summary = await Service(context).AddAsync(
            new ReferenceLibraryInput("  Music I own  ", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        summary.Library.Name.Should().Be("Music I own");
        summary.Library.RootPath.Should().Be("/reference/music");
        summary.Library.Enabled.Should().BeTrue();
        summary.Counts.Should().Be(ReferenceLibraryCounts.Empty);
    }

    [Fact]
    public async Task An_empty_name_is_a_bad_request_on_the_name_field()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("   ", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("name");
    }

    [Fact]
    public async Task A_name_that_differs_only_in_case_from_another_library_is_refused()
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        await service.AddAsync(
            new ReferenceLibraryInput("Music", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        // The NOCASE index on reference_library.name would refuse this too; the check says it kindly.
        var add = async () => await service.AddAsync(
            new ReferenceLibraryInput("music", "/reference/other", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("name");
    }

    [Fact]
    public async Task Renaming_a_library_to_its_own_name_is_allowed()
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        var added = await service.AddAsync(
            new ReferenceLibraryInput("Music", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var updated = await service.UpdateAsync(
            added.Library.Id,
            new ReferenceLibraryInput("Music", "/reference/elsewhere", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        updated!.Library.RootPath.Should().Be("/reference/elsewhere");
    }

    [Fact]
    public async Task A_relative_root_is_refused()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Music", "reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("rootPath");
    }

    [Fact]
    public async Task A_root_inside_a_managed_library_is_refused()
    {
        await using var context = await ContextAsync();

        // The seeded library lives at /data/music.
        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Inside", "/data/music/inner", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("rootPath");
    }

    [Fact]
    public async Task A_root_that_equals_a_managed_library_is_refused()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Same", "/data/music/", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("rootPath");
    }

    [Fact]
    public async Task A_root_that_contains_a_managed_library_is_refused()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Around", "/data", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("rootPath");
    }

    [Fact]
    public async Task A_sibling_of_a_managed_library_is_allowed()
    {
        await using var context = await ContextAsync();

        // "/data/music2" shares the prefix of "/data/music" but is not inside it.
        var summary = await Service(context).AddAsync(
            new ReferenceLibraryInput("Sibling", "/data/music2", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        summary.Library.RootPath.Should().Be("/data/music2");
    }

    [Fact]
    public async Task Adopting_without_a_library_is_refused()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Adopt", "/reference/music", ReferenceLibraryMode.Adopt, null, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("libraryId");
    }

    [Fact]
    public async Task Adopting_into_an_unknown_library_is_refused()
    {
        await using var context = await ContextAsync();

        var add = async () => await Service(context).AddAsync(
            new ReferenceLibraryInput("Adopt", "/reference/music", ReferenceLibraryMode.Adopt, 987654, true),
            CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>()).Which.Field.Should().Be("libraryId");
    }

    [Fact]
    public async Task Adopting_into_a_known_library_is_stored()
    {
        await using var context = await ContextAsync();

        var summary = await Service(context).AddAsync(
            new ReferenceLibraryInput("Adopt", "/reference/music", ReferenceLibraryMode.Adopt, 1, true),
            CancellationToken.None);

        summary.Library.Mode.Should().Be(ReferenceLibraryMode.Adopt);
        summary.Library.LibraryId.Should().Be(1);
    }

    [Fact]
    public async Task Updating_an_unknown_library_is_a_missing_row()
    {
        await using var context = await ContextAsync();

        var updated = await Service(context).UpdateAsync(
            987654,
            new ReferenceLibraryInput("Music", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        updated.Should().BeNull();
    }

    [Fact]
    public async Task Listing_counts_the_files_of_each_library_by_state()
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        var first = await service.AddAsync(
            new ReferenceLibraryInput("Music", "/reference/music", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var second = await service.AddAsync(
            new ReferenceLibraryInput("Other", "/reference/other", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        AddFile(context, first.Library.Id, "a.flac", ReferenceFileState.Ambiguous);
        AddFile(context, first.Library.Id, "b.flac", ReferenceFileState.Ambiguous);
        AddFile(context, first.Library.Id, "c.flac", ReferenceFileState.Identified);
        AddFile(context, second.Library.Id, "d.flac", ReferenceFileState.Missing);
        await context.SaveChangesAsync();

        var summaries = await service.ListAsync(CancellationToken.None);

        summaries.Should().HaveCount(2);
        summaries[0].Counts.Should().Be(new ReferenceLibraryCounts(3, 0, 1, 2, 0, 0, 0, 0, 0));
        summaries[1].Counts.Should().Be(new ReferenceLibraryCounts(1, 0, 0, 0, 0, 0, 0, 1, 0));
    }

    [Fact]
    public async Task Deleting_a_library_frees_only_the_songs_owned_through_its_files()
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        var doomed = await service.AddAsync(
            new ReferenceLibraryInput("Doomed", "/reference/doomed", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var kept = await service.AddAsync(
            new ReferenceLibraryInput("Kept", "/reference/kept", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var mySong = await AddSongAsync(context, "Mine");
        var yourSong = await AddSongAsync(context, "Yours");
        var downloaded = await AddSongAsync(context, "Downloaded");
        var garbled = await AddSongAsync(context, "Garbled");
        var listed = await AddSongAsync(context, "Listed");

        context.SongFiles.AddRange(
            NewFile(mySong, "/reference/doomed/a.flac", SourceTypes.Reference, doomed.Library.Id),
            NewFile(yourSong, "/reference/kept/b.flac", SourceTypes.Reference, kept.Library.Id),
            NewFile(downloaded, "/data/music/c.flac", SourceTypes.Soulseek, doomed.Library.Id),
            WithSourceRef(NewFile(garbled, "/reference/doomed/d.flac", SourceTypes.Reference, doomed.Library.Id), "not json"),
            WithSourceRef(NewFile(listed, "/reference/doomed/e.flac", SourceTypes.Reference, doomed.Library.Id), "[1]"));

        AddFile(context, doomed.Library.Id, "a.flac", ReferenceFileState.Identified);
        AddFile(context, kept.Library.Id, "b.flac", ReferenceFileState.Identified);
        await context.SaveChangesAsync();

        (await service.DeleteAsync(doomed.Library.Id, ReferenceLibraryDeleteChoice.Wanted, CancellationToken.None)).Should().Be(1);

        (await context.ReferenceLibraries.CountAsync()).Should().Be(1);
        (await context.ReferenceFiles.AsNoTracking().Select(row => row.ReferenceLibraryId).ToListAsync())
            .Should().Equal(kept.Library.Id);

        // Only the song owned through the deleted library is wanted again: the other library's file and
        // the downloaded file of the same song are untouched.
        var remaining = await context.SongFiles.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(4, "a reference whose source ref cannot be read is never deleted");
        remaining.Should().Contain(file => file.Path == "/reference/kept/b.flac");
        remaining.Should().Contain(file => file.Path == "/data/music/c.flac");
        remaining.Should().Contain(file => file.Path == "/reference/doomed/d.flac");
        remaining.Should().Contain(file => file.Path == "/reference/doomed/e.flac");
    }

    [Fact]
    public async Task The_count_of_affected_songs_is_the_songs_owned_through_the_library_only()
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        var library = await service.AddAsync(
            new ReferenceLibraryInput("Mine", "/reference/mine", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var other = await service.AddAsync(
            new ReferenceLibraryInput("Other", "/reference/other", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var first = await AddSongAsync(context, "First");
        var second = await AddSongAsync(context, "Second");
        var elsewhere = await AddSongAsync(context, "Elsewhere");
        var downloaded = await AddSongAsync(context, "Downloaded");

        context.SongFiles.AddRange(
            NewFile(first, "/reference/mine/a.flac", SourceTypes.Reference, library.Library.Id),
            NewFile(second, "/reference/mine/b.flac", SourceTypes.Reference, library.Library.Id),
            NewFile(elsewhere, "/reference/other/c.flac", SourceTypes.Reference, other.Library.Id),
            NewFile(downloaded, "/data/music/d.flac", SourceTypes.Soulseek, library.Library.Id));
        await context.SaveChangesAsync();

        (await service.CountOwnedSongsAsync(library.Library.Id, CancellationToken.None)).Should().Be(2);
        (await service.CountOwnedSongsAsync(other.Library.Id, CancellationToken.None)).Should().Be(1);
        (await service.CountOwnedSongsAsync(987654, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(ReferenceLibraryDeleteChoice.Search, true)]
    [InlineData(ReferenceLibraryDeleteChoice.Wanted, true)]
    [InlineData(ReferenceLibraryDeleteChoice.Unmonitor, false)]
    public async Task Deleting_a_library_applies_the_choice_to_the_songs_it_owned(
        ReferenceLibraryDeleteChoice choice,
        bool stillMonitored)
    {
        await using var context = await ContextAsync();
        var service = Service(context);

        var doomed = await service.AddAsync(
            new ReferenceLibraryInput("Doomed", "/reference/doomed", ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        var owned = await AddSongAsync(context, "Owned");
        var untouched = await AddSongAsync(context, "Untouched");

        context.SongFiles.Add(NewFile(owned, "/reference/doomed/a.flac", SourceTypes.Reference, doomed.Library.Id));
        await context.SaveChangesAsync();

        (await service.DeleteAsync(doomed.Library.Id, choice, CancellationToken.None)).Should().Be(1);

        context.ChangeTracker.Clear();

        // Whatever the choice, the song has lost the reference file: it is wanted unless it is unmonitored.
        (await context.SongFiles.AnyAsync(file => file.SongId == owned)).Should().BeFalse();
        (await context.Songs.AsNoTracking().SingleAsync(song => song.Id == owned)).Monitored.Should().Be(stillMonitored);
        (await context.Songs.AsNoTracking().SingleAsync(song => song.Id == untouched)).Monitored.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_an_unknown_library_is_a_missing_row()
    {
        await using var context = await ContextAsync();

        (await Service(context).DeleteAsync(987654, ReferenceLibraryDeleteChoice.Wanted, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("/data/bin", true)]
    [InlineData("/data/bin/", true)]
    [InlineData("/data/bin/collection", true)]
    [InlineData("/data", true)]
    [InlineData("/data/bin2", false)]
    [InlineData("/other/music", false)]
    public async Task A_root_that_is_holds_or_sits_inside_the_recycle_bin_is_refused(string root, bool refused)
    {
        await using var context = await ContextAsync();
        var bin = Path.GetFullPath("/data/bin");
        var input = new ReferenceLibraryInput("Mine", Path.GetFullPath(root), ReferenceLibraryMode.Reference, null, true);

        var add = async () => await Service(context, bin).AddAsync(input, CancellationToken.None);

        if (refused)
        {
            (await add.Should().ThrowAsync<ReferenceLibraryValidationException>())
                .Which.Detail.Should().Contain("overlaps the recycle bin").And.Contain(bin);

            // An update is refused the same way.
            var other = await Service(context).AddAsync(
                input with { Name = "Other", RootPath = Path.GetFullPath("/other/elsewhere") },
                CancellationToken.None);
            var update = async () => await Service(context, bin).UpdateAsync(other.Library.Id, input, CancellationToken.None);

            await update.Should().ThrowAsync<ReferenceLibraryValidationException>();
        }
        else
        {
            await add.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task A_root_inside_the_default_recycle_bin_is_refused()
    {
        await using var context = await ContextAsync();
        var inside = Path.Combine(ConfigDir, "recycle", "collection");
        var input = new ReferenceLibraryInput("Mine", inside, ReferenceLibraryMode.Reference, null, true);

        var add = async () => await Service(context).AddAsync(input, CancellationToken.None);

        (await add.Should().ThrowAsync<ReferenceLibraryValidationException>())
            .Which.Detail.Should().Contain("overlaps the recycle bin").And.Contain(Path.Combine(ConfigDir, "recycle"));
    }

    [Fact]
    public async Task A_relative_recycle_bin_path_is_compared_as_the_absolute_path_it_resolves_to()
    {
        await using var context = await ContextAsync();
        var input = new ReferenceLibraryInput(
            "Mine",
            Path.Combine(Path.GetFullPath("relative-bin"), "collection"),
            ReferenceLibraryMode.Reference,
            null,
            true);

        var add = async () => await Service(context, "relative-bin").AddAsync(input, CancellationToken.None);

        await add.Should().ThrowAsync<ReferenceLibraryValidationException>();
    }

    // Owner question 5: a reference library may not be the file system root, Wondarr's data directory
    // or anything inside it. The recycle bin is moved out of the way so its own rule does not answer first.
    [Theory]
    [InlineData("config")]
    [InlineData("inside")]
    [InlineData("root")]
    public async Task A_root_that_is_the_data_directory_or_inside_it_or_the_file_system_root_is_refused(string which)
    {
        await using var context = await ContextAsync();
        var root = which switch
        {
            "config" => ConfigDir,
            "inside" => Path.Combine(ConfigDir, "backups"),
            _ => Path.GetPathRoot(ConfigDir)!,
        };
        var input = new ReferenceLibraryInput("Mine", root, ReferenceLibraryMode.Reference, null, true);

        var add = async () => await Service(context, recycleBin: Path.GetFullPath("/elsewhere-bin")).AddAsync(input, CancellationToken.None);

        var exception = await add.Should().ThrowAsync<ReferenceLibraryValidationException>();
        exception.Which.Field.Should().Be("rootPath");

        // A root above the recycle bin is also refused by the bin's own rule, which answers first.
        if (which != "root")
        {
            exception.Which.Detail.Should().StartWith("rootPath ").And.Contain("Wondarr keeps its API key");
        }
    }

    [Fact]
    public async Task A_library_already_inside_the_data_directory_stays_editable_until_its_root_moves()
    {
        await using var context = await ContextAsync();
        var inside = Path.Combine(ConfigDir, "old-collection");
        var bin = Path.GetFullPath("/elsewhere-bin");

        context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Old", RootPath = inside, Mode = ReferenceLibraryMode.Reference, Enabled = true });
        await context.SaveChangesAsync();
        var id = (await context.ReferenceLibraries.SingleAsync()).Id;

        var renamed = await Service(context, bin).UpdateAsync(
            id,
            new ReferenceLibraryInput("Renamed", inside, ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        renamed!.Library.Name.Should().Be("Renamed");

        var moved = async () => await Service(context, bin).UpdateAsync(
            id,
            new ReferenceLibraryInput("Renamed", Path.Combine(ConfigDir, "another"), ReferenceLibraryMode.Reference, null, true),
            CancellationToken.None);

        await moved.Should().ThrowAsync<ReferenceLibraryValidationException>();
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static readonly string ConfigDir = Path.GetFullPath("/wondarr-test-config");

    private static ReferenceLibraryService Service(WondarrDbContext context, string? recycleBin = null) =>
        new(
            context,
            new TestOptionsMonitor<ImportOptions>(new ImportOptions { RecycleBinPath = recycleBin }),
            new WondarrPaths(ConfigDir));

    private static SongFile WithSourceRef(SongFile file, string sourceRef)
    {
        file.SourceRef = sourceRef;

        return file;
    }

    private static SongFile NewFile(long songId, string path, string sourceType, long referenceLibraryId) =>
        new()
        {
            SongId = songId,
            Path = path,
            SourceType = sourceType,
            // Every row names the library, whatever its source: only the source type can keep a
            // downloaded file whose reference names the doomed library.
            SourceRef = $"{{\"referenceLibraryId\":{referenceLibraryId},\"referenceFileId\":1}}",
            QualityId = 1,
        };

    /// <summary>A song with a file-less row of its own, so a reference file can be attached to it.</summary>
    private static async Task<long> AddSongAsync(WondarrDbContext context, string title)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        context.Artists.Add(artist);

        var song = new Song
        {
            Title = title,
            ArtistCredit = "Daft Punk",
            PrimaryArtist = artist,
            Monitored = true,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = 1,
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    private static void AddFile(
        WondarrDbContext context,
        long libraryId,
        string relativePath,
        ReferenceFileState state) =>
        context.ReferenceFiles.Add(new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            Size = 1,
            ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            State = state,
        });

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }
}
