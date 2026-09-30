using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Sources;
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

        (await service.DeleteAsync(doomed.Library.Id, CancellationToken.None)).Should().BeTrue();

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
    public async Task Deleting_an_unknown_library_is_a_missing_row()
    {
        await using var context = await ContextAsync();

        (await Service(context).DeleteAsync(987654, CancellationToken.None)).Should().BeFalse();
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static ReferenceLibraryService Service(WondarrDbContext context) => new(context);

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
