using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Domain;

/// <summary>
/// The Phase 1 tables against a real migrated SQLite database: the seed data, the JSON round-tripping
/// and the delete behaviours the rest of Phase 1 relies on.
/// </summary>
public class DomainModelPersistenceTests
{
    [Fact]
    public async Task Migrating_a_fresh_database_seeds_the_quality_ladder()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var qualities = await context.Qualities.OrderBy(x => x.Id).ToListAsync();

        qualities.Should().HaveCount(43);
        qualities.Select(x => x.Id).Should().Equal(Enumerable.Range(1, 43).Select(id => (long)id));

        qualities[0].Name.Should().Be("Unknown");
        qualities[0].Group.Should().Be("Unknown");
        qualities[0].Rank.Should().Be(1);
        qualities[28].Name.Should().Be("MP3-320");
        qualities[28].Group.Should().Be("High lossy");
        qualities[28].MinBitrate.Should().Be(320);
        qualities[35].Name.Should().Be("FLAC");
        qualities[35].Lossless.Should().BeTrue();
        qualities[35].BitDepth.Should().Be(16);
        qualities[39].Name.Should().Be("FLAC 24-bit");
        qualities[39].BitDepth.Should().Be(24);
        qualities[42].Name.Should().Be("AIFF");
        qualities[42].Rank.Should().Be(9);
    }

    [Fact]
    public async Task Migrating_a_fresh_database_seeds_the_two_profiles_with_their_items()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);

        var standard = await context.QualityProfiles.SingleAsync(x => x.Id == SeedData.StandardProfileId);
        standard.Name.Should().Be("Standard 320");
        standard.CutoffQualityId.Should().Be(29);
        standard.UpgradeAllowed.Should().BeTrue();
        standard.MinScore.Should().Be(0);
        standard.DurationToleranceMs.Should().Be(3000);
        standard.Items.Should().HaveCount(13);
        standard.Items[3].Name.Should().Be("Low lossy");
        standard.Items[3].Allowed.Should().BeTrue();
        standard.Items[3].QualityIds.Should().Equal(17L, 18L, 19L, 22L);
        standard.Items[7].QualityIds.Should().Equal(29L, 30L, 25L, 31L, 32L);
        standard.Items[12].Name.Should().BeNull();

        var lossless = await context.QualityProfiles.SingleAsync(x => x.Id == SeedData.LosslessProfileId);
        lossless.Name.Should().Be("Lossless");
        lossless.CutoffQualityId.Should().Be(36);
        lossless.Items.Should().HaveCount(7);
        lossless.Items[0].QualityIds.Should().HaveCount(28);
        lossless.Items[0].Allowed.Should().BeFalse();

        // Stored as camelCase JSON, which is what the API hands the UI.
        var rawItems = await database.ReadStringsAsync(
            $"SELECT items FROM quality_profile WHERE id = {SeedData.StandardProfileId};");
        rawItems.Should().ContainSingle()
            .Which.Should().StartWith("[{\"name\":null,\"qualityIds\":[1],\"allowed\":false},");
    }

    [Fact]
    public async Task Migrating_a_fresh_database_seeds_the_default_plexamp_library()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var libraries = await context.Libraries.ToListAsync();

        libraries.Should().ContainSingle();
        var library = libraries[0];
        library.Id.Should().Be(SeedData.DefaultLibraryId);
        library.Name.Should().Be("Music");
        library.RootPath.Should().Be("/data/music");
        library.Layout.Should().Be(LibraryLayout.Plexamp);
        library.NamingTemplate.Should().Be("{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}");
        library.SidecarOptions.Should().Be("{}");
        library.AlbumPolicy.Should().Be(AlbumPolicy.FewestAlbums);
        library.MinTracksPerRealAlbum.Should().Be(2);
        library.PlexSectionId.Should().BeNull();
        library.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task A_song_round_trips_with_its_credits_album_context_and_file()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        var songId = await AddSongAsync(database, timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var song = await context.Songs
            .Include(x => x.Artists).ThenInclude(x => x.Artist)
            .Include(x => x.AlbumContext)
            .Include(x => x.File)
            .SingleAsync(x => x.Id == songId);

        song.Title.Should().Be("Get Lucky");
        song.ArtistCredit.Should().Be("Daft Punk feat. Pharrell Williams");
        song.Isrcs.Should().Equal("USQX91300108", "GBAYE1300102");
        song.VersionFlags.Should().Equal("remaster");
        song.Tags.Should().Equal("funk");
        song.Monitored.Should().BeTrue();
        song.AddedBy.Should().Be("ui");
        song.QualityProfileId.Should().Be(SeedData.StandardProfileId);
        song.SourceProfileId.Should().BeNull();
        song.LibraryId.Should().Be(SeedData.DefaultLibraryId);
        song.PrimaryArtist.Name.Should().Be("Daft Punk");

        song.Artists.Should().HaveCount(2);
        song.Artists.OrderBy(x => x.Position).Select(x => x.Role)
            .Should().Equal(ArtistRole.Main, ArtistRole.Featured);
        song.Artists.Single(x => x.Position == 2).Artist.Name.Should().Be("Pharrell Williams");

        song.AlbumContext.Should().NotBeNull();
        song.AlbumContext!.Kind.Should().Be(AlbumContextKind.Album);
        song.AlbumContext.AlbumTitle.Should().Be("Random Access Memories");
        song.AlbumContext.AlbumArtist.Should().Be("Daft Punk");
        song.AlbumContext.AlbumKey.Should().Be("a3f0f1c2-1111-4c2e-9d4f-000000000001");
        song.AlbumContext.TrackNo.Should().Be(8);
        song.AlbumContext.Date.Should().Be("2013-05-17");
        song.AlbumContext.IsVariousArtists.Should().BeFalse();
        song.AlbumContext.Sticky.Should().BeTrue();

        song.File.Should().NotBeNull();
        song.File!.Path.Should().Be("/data/music/Daft Punk/Random Access Memories/01 - Get Lucky.flac");
        song.File.Size.Should().Be(31_457_280);
        song.File.Codec.Should().Be("flac");
        song.File.Container.Should().Be("flac");
        song.File.BitrateKbps.Should().Be(1010);
        song.File.SampleRate.Should().Be(44100);
        song.File.BitDepth.Should().Be(16);
        song.File.DurationMs.Should().Be(369_000);
        song.File.QualityId.Should().Be(36);
        song.File.SourceType.Should().Be("soulseek");
        song.File.SourceRef.Should().Be("{\"username\":\"peer\",\"filename\":\"Get Lucky.flac\"}");
        song.File.FingerprintVerified.Should().BeFalse();
        song.File.ImportedAt.Should().Be(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Deleting_a_song_deletes_its_credits_album_context_and_file_but_not_the_artists()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        var songId = await AddSongAsync(database, timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            var song = await context.Songs.SingleAsync(x => x.Id == songId);
            context.Songs.Remove(song);
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateContext(timeProvider))
        {
            (await context.Songs.CountAsync()).Should().Be(0);
            (await context.SongArtists.CountAsync()).Should().Be(0);
            (await context.AlbumContexts.CountAsync()).Should().Be(0);
            (await context.SongFiles.CountAsync()).Should().Be(0);
            (await context.Artists.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task The_unique_index_on_mb_recording_id_rejects_a_second_song()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await AddSongAsync(database, timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var artistId = await context.Artists.Select(x => x.Id).FirstAsync();
        context.Songs.Add(new Song
        {
            Title = "Get Lucky (radio edit)",
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artistId,
            MbRecordingId = "b1a9c0f2-2222-4c2e-9d4f-000000000002",
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_song_cannot_have_a_second_album_context()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        var songId = await AddSongAsync(database, timeProvider);

        await using var context = database.CreateContext(timeProvider);
        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = songId,
            Kind = AlbumContextKind.Single,
            AlbumTitle = "Get Lucky",
            AlbumArtist = "Daft Punk",
            AlbumKey = "a3f0f1c2-1111-4c2e-9d4f-000000000009",
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Adds one song with two credits, an album context and a file, and returns its id.</summary>
    [Fact]
    public async Task False_booleans_and_explicit_values_survive_the_round_trip()
    {
        // Regression: a database default on a bool column makes EF skip an explicit `false` on
        // insert (it is the CLR default), so an unmonitored song came back monitored.
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        long songId;
        await using (var context = database.CreateContext(timeProvider))
        {
            var artist = new Artist { Name = "Nobody", SortName = "Nobody" };
            var song = new Song
            {
                Title = "Quiet",
                ArtistCredit = "Nobody",
                PrimaryArtist = artist,
                Monitored = false,
                QualityProfileId = SeedData.StandardProfileId,
                LibraryId = SeedData.DefaultLibraryId,
                AddedBy = "api",
            };
            song.AlbumContext = new AlbumContext
            {
                Kind = AlbumContextKind.PseudoSingles,
                AlbumTitle = "Singles",
                AlbumArtist = "Nobody",
                AlbumKey = "00000000-0000-0000-0000-000000000001",
                Sticky = false,
            };
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;
        }

        await using var readBack = database.CreateContext(timeProvider);
        var stored = await readBack.Songs.Include(x => x.AlbumContext).SingleAsync(x => x.Id == songId);
        stored.Monitored.Should().BeFalse();
        stored.AlbumContext!.Sticky.Should().BeFalse();
    }

    private static async Task<long> AddSongAsync(SqliteTestDatabase database, TimeProvider timeProvider)
    {
        await using var context = database.CreateContext(timeProvider);

        var mainArtist = new Artist
        {
            Name = "Daft Punk",
            SortName = "Daft Punk",
            MbArtistId = "056e4f3e-3333-4dad-8ec1-000000000003",
            Tags = ["electronic"],
        };

        var featuredArtist = new Artist
        {
            Name = "Pharrell Williams",
            SortName = "Williams, Pharrell",
            DeezerId = 1234,
        };

        var song = new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk feat. Pharrell Williams",
            PrimaryArtist = mainArtist,
            MbRecordingId = "b1a9c0f2-2222-4c2e-9d4f-000000000002",
            MbWorkId = "c2b0d1e3-4444-4c2e-9d4f-000000000004",
            Isrcs = ["USQX91300108", "GBAYE1300102"],
            SpotifyId = "spotify:track:2Foc5Q5nqNiosCNqttzHof",
            DeezerId = 6_789_012,
            YtmVideoId = "5NV6Rdv1a3I",
            DurationMs = 369_000,
            VersionFlags = ["remaster"],
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "ui",
            Tags = ["funk"],
        };

        song.Artists.Add(new SongArtist { Artist = mainArtist, Role = ArtistRole.Main, Position = 1 });
        song.Artists.Add(new SongArtist { Artist = featuredArtist, Role = ArtistRole.Featured, Position = 2 });

        song.AlbumContext = new AlbumContext
        {
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = "Daft Punk",
            AlbumKey = "a3f0f1c2-1111-4c2e-9d4f-000000000001",
            MbReleaseId = "d3c1e2f4-5555-4c2e-9d4f-000000000005",
            MbReleaseGroupId = "e4d2f3a5-6666-4c2e-9d4f-000000000006",
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = "2013-05-17",
            OriginalDate = "2013",
            Label = "Columbia",
            CoverUrl = "https://coverartarchive.org/release/d3c1e2f4-5555-4c2e-9d4f-000000000005/front",
        };

        song.File = new SongFile
        {
            Path = "/data/music/Daft Punk/Random Access Memories/01 - Get Lucky.flac",
            Size = 31_457_280,
            Codec = "flac",
            Container = "flac",
            BitrateKbps = 1010,
            SampleRate = 44100,
            BitDepth = 16,
            Channels = 2,
            DurationMs = 369_000,
            QualityId = 36,
            SourceType = "soulseek",
            SourceRef = "{\"username\":\"peer\",\"filename\":\"Get Lucky.flac\"}",
            ImportedAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            TagsWritten = "{\"title\":\"Get Lucky\"}",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }
}
