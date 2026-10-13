using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Paging;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public async Task An_identity_carrying_both_ids_finds_the_stored_deezer_only_song()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var first = await service.AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky")],
            new SongAddOptions(),
            CancellationToken.None);

        var again = await service.AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky", "m-a")],
            new SongAddOptions(),
            CancellationToken.None);

        again.Should().ContainSingle().Which.Outcome.Should().Be(SongAddOutcome.AlreadyExists);
        again[0].Song.Id.Should().Be(first[0].Song.Id);
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_deezer_only_identity_finds_the_stored_song_that_has_both_ids()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var first = await service.AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky", "m-a")],
            new SongAddOptions(),
            CancellationToken.None);

        var again = await service.AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky")],
            new SongAddOptions(),
            CancellationToken.None);

        again.Should().ContainSingle().Which.Outcome.Should().Be(SongAddOutcome.AlreadyExists);
        again[0].Song.Id.Should().Be(first[0].Song.Id);
    }

    [Fact]
    public async Task A_track_in_two_playlists_no_longer_fails_the_second_playlists_whole_batch()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);

        // Playlist 1 added the track as Deezer-only; MusicBrainz has since learnt its ISRC.
        await service.AddIdentitiesAsync([DeezerIdentity(42, "Get Lucky")], new SongAddOptions(), CancellationToken.None);

        var results = await service.AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky", "m-a"), DeezerIdentity(43, "Other Song", "m-b")],
            new SongAddOptions(),
            CancellationToken.None);

        results.Select(result => result.Outcome).Should().Equal(SongAddOutcome.AlreadyExists, SongAddOutcome.Added);
        (await context.Songs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task One_identity_without_an_artist_is_reported_and_the_rest_of_the_batch_is_added()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var nobody = new SongIdentity
        {
            Source = "musicbrainz",
            MbRecordingId = "m-bad",
            Title = "Nobody's Song",
            ArtistCredit = string.Empty,
            Artists = [],
        };

        var results = await NewService(context).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky")), nobody, Identity("m-b", "Instant Crush", Single("s-b", "Instant Crush"))],
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results.Select(result => result.Outcome)
            .Should().Equal(SongAddOutcome.Added, SongAddOutcome.Failed, SongAddOutcome.Added);
        results[1].Error.Should().NotBeNullOrEmpty();
        (await context.Songs.Select(song => song.MbRecordingId).OrderBy(id => id).ToListAsync())
            .Should().Equal("m-a", "m-b");
    }

    [Fact]
    public async Task One_identity_the_database_refuses_is_reported_and_the_rest_of_the_batch_is_added()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        // The database refuses the second song, whatever the service believes about the keys.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var batch = new[]
        {
            DeezerIdentity(42, "Get Lucky", "m-a"),
            DeezerIdentity(44, "Poison", "m-b"),
            DeezerIdentity(43, "Instant Crush", "m-c"),
        };

        var strict = async () => await NewService(context).AddIdentitiesAsync(batch, new SongAddOptions(), CancellationToken.None);
        await strict.Should().ThrowAsync<DbUpdateException>();
        context.ChangeTracker.Clear();

        var results = await NewService(context).AddIdentitiesAsync(
            batch,
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results.Select(result => result.Outcome)
            .Should().Equal(SongAddOutcome.Added, SongAddOutcome.Failed, SongAddOutcome.Added);
        results[1].Error.Should().NotBeNullOrEmpty();

        context.ChangeTracker.Clear();

        var songs = await context.Songs.AsNoTracking().Include(song => song.AlbumContext).OrderBy(song => song.Id).ToListAsync();
        songs.Select(song => song.DeezerId).Should().Equal(42L, 43L);
        songs.Should().OnlyContain(song => song.AlbumContext != null);
        (await context.Artists.CountAsync()).Should().Be(1, "the failed song's artist was the shared one, saved with the first song");
    }

    [Fact]
    public async Task Two_identities_of_one_batch_that_share_a_deezer_id_are_one_song()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var results = await NewService(context).AddIdentitiesAsync(
            [DeezerIdentity(42, "Get Lucky", "m-a"), DeezerIdentity(42, "Get Lucky", "m-b"), DeezerIdentity(42, "Get Lucky")],
            new SongAddOptions(),
            CancellationToken.None);

        results.Select(result => result.Outcome)
            .Should().Equal(SongAddOutcome.Added, SongAddOutcome.AlreadyExists, SongAddOutcome.AlreadyExists);
        results[1].Song.Should().BeSameAs(results[0].Song);
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_after_the_fallback_began_rolls_back_the_transaction_the_add_opened()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        // The failure is made by cancelling
        // the token as the first save fails, so the fallback throws something that is not a DbUpdateException.
        using var cancellation = new CancellationTokenSource();
        var batch = new[] { DeezerIdentity(44, "Poison", "m-b"), DeezerIdentity(43, "Instant Crush", "m-c") };

        context.SaveChangesFailed += (_, _) => cancellation.Cancel();

        var act = async () => await NewService(context).AddIdentitiesAsync(
            batch,
            new SongAddOptions { IsolateFailures = true, KeepTransactionOpen = true, SearchOnAdd = false },
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        context.Database.CurrentTransaction.Should().BeNull();
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

        var moved = (await service.SetAlbumContextAsync(added[0].Song.Id, "r1", CancellationToken.None))?.Song;

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

        var moved = (await service.SetAlbumContextAsync(added[0].Song.Id, "singles", CancellationToken.None))?.Song;

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

        var moved = (await service.SetAlbumContextAsync(added[0].Song.Id, "singles", CancellationToken.None))?.Song;

        moved!.AlbumContext!.Pinned.Should().BeTrue();
        (await FindSongAsync(context, "m-a")).AlbumContext!.Pinned.Should().BeTrue();
    }

    /// <summary>Whether a MissingSearch body names exactly these songs (R-034).</summary>
    private static bool NamesSongs(string? body, IEnumerable<long> songIds)
    {
        if (body is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(body);
        var named = document.RootElement.GetProperty("songIds").EnumerateArray().Select(id => id.GetInt64());

        // Sorted, so the same set of songs is the same request whatever order the add produced them in.
        var list = named.ToList();

        return list.SequenceEqual(list.Order()) && list.SequenceEqual(songIds.Order());
    }

    [Fact]
    public async Task An_album_add_pins_the_identities_that_carry_the_release_and_leaves_the_rest_to_the_policy()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"), (8, "m-b"));

        var (commands, _) = Queue();
        var added = await NewService(context, commands).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories")),
                Identity("m-b", "Lose Yourself to Dance", Single("s-b", "Lose Yourself to Dance")),
            ],
            new SongAddOptions { AlbumReleaseId = "r1" },
            CancellationToken.None);

        added.Should().HaveCount(2);
        added.Should().OnlyContain(result => result.Outcome == SongAddOutcome.Added);

        // The identity that carries the chosen release is planned against it alone, with the
        // one-track minimum an explicit choice implies, and its context is saved pinned.
        var pinned = await FindSongAsync(context, "m-a");
        pinned.AlbumContext!.AlbumKey.Should().Be("r1");
        pinned.AlbumContext!.MbReleaseId.Should().Be("r1");
        pinned.AlbumContext!.Pinned.Should().BeTrue();
        pinned.AlbumContext!.TrackNo.Should().Be(3);

        // The identity without that release option is planned by the library's policy as usual.
        var byPolicy = await FindSongAsync(context, "m-b");
        byPolicy.AlbumContext!.Pinned.Should().BeFalse();

        // The batch still queues one missing search, not one search per song, and it names its songs.
        await commands.Received(1).EnqueueAsync(
            "MissingSearch",
            Arg.Is<string?>(body => NamesSongs(body, added.Select(result => result!.Song.Id))),
            CommandTrigger.Unspecified,
            Arg.Any<CancellationToken>());
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
    public async Task A_song_with_a_move_in_flight_cannot_be_deleted()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var added = await service.AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"))],
            new SongAddOptions(),
            CancellationToken.None);
        var songId = added[0].Song.Id;
        await AddMoveAsync(context, songId, CompactMoveState.Staged, stagedPath: "/music/.wondarr-compact/1/a.flac");

        var delete = async () => await service.DeleteAsync(songId, CancellationToken.None);

        (await delete.Should().ThrowAsync<SongsCompactingException>())
            .Which.Message.Should().Be("Compaction is moving this song; try again when it finishes");
        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync()).Should().Be(1);

        // Once the move is finished, the song can go.
        await context.CompactMoves.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.State, CompactMoveState.Placed)
            .SetProperty(row => row.StagedPath, (string?)null));

        (await service.DeleteAsync(songId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Choosing_an_album_while_the_song_is_being_moved_is_refused_and_changes_nothing()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var service = NewService(context);
        var identity = Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"), Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);
        var songId = added[0].Song.Id;
        await AddMoveAsync(context, songId, CompactMoveState.Planned);
        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var choose = async () => await service.SetAlbumContextAsync(songId, "r1", CancellationToken.None);

        (await choose.Should().ThrowAsync<SongsCompactingException>())
            .Which.Message.Should().Be("Compaction is moving this song; try again when it finishes");
        context.ChangeTracker.Clear();

        var song = await FindSongAsync(context, "m-a");
        song.AlbumContext!.AlbumKey.Should().NotBe("r1");
        song.AlbumContext.Pinned.Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_an_album_queues_a_refile_for_a_managed_file()
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var (commands, _) = Queue(searchOnAdd: false);
        commands
            .EnqueueAsync("RefileSong", Arg.Any<string?>(), CommandTrigger.Manual, Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CommandRecord { Id = 42, Name = "RefileSong", Body = call.ArgAt<string?>(1) }));

        var service = NewService(context, commands);
        var identity = Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"), Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);
        var songId = added[0].Song.Id;
        await AddFileAsync(context, songId, SourceTypes.Soulseek);
        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var changed = await service.SetAlbumContextAsync(songId, "r1", CancellationToken.None);

        changed!.RefileCommandId.Should().Be(42);
        changed.Song.AlbumContext!.AlbumKey.Should().Be("r1");

        var body = (string)commands.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ICommandQueue.EnqueueAsync)
                && (string)call.GetArguments()[0]! == "RefileSong")
            .GetArguments()[1]!;
        ReadsSongId(body).Should().Be(songId);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData(null)]
    public async Task Choosing_an_album_for_a_reference_file_or_no_file_queues_nothing(string? sourceType)
    {
        await using var context = await ContextAsync();
        StubCoverArt();

        var (commands, _) = Queue(searchOnAdd: false);
        var service = NewService(context, commands);
        var identity = Identity("m-a", "Get Lucky", Single("s-a", "Get Lucky"), Album("r1", "Random Access Memories"));
        var added = await service.AddIdentitiesAsync([identity], new SongAddOptions(), CancellationToken.None);
        var songId = added[0].Song.Id;

        if (sourceType is not null)
        {
            await AddFileAsync(context, songId, sourceType);
        }

        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>()).Returns(identity);

        var changed = await service.SetAlbumContextAsync(songId, "r1", CancellationToken.None);

        changed!.RefileCommandId.Should().BeNull();
        changed.Song.AlbumContext!.AlbumKey.Should().Be("r1", "only the database changes");
        await commands.DidNotReceive()
            .EnqueueAsync("RefileSong", Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    private static async Task AddMoveAsync(
        WondarrDbContext context,
        long songId,
        CompactMoveState state,
        string? stagedPath = null)
    {
        context.CompactMoves.Add(new CompactMoveRecord
        {
            LibraryId = SeedData.DefaultLibraryId,
            SongId = songId,
            FromPath = "/music/a.flac",
            StagedPath = stagedPath,
            State = state,
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task AddFileAsync(WondarrDbContext context, long songId, string sourceType)
    {
        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = "/music/Daft Punk/Get Lucky/01 - Get Lucky.flac",
            Size = 30_000_000,
            Codec = "flac",
            Container = "flac",
            BitrateKbps = 1000,
            SampleRate = 44_100,
            BitDepth = 16,
            Channels = 2,
            DurationMs = 369_000,
            QualityId = 36,
            SourceType = sourceType,
            ImportedAt = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
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
    public async Task Adding_a_monitored_song_queues_a_song_search_for_it()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"));

        var (commands, _) = Queue();
        var added = await NewService(context, commands).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"))],
            new SongAddOptions(),
            CancellationToken.None);

        added[0].Outcome.Should().Be(SongAddOutcome.Added);

        await commands.Received(1).EnqueueAsync(
            "SongSearch",
            Arg.Is<string?>(body => ReadsSongId(body) == added[0].Song.Id),
            CommandTrigger.Unspecified,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_several_songs_queues_one_missing_search_not_one_search_each()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"), (4, "m-b"));

        var (commands, _) = Queue();
        var added = await NewService(context, commands).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories")),
                Identity("m-b", "Lose Yourself to Dance", Album("r1", "Random Access Memories")),
            ],
            new SongAddOptions(),
            CancellationToken.None);

        added.Should().OnlyContain(result => result.Outcome == SongAddOutcome.Added);

        await commands.Received(1).EnqueueAsync(
            "MissingSearch",
            Arg.Is<string?>(body => NamesSongs(body, added.Select(result => result!.Song.Id))),
            CommandTrigger.Unspecified,
            Arg.Any<CancellationToken>());
        await commands.DidNotReceive().EnqueueAsync(
            "SongSearch",
            Arg.Any<string?>(),
            Arg.Any<CommandTrigger>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_an_unmonitored_song_queues_nothing()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"));

        var (commands, _) = Queue();
        await NewService(context, commands).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"))],
            new SongAddOptions { Monitored = false },
            CancellationToken.None);

        await commands.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CommandTrigger>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_on_add_can_be_turned_off()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"));

        var (commands, options) = Queue(searchOnAdd: false);
        await NewService(context, commands, options).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"))],
            new SongAddOptions(),
            CancellationToken.None);

        await commands.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CommandTrigger>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_enqueue_does_not_fail_the_add()
    {
        await using var context = await ContextAsync();
        StubCoverArt();
        StubRelease("r1", (3, "m-a"));

        var (commands, _) = Queue();
        commands.EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandRecord>>(_ => throw new InvalidOperationException("the queue is down"));

        var added = await NewService(context, commands).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", Album("r1", "Random Access Memories"))],
            new SongAddOptions(),
            CancellationToken.None);

        added[0].Outcome.Should().Be(SongAddOutcome.Added, "the song is saved; only the immediate search is lost");
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    /// <summary>A substituted queue and options monitor, wired the way the service reads them.</summary>
    private static (ICommandQueue Commands, IOptionsMonitor<SearchOptions> Options) Queue(bool searchOnAdd = true)
    {
        var commands = Substitute.For<ICommandQueue>();
        var options = Substitute.For<IOptionsMonitor<SearchOptions>>();
        options.CurrentValue.Returns(new SearchOptions { SearchOnAdd = searchOnAdd });

        return (commands, options);
    }

    /// <summary>Reads the song id out of a queued <c>SongSearch</c> body.</summary>
    private static long? ReadsSongId(string? body)
    {
        using var document = JsonDocument.Parse(body ?? "{}");

        return document.RootElement.TryGetProperty("songId", out var songId) && songId.TryGetInt64(out var value)
            ? value
            : null;
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

    private SongService NewService(
        WondarrDbContext context,
        ICommandQueue? commands = null,
        IOptionsMonitor<SearchOptions>? options = null)
    {
        var counter = 0;

        return new SongService(
            context,
            _resolver,
            new AlbumPolicyEngine(() => new Guid(++counter, 0, 0, new byte[8])),
            _musicBrainz,
            _coverArt,
            commands ?? SearchOnAddOff.Commands,
            options ?? Queue().Options,
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

    /// <summary>An identity with a Deezer id, and a MusicBrainz id when the recording has since been found there.</summary>
    private static SongIdentity DeezerIdentity(long deezerId, string title, string? mbRecordingId = null) => new()
    {
        Source = mbRecordingId is null ? "deezer" : "musicbrainz",
        MbRecordingId = mbRecordingId,
        DeezerId = deezerId,
        Title = title,
        ArtistCredit = "Daft Punk",
        Artists = [new IdentityArtist("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0)],
        OriginalDate = "2013",
        ReleaseOptions = [Single("s-" + deezerId, title)],
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
