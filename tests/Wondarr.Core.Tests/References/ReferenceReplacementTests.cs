using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// Which songs held by a reference file may be replaced (DECISIONS build session 14 #32): the row the
/// song's file points at decides, by its own Replace answer or its library's setting.
/// </summary>
public sealed class ReferenceReplacementTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Nothing_is_replaceable_until_someone_asks()
    {
        await using var context = await ContextAsync();
        var library = await LibraryAsync(context, "Music", replaces: false);
        var song = await SongAsync(context, "Hip to Be Square");
        await HoldAsync(context, song, await RowAsync(context, library, song, replace: false));

        (await ReferenceReplacement.ReplaceableSongsAsync(context, null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_row_the_song_holds_decides()
    {
        await using var context = await ContextAsync();
        var keeping = await LibraryAsync(context, "Music", replaces: false);
        var replacing = await LibraryAsync(context, "Rips", replaces: true);

        // Held by the file in the library that keeps its files; a second copy sits in the one that
        // replaces them. The second copy does not make the song replaceable.
        var kept = await SongAsync(context, "The Power of Love");
        await HoldAsync(context, kept, await RowAsync(context, keeping, kept, replace: false));
        await RowAsync(context, replacing, kept, replace: false);

        // Held by the copy in the replacing library: replaceable, whatever the other copy says.
        var replaced = await SongAsync(context, "Hip to Be Square");
        await RowAsync(context, keeping, replaced, replace: false);
        await HoldAsync(context, replaced, await RowAsync(context, replacing, replaced, replace: false));

        // Held by a row settled with Replace, in the library that keeps its files.
        var asked = await SongAsync(context, "Stuck with You");
        await HoldAsync(context, asked, await RowAsync(context, keeping, asked, replace: true));

        (await ReferenceReplacement.ReplaceableSongsAsync(context, null, CancellationToken.None))
            .Should().BeEquivalentTo([replaced, asked]);
        (await ReferenceReplacement.ReplaceableSongsAsync(context, [kept, asked], CancellationToken.None))
            .Should().BeEquivalentTo([asked], "only the songs asked about are answered");
        (await ReferenceReplacement.IsReplaceableAsync(context, kept, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_song_whose_held_file_is_missing_for_now_stays_replaceable()
    {
        await using var context = await ContextAsync();
        var replacing = await LibraryAsync(context, "Rips", replaces: true);
        var song = await SongAsync(context, "Hip to Be Square");
        var row = await RowAsync(context, replacing, song, replace: false);
        await HoldAsync(context, song, row);

        row.State = ReferenceFileState.Missing;
        await context.SaveChangesAsync();

        (await ReferenceReplacement.IsReplaceableAsync(context, song, CancellationToken.None))
            .Should().BeTrue("the song still holds the file through the grace period, and a better copy is what was asked for");
    }

    [Fact]
    public async Task A_song_with_a_library_file_is_not_a_reference_song_at_all()
    {
        await using var context = await ContextAsync();
        var replacing = await LibraryAsync(context, "Rips", replaces: true);
        var song = await SongAsync(context, "Hip to Be Square");
        var row = await RowAsync(context, replacing, song, replace: true);

        context.SongFiles.Add(new SongFile
        {
            SongId = song,
            Path = "/data/music/Hip to Be Square.flac",
            SourceType = SourceTypes.Soulseek,
            SourceRef = $"{{\"referenceLibraryId\":{replacing.Id},\"referenceFileId\":{row.Id}}}",
            QualityId = 36,
        });
        await context.SaveChangesAsync();

        (await ReferenceReplacement.ReplaceableSongsAsync(context, null, CancellationToken.None))
            .Should().BeEmpty("the rule is about songs held by a reference file; a library file is upgraded as always");
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private static async Task<ReferenceLibrary> LibraryAsync(WondarrDbContext context, string name, bool replaces)
    {
        var library = new ReferenceLibrary { Name = name, RootPath = $"/reference/{name}", ReplaceBelowCutoff = replaces };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        return library;
    }

    private static async Task<long> SongAsync(WondarrDbContext context, string title)
    {
        var song = new Song
        {
            Title = title,
            ArtistCredit = "Huey Lewis and the News",
            PrimaryArtist = new Artist { Name = $"Huey Lewis and the News {title}", SortName = "Lewis, Huey" },
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            Monitored = true,
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    private static async Task<ReferenceFile> RowAsync(WondarrDbContext context, ReferenceLibrary library, long songId, bool replace)
    {
        var row = new ReferenceFile
        {
            ReferenceLibraryId = library.Id,
            RelativePath = $"{songId}.mp3",
            SongId = songId,
            State = ReferenceFileState.Identified,
            LikelyYouTubeRip = true,
            Replace = replace,
        };
        context.ReferenceFiles.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    /// <summary>Makes the row the song's file, as a scan's link does.</summary>
    private static async Task HoldAsync(WondarrDbContext context, long songId, ReferenceFile row)
    {
        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = $"/reference/{row.ReferenceLibraryId}/{row.RelativePath}",
            SourceType = SourceTypes.Reference,
            SourceRef = $"{{\"referenceLibraryId\":{row.ReferenceLibraryId},\"referenceFileId\":{row.Id}}}",
            QualityId = SeedData.Opus160QualityId,
        });
        await context.SaveChangesAsync();
    }
}
