using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists.References;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The reference-library import list: its identified files, in folder order, joined to their songs;
/// and the refusals when the library is unknown or the settings carry no id.
/// </summary>
public sealed class ReferenceLibraryListProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Reads_the_identified_files_in_path_order_with_their_mbids()
    {
        await using var context = await ContextAsync();
        var library = new ReferenceLibrary { Name = "Music", RootPath = "/reference/music" };
        context.ReferenceLibraries.Add(library);
        var lucky = await SeedSongAsync(context, "Get Lucky", "22222222-2222-2222-2222-222222222222");
        var rhapsody = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111");

        // Out of path order on purpose: the list is the folder's order, not the scan's.
        var second = AddFile(context, library.Id, "b/Bohemian Rhapsody.flac", rhapsody);
        var first = AddFile(context, library.Id, "a/Get Lucky.flac", lucky);
        AddFile(context, library.Id, "c/unidentified.flac", null);
        await context.SaveChangesAsync();

        var provider = new ReferenceLibraryListProvider(context);

        var result = await provider.FetchAsync(List(library.Id), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Entries.Select(entry => entry.ExternalId)
            .Should().Equal([$"ref:{first.Id}", $"ref:{second.Id}"]);
        result.Entries[0].Artist.Should().Be("Get Lucky");
        result.Entries[0].Title.Should().Be("Get Lucky");
        result.Entries[0].MbRecordingId.Should().Be("22222222-2222-2222-2222-222222222222");
        result.Entries[1].MbRecordingId.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task An_unknown_library_fails_the_read()
    {
        await using var context = await ContextAsync();
        var provider = new ReferenceLibraryListProvider(context);

        var result = await provider.FetchAsync(List(999), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Entries.Should().BeEmpty();
        result.Error.Should().Be("Reference library 999 does not exist.");
    }

    [Fact]
    public async Task Validate_asks_for_a_positive_library_id()
    {
        await using var context = await ContextAsync();
        var provider = new ReferenceLibraryListProvider(context);

        provider.Validate(Json("{}"), null).Should().ContainSingle().Which.Should().Contain("reference library");
        provider.Validate(Json("""{"referenceLibraryId":0}"""), null)
            .Should().ContainSingle().Which.Should().Contain("reference library");
        provider.Validate(Json("""{"referenceLibraryId":-2}"""), null)
            .Should().ContainSingle().Which.Should().Contain("reference library");
        provider.Validate(Json("""{"referenceLibraryId":3}"""), null).Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }

    private static ImportList List(long libraryId) => new()
    {
        Type = ReferenceLibraryListProvider.ReferenceLibraryType,
        Settings = $$"""{"referenceLibraryId":{{libraryId}}}""",
    };

    private static async Task<Song> SeedSongAsync(WondarrDbContext context, string title, string mbRecordingId)
    {
        var artist = new Artist { Name = title, SortName = title };
        context.Artists.Add(artist);

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtist = artist,
            MbRecordingId = mbRecordingId,
            DurationMs = 200_000,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song;
    }

    private static ReferenceFile AddFile(WondarrDbContext context, long libraryId, string relativePath, Song? song)
    {
        var file = new ReferenceFile
        {
            ReferenceLibraryId = libraryId,
            RelativePath = relativePath,
            SongId = song?.Id,
            State = song is null ? ReferenceFileState.Pending : ReferenceFileState.Identified,
        };
        context.ReferenceFiles.Add(file);

        return file;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
