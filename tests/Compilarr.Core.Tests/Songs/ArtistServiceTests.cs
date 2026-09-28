using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using Compilarr.Core.Songs;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Songs;

/// <summary>The artist list over a real migrated SQLite database.</summary>
public class ArtistServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public async Task Artists_are_ordered_by_sort_name_and_count_every_credit()
    {
        await using var context = await ContextAsync();

        var daft = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        var guest = new Artist { Name = "Pharrell Williams", SortName = "Williams, Pharrell" };
        var idle = new Artist { Name = "Nile Rodgers", SortName = "Rodgers, Nile" };

        context.Songs.Add(Song("Get Lucky", daft, [Credit(daft, ArtistRole.Main, 1), Credit(guest, ArtistRole.Featured, 2)]));
        context.Songs.Add(Song("Doin' It Right", daft, [Credit(daft, ArtistRole.Main, 1)]));
        context.Artists.Add(idle);
        await context.SaveChangesAsync();

        var service = new ArtistService(context);
        var artists = await service.GetAllAsync();

        artists.Should().HaveCount(3);
        artists.Select(summary => summary.Artist.Name).Should().Equal("Daft Punk", "Nile Rodgers", "Pharrell Williams");
        artists.Single(summary => summary.Artist.Name == "Daft Punk").SongCount.Should().Be(2);
        artists.Single(summary => summary.Artist.Name == "Pharrell Williams").SongCount.Should().Be(1);
        artists.Single(summary => summary.Artist.Name == "Nile Rodgers").SongCount.Should().Be(0);
    }

    [Fact]
    public async Task Getting_an_artist_returns_it_or_null()
    {
        await using var context = await ContextAsync();

        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var service = new ArtistService(context);

        (await service.GetAsync(artist.Id)).Should().NotBeNull();
        (await service.GetAsync(artist.Id))!.Name.Should().Be("Daft Punk");
        (await service.GetAsync(9999)).Should().BeNull();
    }

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<CompilarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private static SongArtist Credit(Artist artist, ArtistRole role, int position) => new()
    {
        Artist = artist,
        Role = role,
        Position = position,
    };

    private static Song Song(string title, Artist primary, List<SongArtist> credits)
    {
        var song = new Song
        {
            Title = title,
            ArtistCredit = string.Join(", ", credits.Select(credit => credit.Artist.Name)),
            PrimaryArtist = primary,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "test",
        };

        song.Artists.AddRange(credits);

        return song;
    }
}