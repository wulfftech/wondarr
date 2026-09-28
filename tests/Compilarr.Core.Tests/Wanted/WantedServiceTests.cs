using Compilarr.Core.Domain;
using Compilarr.Core.Paging;
using Compilarr.Core.Persistence;
using Compilarr.Core.Tests.Persistence;
using Compilarr.Core.Wanted;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Wanted;

public sealed class WantedServiceTests
{
    /// <summary>The seeded quality ids the cases use: MP3-256, AAC-256 and MP3-320.</summary>
    private const long Mp3256 = 23;
    private const long Aac256 = 25;
    private const long Mp3320 = 29;

    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Missing_lists_the_monitored_song_without_a_file()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        await SeedLifecycleAsync(database, time);

        var page = await Service(database, time).GetMissingAsync(new PagingSpec(1, 20, null, true), CancellationToken.None);

        page.TotalRecords.Should().Be(1);
        page.Records.Should().ContainSingle().Which.Title.Should().Be("Alpha");
        page.Records[0].AlbumContext.Should().BeNull();
    }

    [Fact]
    public async Task Cutoff_lists_only_the_monitored_song_below_the_profile_cutoff()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        await SeedLifecycleAsync(database, time);

        var page = await Service(database, time).GetCutoffUnmetAsync(new PagingSpec(1, 20, null, true), CancellationToken.None);

        // MP3-256 is below the Standard 320 cutoff; MP3-320 is at it and AAC-256 is in the cutoff group.
        page.TotalRecords.Should().Be(1);
        page.Records.Should().ContainSingle().Which.Title.Should().Be("Gamma");
        page.Records[0].File.Should().NotBeNull();
    }

    [Fact]
    public async Task Paging_returns_the_second_song_by_the_requested_sort()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        await SeedThreeMissingAsync(database, time);

        var service = Service(database, time);

        var byTitle = await service.GetMissingAsync(new PagingSpec(2, 1, "title", descending: false), CancellationToken.None);

        byTitle.TotalRecords.Should().Be(3);
        byTitle.Records.Should().ContainSingle().Which.Title.Should().Be("Bravo");

        // The keys are case-insensitive, and a title sort runs the other way when asked.
        var descendingTitle = await service.GetMissingAsync(new PagingSpec(2, 1, "TITLE", descending: true), CancellationToken.None);
        descendingTitle.Records.Should().ContainSingle().Which.Title.Should().Be("Bravo");

        // No key, and an unknown key, both fall back to "added".
        var byAdded = await service.GetMissingAsync(new PagingSpec(2, 1, "nonsense", descending: true), CancellationToken.None);
        byAdded.Records.Should().ContainSingle().Which.Title.Should().Be("Bravo");
    }

    [Fact]
    public async Task The_added_sort_defaults_to_newest_first()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        await SeedThreeMissingAsync(database, time);

        var page = await Service(database, time).GetMissingAsync(new PagingSpec(1, 20, null, true), CancellationToken.None);

        page.Records.Select(song => song.Title).Should().ContainInOrder("Charlie", "Bravo", "Alpha");
    }

    [Fact]
    public void Paging_spec_clamps_the_request()
    {
        var clamped = new PagingSpec(page: 0, pageSize: 99_999, sortKey: "  ", descending: false);

        clamped.Page.Should().Be(1);
        clamped.PageSize.Should().Be(PagingSpec.MaxPageSize);
        clamped.SortKey.Should().BeNull();
        clamped.Skip.Should().Be(0);

        new PagingSpec(3, 0, "title", false).PageSize.Should().Be(PagingSpec.MinPageSize);
        new PagingSpec(3, 10, "title", false).Skip.Should().Be(20);
    }

    private static WantedService Service(SqliteTestDatabase database, FakeTimeProvider time) =>
        new(database.CreateContext(time));

    /// <summary>
    /// A (monitored, no file), B (unmonitored, no file), C (MP3-256), D (MP3-320) and E (AAC-256) —
    /// the five cases the task's acceptance criterion names.
    /// </summary>
    private static async Task SeedLifecycleAsync(SqliteTestDatabase database, FakeTimeProvider time)
    {
        await using var context = database.CreateContext(time);

        var artist = new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var a = NewSong(artist, "Alpha", monitored: true);
        var b = NewSong(artist, "Beta", monitored: false);
        var c = NewSong(artist, "Gamma", monitored: true);
        var d = NewSong(artist, "Delta", monitored: true);
        var e = NewSong(artist, "Epsilon", monitored: true);

        context.Songs.AddRange(a, b, c, d, e);
        await context.SaveChangesAsync();

        context.SongFiles.AddRange(NewFile(c, Mp3256), NewFile(d, Mp3320), NewFile(e, Aac256));
        await context.SaveChangesAsync();
    }

    /// <summary>Three monitored songs with no files, one per minute so the "added" sort is total.</summary>
    private static async Task SeedThreeMissingAsync(SqliteTestDatabase database, FakeTimeProvider time)
    {
        await using var context = database.CreateContext(time);

        var artist = new Artist { Name = "Boards of Canada", SortName = "Boards of Canada" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        foreach (var title in new[] { "Alpha", "Bravo", "Charlie" })
        {
            context.Songs.Add(NewSong(artist, title, monitored: true));
            await context.SaveChangesAsync();

            time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private static Song NewSong(Artist artist, string title, bool monitored) => new()
    {
        Title = title,
        ArtistCredit = artist.Name,
        PrimaryArtistId = artist.Id,
        Monitored = monitored,
        QualityProfileId = SeedData.StandardProfileId,
        LibraryId = SeedData.DefaultLibraryId,
        AddedBy = "api",
    };

    private static SongFile NewFile(Song song, long qualityId) => new()
    {
        SongId = song.Id,
        Path = $"/data/music/{song.Title}.mp3",
        Size = 1024,
        Codec = "mp3",
        Container = "mpeg",
        QualityId = qualityId,
        SourceType = "soulseek",
        ImportedAt = Now,
    };
}
