using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>The shared filing graph: what each of its two shapes loads, and what the credits come out as.</summary>
public sealed class SongQueriesTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Filing_graph_loads_album_file_and_credits_but_not_the_files_quality()
    {
        var songId = await SeedAsync();

        // A fresh context: a quality already tracked by the seeding one would be fixed up regardless.
        await using var context = _database.CreateContext(_time);
        var song = await context.Songs.IncludeFilingGraph().SingleAsync(row => row.Id == songId);

        song.AlbumContext.Should().NotBeNull();
        song.File.Should().NotBeNull();
        song.Artists.Should().ContainSingle().Which.Artist.Name.Should().Be("Daft Punk");
        context.Entry(song.File!).Reference(file => file.Quality).IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task Filing_graph_with_quality_also_loads_the_files_quality()
    {
        var songId = await SeedAsync();

        await using var context = _database.CreateContext(_time);
        var song = await context.Songs.IncludeFilingGraphWithQuality().SingleAsync(row => row.Id == songId);

        song.AlbumContext.Should().NotBeNull();
        song.File.Should().NotBeNull();
        song.Artists.Should().ContainSingle().Which.Artist.Name.Should().Be("Daft Punk");
        context.Entry(song.File!).Reference(file => file.Quality).IsLoaded.Should().BeTrue();
        song.File!.Quality.Id.Should().Be(SongSeed.HighQuality);
    }

    [Fact]
    public async Task Ordered_credits_follow_the_credit_position_not_the_row_order()
    {
        await _database.MigrateAsync(_time);
        long songId;

        await using (var seed = _database.CreateContext(_time))
        {
            var song = await SongSeed.AddAsync(seed, "Alpha", "Daft Punk");
            var guest = new Artist { Name = "Pharrell Williams", SortName = "Williams, Pharrell" };

            // Stored after the main credit (position 1) but positioned before it.
            song.Artists.Add(new SongArtist { Artist = guest, Role = ArtistRole.Featured, Position = 0 });
            await seed.SaveChangesAsync();
            songId = song.Id;
        }

        await using var context = _database.CreateContext(_time);
        var loaded = await context.Songs.IncludeFilingGraph().SingleAsync(row => row.Id == songId);

        loaded.OrderedCredits().Select(credit => (credit.Artist.Name, credit.Role)).Should().Equal(
            ("Pharrell Williams", ArtistRole.Featured),
            ("Daft Punk", ArtistRole.Main));
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<long> SeedAsync()
    {
        await _database.MigrateAsync(_time);
        await using var seed = _database.CreateContext(_time);

        return (await SongSeed.AddAsync(seed, "Alpha", "Daft Punk", fileQuality: SongSeed.HighQuality)).Id;
    }
}
