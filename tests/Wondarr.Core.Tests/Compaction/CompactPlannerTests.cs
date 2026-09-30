using Wondarr.Core.Compaction;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Compaction;

/// <summary>
/// The Compact library dry run against a real migrated SQLite database: a library that grew one song
/// at a time is re-planned as if none of it were placed, and the play is reported — never made.
/// </summary>
public class CompactPlannerTests : IDisposable
{
    /// <summary>The seeded FLAC quality, which the re-plan reads off the file row.</summary>
    private const long FlacQualityId = 36;

    private const string CoverUrl = "https://cover.example/album.jpg";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly ICoverArtResolver _coverArt = Substitute.For<ICoverArtResolver>();
    private readonly Dictionary<string, SongIdentity> _identities = new(StringComparer.Ordinal);

    public CompactPlannerTests()
    {
        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt(CoverUrl, "coverartarchive"));

        _resolver
            .GetIdentityAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(call => _identities.TryGetValue(call.ArgAt<string?>(0) ?? string.Empty, out var identity)
                ? identity
                : (SongIdentity?)null);
    }

    [Fact]
    public async Task Four_songs_added_one_at_a_time_are_planned_into_the_one_release_they_share()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        for (var index = 1; index <= 4; index++)
        {
            await SeedLooseSingleAsync(context, artistId, index);
            StubIdentity(
                "m-" + index,
                Single("s-" + index, "Single " + index),
                Album("r1", "Random Access Memories", index));
        }

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.AlbumsBefore.Should().Be(4);
        plan.AlbumsAfter.Should().Be(1);
        plan.SongsConsidered.Should().Be(4);
        plan.Moves.Should().HaveCount(4);
        plan.Moves.Should().AllSatisfy(move =>
        {
            move.To.AlbumKey.Should().Be("r1");
            move.To.Kind.Should().Be(AlbumContextKind.Album);
            move.ToPath.Should().NotBeNull();
            move.ToPath.Should().StartWith(Placed("Daft Punk/Random Access Memories/"));
        });

        // The plan says where each file goes, and the numbering comes from the release's own tracks.
        plan.Moves
            .Select(move => move.ToPath)
            .Should()
            .BeEquivalentTo(
            [
                Placed("Daft Punk/Random Access Memories/01 - Track 1.flac"),
                Placed("Daft Punk/Random Access Memories/02 - Track 2.flac"),
                Placed("Daft Punk/Random Access Memories/03 - Track 3.flac"),
                Placed("Daft Punk/Random Access Memories/04 - Track 4.flac"),
            ]);
    }

    [Fact]
    public async Task A_song_whose_album_does_not_change_is_not_a_move()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        // Two songs are already on the album, and their track numbers would change under the re-plan.
        await SeedSongAsync(context, artistId, "m-1", "Track 1", "r1", AlbumContextKind.Album, "Random Access Memories", trackNo: 9);
        await SeedSongAsync(context, artistId, "m-2", "Track 2", "r1", AlbumContextKind.Album, "Random Access Memories", trackNo: 8);
        await SeedLooseSingleAsync(context, artistId, 3);

        StubIdentity("m-1", Album("r1", "Random Access Memories", 1));
        StubIdentity("m-2", Album("r1", "Random Access Memories", 2));
        StubIdentity("m-3", Single("s-3", "Single 3"), Album("r1", "Random Access Memories", 3));

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.SongsConsidered.Should().Be(3);
        plan.AlbumsBefore.Should().Be(2);
        plan.AlbumsAfter.Should().Be(1);
        plan.Moves.Should().ContainSingle().Which.SongId.Should().Be(SongId(context, "m-3"));
    }

    [Fact]
    public async Task A_pinned_song_keeps_its_album_and_other_songs_join_it()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        var pinned = await SeedSongAsync(
            context,
            artistId,
            "m-1",
            "Track 1",
            "r1",
            AlbumContextKind.Album,
            "Random Access Memories",
            trackNo: 1,
            pinned: true);

        await SeedLooseSingleAsync(context, artistId, 2);
        await SeedLooseSingleAsync(context, artistId, 3);

        StubIdentity("m-1", Album("r1", "Random Access Memories", 1));
        StubIdentity("m-2", Single("s-2", "Single 2"), Album("r1", "Random Access Memories", 2));
        StubIdentity("m-3", Single("s-3", "Single 3"), Album("r1", "Random Access Memories", 3));

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.SongsConsidered.Should().Be(2);
        plan.AlbumsBefore.Should().Be(3);
        plan.AlbumsAfter.Should().Be(1);
        plan.Moves.Should().HaveCount(2);
        plan.Moves.Should().NotContain(move => move.SongId == pinned);
        plan.Moves.Should().AllSatisfy(move => move.To.AlbumKey.Should().Be("r1"));
    }

    [Fact]
    public async Task A_reference_file_is_a_move_that_touches_no_path()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        var managed = await SeedLooseSingleAsync(context, artistId, 1, filePath: "/data/music/Daft Punk/Single 1/01 - Track 1.flac");
        var reference = await SeedLooseSingleAsync(
            context,
            artistId,
            2,
            filePath: "/home/listener/Music/Daft Punk - Track 2.flac",
            sourceType: SourceTypes.Reference);

        StubIdentity("m-1", Single("s-1", "Single 1"), Album("r1", "Random Access Memories", 1));
        StubIdentity("m-2", Single("s-2", "Single 2"), Album("r1", "Random Access Memories", 2));

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.Moves.Should().HaveCount(2);

        var moved = plan.Moves.Single(move => move.SongId == managed);
        moved.FromPath.Should().Be("/data/music/Daft Punk/Single 1/01 - Track 1.flac");
        moved.ToPath.Should().Be(Placed("Daft Punk/Random Access Memories/01 - Track 1.flac"));

        // A reference file lives in the user's own folder, so the move only changes the album context.
        var borrowed = plan.Moves.Single(move => move.SongId == reference);
        borrowed.FromPath.Should().BeNull();
        borrowed.ToPath.Should().BeNull();
    }

    [Fact]
    public async Task A_song_with_no_identity_is_left_out_of_the_plan()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        await SeedLooseSingleAsync(context, artistId, 1);
        await SeedLooseSingleAsync(context, artistId, 2);
        await SeedSongAsync(context, artistId, null, "Unresolved", "s-9", AlbumContextKind.Single, "Single 9", trackNo: 1);

        StubIdentity("m-1", Single("s-1", "Single 1"), Album("r1", "Random Access Memories", 1));
        StubIdentity("m-2", Single("s-2", "Single 2"), Album("r1", "Random Access Memories", 2));

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.SongsConsidered.Should().Be(2);
        plan.Moves.Should().HaveCount(2);

        // It keeps its album, so the library is left with the release plus that one loose single.
        plan.AlbumsBefore.Should().Be(3);
        plan.AlbumsAfter.Should().Be(2);
    }

    [Fact]
    public async Task A_song_that_falls_into_singles_reuses_the_artist_s_existing_pseudo_album_key()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        await SeedSongAsync(context, artistId, "m-1", "Track 1", "pseudo-1", AlbumContextKind.PseudoSingles, "Singles", trackNo: 1);
        await SeedLooseSingleAsync(context, artistId, 2);

        StubIdentity("m-1", Single("s-1", "Single 1"));
        StubIdentity("m-2", Single("s-2", "Single 2"));

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);

        plan.AlbumsBefore.Should().Be(2);
        plan.AlbumsAfter.Should().Be(1);

        var move = plan.Moves.Should().ContainSingle().Subject;
        move.To.AlbumKey.Should().Be("pseudo-1");
        move.To.Kind.Should().Be(AlbumContextKind.PseudoSingles);
        move.To.AlbumTitle.Should().Be("Singles");
    }

    [Fact]
    public async Task Planning_writes_nothing_to_the_database()
    {
        await using var context = await ContextAsync();
        var artistId = await SeedArtistAsync(context);

        for (var index = 1; index <= 3; index++)
        {
            await SeedLooseSingleAsync(context, artistId, index);
            StubIdentity("m-" + index, Single("s-" + index, "Single " + index), Album("r1", "Random Access Memories", index));
        }

        var plan = await NewPlanner(context).PlanAsync(SeedData.DefaultLibraryId, CancellationToken.None);
        plan.Moves.Should().HaveCount(3);

        context.ChangeTracker.HasChanges().Should().BeFalse();

        await using var fresh = _database.CreateContext(_timeProvider);
        var stored = await fresh.AlbumContexts
            .OrderBy(album => album.SongId)
            .Select(album => album.AlbumKey)
            .ToListAsync();

        stored.Should().Equal("s-1", "s-2", "s-3");
        (await fresh.AlbumContexts.AnyAsync(album => album.Pinned)).Should().BeFalse();
    }

    [Fact]
    public async Task Planning_an_unknown_library_throws()
    {
        await using var context = await ContextAsync();

        var plan = async () => await NewPlanner(context).PlanAsync(987654, CancellationToken.None);

        await plan.Should().ThrowAsync<KeyNotFoundException>();
    }

    /// <summary>Deletes this test's temp database.</summary>
    /// <summary>
    /// Where the placer would put a file under the library root, joined the way it joins paths on the
    /// machine running the test (a <c>/data/music</c> root gains a drive letter on Windows).
    /// </summary>
    private static string Placed(string relative) =>
        Path.GetFullPath(Path.Combine("/data/music", relative.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private CompactPlanner NewPlanner(WondarrDbContext context)
    {
        var counter = 0;

        var songs = new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            NullLogger<SongService>.Instance);

        return new CompactPlanner(context, songs);
    }

    private static long SongId(WondarrDbContext context, string mbRecordingId) =>
        context.Songs.AsNoTracking().Single(song => song.MbRecordingId == mbRecordingId).Id;

    private static async Task<long> SeedArtistAsync(WondarrDbContext context)
    {
        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" };

        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        return artist.Id;
    }

    /// <summary>One song of an artist, filed under its own one-track album and holding a file.</summary>
    private static async Task<long> SeedSongAsync(
        WondarrDbContext context,
        long artistId,
        string? mbRecordingId,
        string title,
        string albumKey,
        AlbumContextKind kind,
        string albumTitle,
        int? trackNo,
        bool pinned = false,
        string? filePath = null,
        string sourceType = SourceTypes.Soulseek)
    {
        var song = new Song
        {
            Title = title,
            ArtistCredit = "Daft Punk",
            PrimaryArtistId = artistId,
            MbRecordingId = mbRecordingId,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongArtists.Add(new SongArtist
        {
            SongId = song.Id,
            ArtistId = artistId,
            Role = ArtistRole.Main,
            Position = 1,
        });

        context.AlbumContexts.Add(new AlbumContext
        {
            SongId = song.Id,
            Kind = kind,
            AlbumTitle = albumTitle,
            AlbumArtist = "Daft Punk",
            AlbumKey = albumKey,
            MbReleaseId = kind == AlbumContextKind.PseudoSingles ? null : albumKey,
            MbReleaseGroupId = kind == AlbumContextKind.PseudoSingles ? null : albumKey + "-group",
            TrackNo = trackNo,
            DiscNo = 1,
            TotalTracks = 1,
            Date = "2013-05-17",
            Pinned = pinned,
        });

        if (filePath is not null)
        {
            context.SongFiles.Add(new SongFile
            {
                SongId = song.Id,
                Path = filePath,
                Size = 30_000_000,
                Codec = "flac",
                Container = "flac",
                BitrateKbps = 1000,
                SampleRate = 44_100,
                BitDepth = 16,
                Channels = 2,
                DurationMs = 369_000,
                QualityId = FlacQualityId,
                SourceType = sourceType,
                ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            });
        }

        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <summary>One song filed under its own one-track single, as the add path leaves a loose song.</summary>
    private static Task<long> SeedLooseSingleAsync(
        WondarrDbContext context,
        long artistId,
        int index,
        string? filePath = null,
        string sourceType = SourceTypes.Soulseek) =>
        SeedSongAsync(
            context,
            artistId,
            "m-" + index,
            "Track " + index,
            "s-" + index,
            AlbumContextKind.Single,
            "Single " + index,
            trackNo: 1,
            filePath: filePath ?? $"/data/music/Daft Punk/Single {index}/01 - Track {index}.flac",
            sourceType: sourceType);

    private void StubIdentity(string mbRecordingId, params ReleaseOption[] options) =>
        _identities[mbRecordingId] = new SongIdentity
        {
            Source = "musicbrainz",
            MbRecordingId = mbRecordingId,
            Title = mbRecordingId,
            ArtistCredit = "Daft Punk",
            Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
            OriginalDate = "2013",
            ReleaseOptions = options,
            CoverUrl = CoverUrl,
        };

    private static ReleaseOption Album(string key, string title, int trackNo) => new()
    {
        Key = key,
        MbReleaseId = key,
        MbReleaseGroupId = key + "-group",
        Title = title,
        AlbumArtist = "Daft Punk",
        PrimaryType = "Album",
        Status = "Official",
        Date = "2013-05-17",
        TrackNo = trackNo,
        DiscNo = 1,
        TotalTracks = 4,
    };

    private static ReleaseOption Single(string key, string title) => new()
    {
        Key = key,
        MbReleaseId = key,
        MbReleaseGroupId = key + "-group",
        Title = title,
        AlbumArtist = "Daft Punk",
        PrimaryType = "Single",
        Status = "Official",
        Date = "2013-04-19",
        TrackNo = 1,
        DiscNo = 1,
        TotalTracks = 1,
    };
}
