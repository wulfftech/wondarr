using Wondarr.Core.Domain;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>The song list's filters and sort keys against a real migrated SQLite database.</summary>
public sealed class SongListFilterTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Each_filter_alone_narrows_the_list()
    {
        await using var context = await ContextAsync();
        var second = await SongSeed.AddAsync(context, "Alpha", "Daft Punk", fileQuality: SongSeed.HighQuality, tags: ["chill"]);
        await SongSeed.AddAsync(context, "Bravo", "Justice", monitored: false);
        await SongSeed.AddAsync(context, "Charlie", "Justice", fileQuality: SongSeed.LowQuality, profileId: SeedData.LosslessProfileId);
        var library = await SongSeed.AddLibraryAsync(context);
        await SongSeed.AddAsync(context, "Delta", "Justice", libraryId: library.Id);

        var service = SongSeed.Service(context);

        (await TitlesAsync(service, new SongListFilter { Monitored = false })).Should().Equal("Bravo");
        (await TitlesAsync(service, new SongListFilter { HasFile = true })).Should().Equal("Alpha", "Charlie");
        (await TitlesAsync(service, new SongListFilter { HasFile = false })).Should().Equal("Bravo", "Delta");
        (await TitlesAsync(service, new SongListFilter { LibraryId = library.Id })).Should().Equal("Delta");
        (await TitlesAsync(service, new SongListFilter { QualityProfileId = SeedData.LosslessProfileId })).Should().Equal("Charlie");
        (await TitlesAsync(service, new SongListFilter { QualityId = SongSeed.HighQuality })).Should().Equal("Alpha");
        (await TitlesAsync(service, new SongListFilter { Tag = "chill" })).Should().Equal("Alpha");
        (await TitlesAsync(service, new SongListFilter { ArtistId = second.PrimaryArtistId })).Should().Equal("Alpha");
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        await using var context = await ContextAsync();
        await SongSeed.AddAsync(context, "Alpha", fileQuality: SongSeed.HighQuality, tags: ["chill"]);
        await SongSeed.AddAsync(context, "Alpine", fileQuality: SongSeed.LowQuality, tags: ["chill"]);
        await SongSeed.AddAsync(context, "Alps", fileQuality: SongSeed.HighQuality);

        var service = SongSeed.Service(context);

        (await TitlesAsync(service, new SongListFilter { Tag = "chill", QualityId = SongSeed.HighQuality })).Should().Equal("Alpha");
        (await TitlesAsync(service, new SongListFilter { Term = "alp", HasFile = true, Tag = "chill" }))
            .Should().Equal("Alpha", "Alpine");
    }

    [Fact]
    public async Task Term_matches_title_or_credit_case_insensitively_and_treats_wildcards_literally()
    {
        await using var context = await ContextAsync();
        await SongSeed.AddAsync(context, "100% Pure", "Someone");
        await SongSeed.AddAsync(context, "1000 Pure", "Someone");
        await SongSeed.AddAsync(context, "snake_case", "Someone");
        await SongSeed.AddAsync(context, "snakeXcase", "Someone");
        await SongSeed.AddAsync(context, "Back\\slash", "Someone");
        await SongSeed.AddAsync(context, "Other", "DAFT punk");

        var service = SongSeed.Service(context);

        (await TitlesAsync(service, new SongListFilter { Term = "100%" })).Should().Equal("100% Pure");
        (await TitlesAsync(service, new SongListFilter { Term = "e_c" })).Should().Equal("snake_case");
        (await TitlesAsync(service, new SongListFilter { Term = "k\\s" })).Should().Equal("Back\\slash");
        (await TitlesAsync(service, new SongListFilter { Term = "PURE" })).Should().Equal("100% Pure", "1000 Pure");
        (await TitlesAsync(service, new SongListFilter { Term = "daft" })).Should().Equal("Other");
    }

    [Fact]
    public async Task Tag_matches_whatever_case_it_was_written_in()
    {
        await using var context = await ContextAsync();
        await SongSeed.AddAsync(context, "Alpha", tags: ["chill"]);
        await SongSeed.AddAsync(context, "Bravo");

        (await TitlesAsync(SongSeed.Service(context), new SongListFilter { Tag = "ChIlL" })).Should().Equal("Alpha");
    }

    [Fact]
    public async Task Cutoff_met_splits_songs_with_a_file_and_leaves_out_songs_without_one()
    {
        await using var context = await ContextAsync();
        await SongSeed.AddAsync(context, "Met1", fileQuality: SongSeed.HighQuality);
        await SongSeed.AddAsync(context, "Unmet1", fileQuality: SongSeed.LowQuality);
        await SongSeed.AddAsync(context, "Unmet2", fileQuality: SongSeed.LowQuality);
        await SongSeed.AddAsync(context, "Unmet3", fileQuality: SongSeed.LowQuality);
        await SongSeed.AddAsync(context, "NoFile");

        var service = SongSeed.Service(context);

        (await TitlesAsync(service, new SongListFilter { CutoffMet = true })).Should().Equal("Met1");
        (await TitlesAsync(service, new SongListFilter { CutoffMet = false })).Should().Equal("Unmet1", "Unmet2", "Unmet3");

        var page2 = await service.GetPageAsync(
            new PagingSpec(2, 2, "title", descending: false),
            new SongListFilter { CutoffMet = false },
            CancellationToken.None);

        page2.TotalRecords.Should().Be(3);
        page2.Records.Select(song => song.Title).Should().Equal("Unmet3");
    }

    [Fact]
    public async Task Quality_monitored_and_library_are_sort_keys()
    {
        await using var context = await ContextAsync();
        var library = await SongSeed.AddLibraryAsync(context);
        await SongSeed.AddAsync(context, "B", fileQuality: SongSeed.HighQuality, monitored: false);
        await SongSeed.AddAsync(context, "A", fileQuality: SongSeed.LowQuality, libraryId: library.Id);
        await SongSeed.AddAsync(context, "C", fileQuality: SongSeed.HighQuality);

        var service = SongSeed.Service(context);

        (await SortedAsync(service, "quality", descending: false)).Should().Equal("A", "B", "C");
        (await SortedAsync(service, "quality", descending: true)).Should().Equal("B", "C", "A");
        (await SortedAsync(service, "monitored", descending: false)).Should().Equal("B", "A", "C");
        (await SortedAsync(service, "library", descending: true)).Should().Equal("A", "B", "C");
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<List<string>> TitlesAsync(SongService service, SongListFilter filter)
    {
        var page = await service.GetPageAsync(new PagingSpec(1, 50, "title", descending: false), filter, CancellationToken.None);

        return [.. page.Records.Select(song => song.Title)];
    }

    private static async Task<List<string>> SortedAsync(SongService service, string key, bool descending)
    {
        var page = await service.GetPageAsync(new PagingSpec(1, 50, key, descending), new SongListFilter(), CancellationToken.None);

        return [.. page.Records.Select(song => song.Title)];
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }
}
