using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// The add pipeline against a real migrated SQLite database and the real album policy engine: what a
/// batch writes, how a later batch joins what the first one created, and the CRUD around the songs.
/// </summary>
public class SongServiceTests : IDisposable
{
    private const string CoverUrl = "https://cover.example/album.jpg";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly ICoverArtResolver _coverArt = Substitute.For<ICoverArtResolver>();

    [Fact]
    public async Task A_batch_of_three_songs_of_one_artist_fills_a_real_album_and_a_pseudo_album()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"), (8, "m-b"));

        var results = await NewService(context).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"), Single("s-a", "Get Lucky")),
                Identity("m-b", "Lose Yourself to Dance", Album("r1", "Random Access Memories")),
                Identity("m-c", "Doin' It Right", Single("s-c", "Doin' It Right")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(result => result.Outcome.Should().Be(SongAddOutcome.Added));

        var songs = await context.Songs.Include(song => song.AlbumContext).OrderBy(song => song.Id).ToListAsync();

        songs.Should().HaveCount(3);
        songs[0].AlbumContext!.AlbumKey.Should().Be("r1");
        songs[0].AlbumContext!.Kind.Should().Be(AlbumContextKind.Album);
        songs[0].AlbumContext!.TrackNo.Should().Be(3);
        songs[0].AlbumContext!.DiscNo.Should().Be(1);
        songs[0].AlbumContext!.TotalTracks.Should().Be(13);
        songs[0].AlbumContext!.Sticky.Should().BeTrue();
        songs[1].AlbumContext!.AlbumKey.Should().Be("r1");
        songs[1].AlbumContext!.TrackNo.Should().Be(8);

        // The single covers only one song, so it falls short of the two-track minimum and becomes a
        // pseudo-album instead.
        songs[2].AlbumContext!.Kind.Should().Be(AlbumContextKind.PseudoSingles);
        songs[2].AlbumContext!.AlbumTitle.Should().Be("Singles");
        songs[2].AlbumContext!.AlbumKey.Should().NotBe("r1");

        (await context.Artists.CountAsync()).Should().Be(1);
        await _musicBrainz.Received(1).GetReleaseAsync("r1", Arg.Any<CancellationToken>());
        await _coverArt.Received(2).ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_later_batch_joins_the_stored_album_and_the_stored_singles_album_without_a_new_cover()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"), (8, "m-b"), (5, "m-d"));

        var service = NewService(context);
        await service.AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories")),
                Identity("m-b", "Lose Yourself to Dance", Album("r1", "Random Access Memories")),
                Identity("m-c", "Doin' It Right", Single("s-c", "Doin' It Right")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        var survivors = await context.Songs.Include(song => song.AlbumContext).ToListAsync();
        var pseudoKey = survivors.Single(song => song.MbRecordingId == "m-c").AlbumContext!.AlbumKey;

        _coverArt.ClearReceivedCalls();

        var results = await service.AddIdentitiesAsync(
            [
                Identity("m-d", "Instant Crush", Album("r1", "Random Access Memories")),
                Identity("m-e", "Motherboard", Single("s-e", "Motherboard")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        results.Should().AllSatisfy(result => result.Outcome.Should().Be(SongAddOutcome.Added));

        var joined = await FindSongAsync(context, "m-d");
        joined.AlbumContext!.AlbumKey.Should().Be("r1");
        joined.AlbumContext!.TrackNo.Should().Be(5);
        joined.AlbumContext!.CoverUrl.Should().Be(CoverUrl);

        var loose = await FindSongAsync(context, "m-e");
        loose.AlbumContext!.AlbumKey.Should().Be(pseudoKey);
        loose.AlbumContext!.TrackNo.Should().Be(2);

        await _coverArt.DidNotReceive().ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_adding_a_stored_recording_is_already_exists_with_the_stored_song()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var first = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"))],
            new SongAddOptions(),
            CancellationToken.None);

        var again = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"))],
            new SongAddOptions(),
            CancellationToken.None);

        again[0].Outcome.Should().Be(SongAddOutcome.AlreadyExists);
        again[0].Song.Id.Should().Be(first[0].Song.Id);
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_same_identity_twice_in_one_batch_becomes_one_song()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var results = await NewService(context).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky")),
                Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        results[0].Outcome.Should().Be(SongAddOutcome.Added);
        results[1].Outcome.Should().Be(SongAddOutcome.AlreadyExists);
        results[1].Song.Should().BeSameAs(results[0].Song);
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Featured_credits_keep_their_roles_and_a_known_artist_is_reused()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        context.Artists.Add(new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" });
        await context.SaveChangesAsync();

        var identity = new SongIdentity
        {
            Source = "musicbrainz",
            MbRecordingId = "m-a",
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk feat. Pharrell Williams & Nile Rodgers",
            Artists =
            [
                new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0),
                new IdentityArtist("Pharrell Williams", "Williams, Pharrell", "a2", null, ArtistRole.Featured, 1),
                new IdentityArtist("Nile Rodgers", "Rodgers, Nile", "a3", null, ArtistRole.Featured, 2),
            ],
            ReleaseOptions = [Album("r1", "Random Access Memories")],
        };

        await NewService(context).AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        var song = await context.Songs
            .Include(candidate => candidate.Artists).ThenInclude(credit => credit.Artist)
            .SingleAsync();

        song.Artists.Should().HaveCount(3);
        song.Artists.OrderBy(credit => credit.Position).Select(credit => credit.Role)
            .Should().Equal(ArtistRole.Main, ArtistRole.Featured, ArtistRole.Featured);
        song.Artists.OrderBy(credit => credit.Position).Select(credit => credit.Artist.Name)
            .Should().Equal("Daft Punk", "Pharrell Williams", "Nile Rodgers");

        // The pre-seeded Daft Punk row was matched by its MBID, not created a second time.
        (await context.Artists.CountAsync()).Should().Be(3);
        song.PrimaryArtistId.Should().Be((await context.Artists.SingleAsync(artist => artist.MbArtistId == "a1")).Id);
    }

    [Fact]
    public async Task A_deezer_only_identity_uses_its_own_cover_and_never_reads_a_release()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var library = await context.Libraries.SingleAsync();
        library.MinTracksPerRealAlbum = 1;
        await context.SaveChangesAsync();

        var identity = new SongIdentity
        {
            Source = "deezer",
            DeezerId = 42,
            Title = "Around the World",
            ArtistCredit = "Daft Punk",
            Artists = [new IdentityArtist("Daft Punk", null, null, 7L, ArtistRole.Main, 0)],
            OriginalDate = "1997-01-17",
            CoverUrl = "https://deezer.example/cover.jpg",
            ReleaseOptions =
            [
                new ReleaseOption
                {
                    Key = "deezer-album-key",
                    Title = "Homework",
                    AlbumArtist = "Daft Punk",
                    PrimaryType = "Single",
                    Status = "Official",
                    Date = "1997-01-17",
                },
            ],
        };

        await NewService(context).AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        var song = await context.Songs.Include(candidate => candidate.AlbumContext).SingleAsync();
        song.DeezerId.Should().Be(42);
        song.MbRecordingId.Should().BeNull();
        song.AlbumContext!.AlbumKey.Should().Be("deezer-album-key");
        song.AlbumContext!.Kind.Should().Be(AlbumContextKind.Single);
        song.AlbumContext!.CoverUrl.Should().Be("https://deezer.example/cover.jpg");
        (await context.Artists.SingleAsync()).DeezerId.Should().Be(7);

        await _musicBrainz.DidNotReceive().GetReleaseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _coverArt.DidNotReceive().ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_singles_only_policy_files_the_whole_batch_into_the_pseudo_album()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var library = await context.Libraries.SingleAsync();
        library.AlbumPolicy = AlbumPolicy.SinglesOnly;
        await context.SaveChangesAsync();

        await NewService(context).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories")),
                Identity("m-b", "Lose Yourself to Dance", Album("r1", "Random Access Memories")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        var contexts = await context.AlbumContexts.ToListAsync();
        contexts.Should().HaveCount(2);
        contexts.Should().AllSatisfy(album =>
        {
            album.Kind.Should().Be(AlbumContextKind.PseudoSingles);
            album.AlbumKey.Should().Be(contexts[0].AlbumKey);
        });
    }

    [Fact]
    public async Task The_compilation_policy_files_the_whole_batch_into_one_various_artists_album()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var library = await context.Libraries.SingleAsync();
        library.AlbumPolicy = AlbumPolicy.Compilation;
        await context.SaveChangesAsync();

        await NewService(context).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories")),
                Identity("m-b", "Lose Yourself to Dance", Album("r1", "Random Access Memories")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        var contexts = await context.AlbumContexts.OrderBy(album => album.TrackNo).ToListAsync();
        contexts.Should().HaveCount(2);
        contexts.Should().AllSatisfy(album =>
        {
            album.Kind.Should().Be(AlbumContextKind.Compilation);
            album.AlbumTitle.Should().Be("Music");
            album.AlbumArtist.Should().Be("Various Artists");
            album.IsVariousArtists.Should().BeTrue();
            album.AlbumKey.Should().Be(contexts[0].AlbumKey);
        });
        contexts.Select(album => album.TrackNo).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Songs_page_by_title_and_filter_by_artist_and_monitored()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var added = await service.AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky")),
                Featured("m-b", "Lose Yourself to Dance", "Pharrell Williams", "a2", "Williams, Pharrell"),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        var guest = await context.Artists.SingleAsync(artist => artist.MbArtistId == "a2");
        await service.UpdateAsync(added[1].Song.Id, monitored: false, qualityProfileId: null, CancellationToken.None);

        var byTitle = await service.GetPageAsync(
            new PagingSpec(1, 10, "title", descending: false),
            artistId: null,
            monitored: null,
            CancellationToken.None);

        byTitle.TotalRecords.Should().Be(2);
        byTitle.Records.Select(song => song.Title).Should().Equal("Get Lucky", "Lose Yourself to Dance");

        var featuredOnly = await service.GetPageAsync(
            new PagingSpec(1, 10, null, descending: false),
            artistId: guest.Id,
            monitored: null,
            CancellationToken.None);

        featuredOnly.TotalRecords.Should().Be(1);
        featuredOnly.Records[0].MbRecordingId.Should().Be("m-b");

        var unmonitored = await service.GetPageAsync(
            new PagingSpec(1, 10, null, descending: false),
            artistId: null,
            monitored: false,
            CancellationToken.None);

        unmonitored.TotalRecords.Should().Be(1);
        unmonitored.Records[0].MbRecordingId.Should().Be("m-b");
    }

    [Fact]
    public async Task Updating_a_song_flips_monitored_and_rejects_an_unknown_profile()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var added = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"))],
            new SongAddOptions(),
            CancellationToken.None);

        var updated = await service.UpdateAsync(
            added[0].Song.Id,
            monitored: false,
            qualityProfileId: SeedData.LosslessProfileId,
            CancellationToken.None);

        updated!.Monitored.Should().BeFalse();
        updated.QualityProfileId.Should().Be(SeedData.LosslessProfileId);

        var reject = async () => await service.UpdateAsync(
            added[0].Song.Id,
            monitored: null,
            qualityProfileId: 999,
            CancellationToken.None);

        await reject.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Deleting_a_song_removes_it_and_its_context_but_not_the_artist()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var added = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"))],
            new SongAddOptions(),
            CancellationToken.None);

        (await service.DeleteAsync(added[0].Song.Id, CancellationToken.None)).Should().BeTrue();
        (await service.DeleteAsync(added[0].Song.Id, CancellationToken.None)).Should().BeFalse();

        (await context.Songs.CountAsync()).Should().Be(0);
        (await context.AlbumContexts.CountAsync()).Should().Be(0);
        (await context.SongArtists.CountAsync()).Should().Be(0);
        (await context.Artists.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_explicit_album_choice_moves_the_song_onto_that_release()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        added[0].Song.AlbumContext!.Kind.Should().Be(AlbumContextKind.PseudoSingles);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(identity with { ReleaseOptions = [Single("s-a", "Get Lucky"), Album("r1", "Random Access Memories")] });

        var moved = await service.SetAlbumContextAsync(added[0].Song.Id, "r1", CancellationToken.None);

        moved!.AlbumContext!.AlbumKey.Should().Be("r1");
        moved.AlbumContext.Kind.Should().Be(AlbumContextKind.Album);
        moved.AlbumContext.AlbumTitle.Should().Be("Random Access Memories");
        moved.AlbumContext.Sticky.Should().BeTrue();
        moved.AlbumContext.CoverUrl.Should().Be(CoverUrl);
    }

    [Fact]
    public async Task The_singles_key_moves_the_song_to_the_artists_pseudo_album()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var library = await context.Libraries.SingleAsync();
        library.MinTracksPerRealAlbum = 1;
        await context.SaveChangesAsync();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        added[0].Song.AlbumContext!.AlbumKey.Should().Be("r1");

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var moved = await service.SetAlbumContextAsync(added[0].Song.Id, "singles", CancellationToken.None);

        moved!.AlbumContext!.Kind.Should().Be(AlbumContextKind.PseudoSingles);
        moved.AlbumContext.AlbumTitle.Should().Be("Singles");
        moved.AlbumContext.AlbumKey.Should().NotBe("r1");
        moved.AlbumContext.Sticky.Should().BeTrue();
    }

    [Fact]
    public async Task An_explicit_album_choice_pins_the_song_and_adding_one_does_not()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        added[0].Song.AlbumContext!.Pinned.Should().BeFalse();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var moved = await service.SetAlbumContextAsync(added[0].Song.Id, "singles", CancellationToken.None);

        moved!.AlbumContext!.Pinned.Should().BeTrue();
        (await FindSongAsync(context, "m-a")).AlbumContext!.Pinned.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_album_key_and_an_unknown_song_are_rejected()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var unknownKey = async () => await service.SetAlbumContextAsync(added[0].Song.Id, "nope", CancellationToken.None);
        await unknownKey.Should().ThrowAsync<ArgumentException>();

        (await service.SetAlbumContextAsync(12345, "singles", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Album_options_come_from_the_resolver_and_are_empty_for_an_unknown_song()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var options = await service.GetAlbumOptionsAsync(added[0].Song.Id, CancellationToken.None);
        options.Should().ContainSingle().Which.Key.Should().Be("r1");

        (await service.GetAlbumOptionsAsync(12345, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Getting_a_song_returns_null_for_an_unknown_id()
    {
        await using var context = await ContextAsync();

        (await NewService(context).GetAsync(12345, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Adding_one_id_goes_through_the_resolver()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky")));

        var service = NewService(context);
        var added = await service.AddAsync("m-a", null, new SongAddOptions(), CancellationToken.None);

        added.Outcome.Should().Be(SongAddOutcome.Added);
        added.Identity.MbRecordingId.Should().Be("m-a");

        _resolver.ClearReceivedCalls();
        _resolver.GetIdentityAsync("m-nope", null, Arg.Any<CancellationToken>()).Returns((SongIdentity?)null);

        var missing = async () => await service.AddAsync("m-nope", null, new SongAddOptions(), CancellationToken.None);
        await missing.Should().ThrowAsync<SongNotFoundException>();

        var nothing = async () => await service.AddAsync(null, null, new SongAddOptions(), CancellationToken.None);
        await nothing.Should().ThrowAsync<SongNotFoundException>();
    }

    /// <summary>Deletes this test's temp database.</summary>
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

    private SongService NewService(WondarrDbContext context)
    {
        var counter = 0;

        return new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            NullLogger<SongService>.Instance);
    }

    private void StubCoverArt() =>
        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt(CoverUrl, "coverartarchive"));

    private void StubRelease(string releaseId, params (int TrackNo, string RecordingId)[] tracks) =>
        _musicBrainz.GetReleaseAsync(releaseId, Arg.Any<CancellationToken>())
            .Returns(new MbRelease
            {
                Id = releaseId,
                Title = "Release " + releaseId,
                Status = "Official",
                Date = "2013-05-17",
                Media =
                [
                    new MbMedium
                    {
                        Position = 1,
                        TrackCount = 13,
                        Tracks = [.. tracks.Select(track => new MbTrack
                        {
                            Position = track.TrackNo,
                            Recording = new MbRecording { Id = track.RecordingId },
                        })],
                    },
                ],
            });

    private static Task<Song> FindSongAsync(WondarrDbContext context, string mbRecordingId) =>
        context.Songs
            .AsNoTracking()
            .Include(song => song.AlbumContext)
            .SingleAsync(song => song.MbRecordingId == mbRecordingId);

    private static SongIdentity Identity(string mbRecordingId, string title, params ReleaseOption[] options) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
        OriginalDate = "2013",
        ReleaseOptions = options,
    };

    private static SongIdentity Featured(
        string mbRecordingId,
        string title,
        string guestName,
        string guestMbId,
        string guestSortName) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = $"Daft Punk feat. {guestName}",
        Artists =
        [
            new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0),
            new IdentityArtist(guestName, guestSortName, guestMbId, null, ArtistRole.Featured, 1),
        ],
        OriginalDate = "2013",
        ReleaseOptions = [Single("s-b", title)],
    };

    private static ReleaseOption Album(string key, string title) => new()
    {
        Key = key,
        MbReleaseId = key,
        MbReleaseGroupId = key + "-group",
        Title = title,
        AlbumArtist = "Daft Punk",
        PrimaryType = "Album",
        Status = "Official",
        Date = "2013-05-17",
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
    };
}
