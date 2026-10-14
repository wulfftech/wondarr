using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// Two adds of different new songs by the same new artist at once: both look the artist up before either
/// writes, so both add it, and the second save breaks the artist table's unique index on the MusicBrainz
/// id. The seam is the cover archive, which the add asks after it read the artists and before it writes:
/// a second context commits the artist (and a song by it) there, as the request that won the race would.
/// </summary>
public class SongServiceArtistRaceTests : IDisposable
{
    private const string CoverUrl = "https://cover.example/album.jpg";

    private static readonly IdentityArtist DaftPunk = new("Daft Punk", "Daft Punk", "a1", null, ArtistRole.Main, 0);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly ICoverArtResolver _coverArt = Substitute.For<ICoverArtResolver>();

    /// <summary>Deletes this test's temp database.</summary>
    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Which failures count ------------------------------------------------------------------

    [Fact]
    public async Task Only_the_artists_musicbrainz_id_is_unique_so_only_it_can_be_a_duplicate_artist_key()
    {
        await using var context = await ContextAsync();

        // The Deezer id and the name are not unique: two artists may share them.
        context.Artists.Add(new Artist { Name = "Twin", SortName = "Twin", DeezerId = 7 });
        context.Artists.Add(new Artist { Name = "Twin", SortName = "Twin", DeezerId = 7 });
        context.Artists.Add(new Artist { Name = "Daft Punk", SortName = "Daft Punk", MbArtistId = "a1" });
        await context.SaveChangesAsync();

        await using var other = _database.CreateContext(_timeProvider);
        other.Artists.Add(new Artist { Name = "Daft Punk again", SortName = "Daft Punk again", MbArtistId = "a1" });

        var duplicate = (await FluentActions.Awaiting(() => other.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).Which;

        SongService.IsDuplicateArtistKey(duplicate).Should().BeTrue();
        SongService.IsDuplicateSongKey(duplicate).Should().BeFalse();
        duplicate.GetBaseException().Message.Should().Contain("artist.mb_artist_id");
    }

    [Fact]
    public async Task A_duplicate_song_key_and_a_refusing_trigger_are_not_duplicate_artist_keys()
    {
        await using var context = await ContextAsync();

        var first = await SongSeed.AddAsync(context, "First");
        var second = await SongSeed.AddAsync(context, "Second");

        await context.Songs.Where(song => song.Id == first.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(song => song.MbRecordingId, "m-a"));

        await using var other = _database.CreateContext(_timeProvider);
        (await other.Songs.SingleAsync(song => song.Id == second.Id)).MbRecordingId = "m-a";

        var songKey = (await FluentActions.Awaiting(() => other.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).Which;

        SongService.IsDuplicateSongKey(songKey).Should().BeTrue();
        SongService.IsDuplicateArtistKey(songKey).Should().BeFalse();

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_artist BEFORE INSERT ON artist "
            + "BEGIN SELECT RAISE(ABORT, 'artist refused'); END");
        context.Artists.Add(new Artist { Name = "New", SortName = "New", MbArtistId = "a2" });

        var refused = (await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).Which;

        SongService.IsDuplicateArtistKey(refused).Should().BeFalse();
        SongService.IsDuplicateSongKey(refused).Should().BeFalse();
    }

    // --- The paths -----------------------------------------------------------------------------

    [Fact]
    public async Task A_single_add_reuses_the_new_artist_a_racing_add_created_first()
    {
        await using var context = await ContextAsync();
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));
        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky"));

        // The song controller's options: one failing identity is reported, not thrown.
        var result = await NewService(context).AddAsync(
            "m-a",
            null,
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        artistId.Should().BeGreaterThan(0);
        result.Outcome.Should().Be(SongAddOutcome.Added);
        result.Song.Id.Should().BeGreaterThan(0);
        result.Song.PrimaryArtistId.Should().Be(artistId);
        context.Database.CurrentTransaction.Should().BeNull();

        await AssertOneSharedArtistAsync(artistId, songs: 2, artists: 1);
    }

    [Fact]
    public async Task A_bulk_add_reuses_the_racing_artist_and_still_creates_its_other_new_artists()
    {
        await using var context = await ContextAsync();
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));

        var results = await NewService(context).AddIdentitiesAsync(
            [
                Identity("m-a", "Get Lucky"),
                Identity("m-b", "Lose Yourself to Dance"),
                Identity(
                    "m-c",
                    "Fragments of Time",
                    DaftPunk,
                    new IdentityArtist("Todd Edwards", "Edwards, Todd", "g1", null, ArtistRole.Featured, 1)),
            ],
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results.Select(result => result.Outcome).Should().OnlyContain(outcome => outcome == SongAddOutcome.Added);
        results.Should().OnlyContain(result => result.Song.Id > 0);
        context.Database.CurrentTransaction.Should().BeNull();

        // Daft Punk is the winner's row; only the guest is a row of this add's own.
        await AssertOneSharedArtistAsync(artistId, songs: 4, artists: 2);
    }

    [Fact]
    public async Task A_list_add_reuses_the_new_artist_a_racing_add_created_first()
    {
        await using var context = await ContextAsync();
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));
        _resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = call.Arg<string>().Contains("Get Lucky", StringComparison.Ordinal)
                    ? Identity("m-a", "Get Lucky")
                    : Identity("m-b", "Lose Yourself to Dance"),
            }));

        var lists = new PasteListService(context, _resolver, NewService(context), _timeProvider, NullLogger<PasteListService>.Instance);
        var list = await lists.CreateAsync(
            "Daft Punk - Get Lucky\nDaft Punk - Lose Yourself to Dance",
            null,
            null,
            CancellationToken.None);

        var summary = await lists.ProcessAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        summary.Should().Be("2 lines: 2 added, 0 already in the library, 0 unresolved, 0 skipped");

        var items = await context.ImportListItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync();

        items.Should().OnlyContain(item => item.State == ImportListItemState.Added && item.SongId > 0);
        context.Database.CurrentTransaction.Should().BeNull();

        await AssertOneSharedArtistAsync(artistId, songs: 3, artists: 1);
    }

    [Fact]
    public async Task An_add_that_keeps_its_transaction_open_still_reuses_the_racing_artist_and_leaves_the_transaction_to_the_caller()
    {
        await using var context = await ContextAsync();
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));
        _resolver.GetIdentityAsync("m-a", null, Arg.Any<CancellationToken>())
            .Returns(Identity("m-a", "Get Lucky"));

        // A reference scan's options: the songs wait in a transaction until their files are recorded.
        var result = await NewService(context).AddAsync(
            "m-a",
            null,
            new SongAddOptions { IsolateFailures = true, KeepTransactionOpen = true, SearchOnAdd = false },
            CancellationToken.None);

        result.Outcome.Should().Be(SongAddOutcome.Added);
        context.Database.CurrentTransaction.Should().NotBeNull("the caller commits what the add left open");

        await context.Database.CurrentTransaction!.CommitAsync();

        context.Database.CurrentTransaction.Should().BeNull();
        await AssertOneSharedArtistAsync(artistId, songs: 2, artists: 1);
    }

    // --- The retry is one retry ------------------------------------------------------------------

    [Fact]
    public async Task A_retry_that_meets_another_racing_artist_fails_the_song_and_is_not_tried_again()
    {
        // Saves of this context: 1 the batch, 2 the song on its own, 3 the retry. The racing add that
        // creates the guest lands just before the third, so the retry breaks the same index again.
        var hook = new BeforeSaveHook(async save =>
        {
            if (save == 3)
            {
                await CommitArtistAsync("g1", "Todd Edwards");
            }
        });

        await using var context = await ContextAsync(hook);
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));

        var results = await NewService(context).AddIdentitiesAsync(
            [
                Identity(
                    "m-c",
                    "Fragments of Time",
                    DaftPunk,
                    new IdentityArtist("Todd Edwards", "Edwards, Todd", "g1", null, ArtistRole.Featured, 1)),
            ],
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results[0].Outcome.Should().Be(SongAddOutcome.Failed);
        results[0].Error.Should().Contain("artist.mb_artist_id");
        hook.Saves.Should().Be(3, "a failed retry is final: no third try of the song");
        context.Database.CurrentTransaction.Should().BeNull();

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Artists.AsNoTracking().OrderBy(artist => artist.MbArtistId).Select(artist => artist.MbArtistId).ToListAsync())
            .Should().Equal("a1", "g1");
        (await reader.Artists.AsNoTracking().SingleAsync(artist => artist.MbArtistId == "a1")).Id.Should().Be(artistId);
        (await reader.Songs.CountAsync()).Should().Be(1, "only the winner's song exists");
        (await reader.SongArtists.CountAsync()).Should().Be(1);
        (await reader.AlbumContexts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_retry_that_fails_for_another_reason_fails_only_that_song()
    {
        var hook = new BeforeSaveHook(_ => Task.CompletedTask);

        await using var context = await ContextAsync(hook);
        var artistId = 0L;

        StubCoverArtThen(async () => artistId = await CommitWinnerAsync("Instant Crush"));

        // The artist index refuses the first two tries; the retry reaches the song, which the trigger refuses.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_poison BEFORE INSERT ON song WHEN NEW.title = 'Poison' "
            + "BEGIN SELECT RAISE(ABORT, 'poison refused'); END");

        var results = await NewService(context).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky"), Identity("m-b", "Poison"), Identity("m-c", "Instant Crush Again")],
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results.Select(result => result.Outcome)
            .Should().Equal(SongAddOutcome.Added, SongAddOutcome.Failed, SongAddOutcome.Added);
        results[1].Error.Should().Contain("poison refused");

        // 1 the batch, then the first song: 2 on its own and 3 its retry; the poisoned song, 4, and the
        // last, 5, find the artist among those known; none is tried twice.
        hook.Saves.Should().Be(5);

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Artists.CountAsync()).Should().Be(1);
        (await reader.Songs.AsNoTracking().OrderBy(song => song.Id).Select(song => song.Title).ToListAsync())
            .Should().Equal("Instant Crush", "Get Lucky", "Instant Crush Again");
        (await reader.Songs.CountAsync(song => song.PrimaryArtistId == artistId)).Should().Be(3);
        (await reader.AlbumContexts.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task An_artist_save_that_fails_for_another_reason_is_not_retried()
    {
        var hook = new BeforeSaveHook(_ => Task.CompletedTask);

        await using var context = await ContextAsync(hook);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_artist BEFORE INSERT ON artist WHEN NEW.name = 'Poison Artist' "
            + "BEGIN SELECT RAISE(ABORT, 'artist refused'); END");
        StubCoverArt();

        var results = await NewService(context).AddIdentitiesAsync(
            [Identity("m-a", "Get Lucky", new IdentityArtist("Poison Artist", "Poison Artist", "p1", null, ArtistRole.Main, 0))],
            new SongAddOptions { IsolateFailures = true },
            CancellationToken.None);

        results[0].Outcome.Should().Be(SongAddOutcome.Failed);
        results[0].Error.Should().Contain("artist refused");
        hook.Saves.Should().Be(2, "the batch and the song on its own; a refusal that is not a duplicate is not retried");

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Artists.CountAsync()).Should().Be(0);
        (await reader.Songs.CountAsync()).Should().Be(0);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Commits the artist Daft Punk (MusicBrainz id <c>a1</c>) and a song by it through a second context,
    /// as a request that won the race would, and returns the artist's id.
    /// </summary>
    private async Task<long> CommitWinnerAsync(string songTitle)
    {
        var artistId = await CommitArtistAsync("a1", "Daft Punk");

        await using var other = _database.CreateContext(_timeProvider);
        await SongSeed.AddAsync(other, songTitle);

        return artistId;
    }

    /// <summary>Commits one artist through a second context and returns its id.</summary>
    private async Task<long> CommitArtistAsync(string mbArtistId, string name)
    {
        await using var other = _database.CreateContext(_timeProvider);
        var artist = new Artist { Name = name, SortName = name, MbArtistId = mbArtistId };

        other.Artists.Add(artist);
        await other.SaveChangesAsync();

        return artist.Id;
    }

    /// <summary>
    /// Reads the database through a context of its own and checks what a lost race must leave: the
    /// winner's artist row is the only <c>a1</c>, every song credits it, and no song is missing a credit
    /// or an album context.
    /// </summary>
    private async Task AssertOneSharedArtistAsync(long artistId, int songs, int artists)
    {
        artistId.Should().BeGreaterThan(0);

        await using var reader = _database.CreateContext(_timeProvider);

        (await reader.Artists.CountAsync()).Should().Be(artists);
        (await reader.Artists.AsNoTracking().SingleAsync(artist => artist.MbArtistId == "a1")).Id
            .Should().Be(artistId, "the winner's row is the artist: the loser's copy was never written");

        (await reader.Songs.CountAsync()).Should().Be(songs);
        (await reader.Songs.CountAsync(song => song.PrimaryArtistId == artistId)).Should().Be(songs);
        (await reader.SongArtists.CountAsync(credit => credit.ArtistId == artistId)).Should().Be(songs);
        (await reader.AlbumContexts.CountAsync()).Should().Be(songs);
        (await reader.SongArtists.CountAsync(credit => !reader.Songs.Any(song => song.Id == credit.SongId))).Should().Be(0);
    }

    private async Task<WondarrDbContext> ContextAsync(params IInterceptor[] interceptors)
    {
        await _database.MigrateAsync(_timeProvider);

        if (interceptors.Length == 0)
        {
            return _database.CreateContext(_timeProvider);
        }

        return new WondarrDbContext(
            new DbContextOptionsBuilder<WondarrDbContext>()
                .UseSqlite($"Data Source={_database.FilePath}")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptors)
                .Options,
            _timeProvider);
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
            SearchOnAddOff.Commands,
            SearchOnAddOff.Options,
            NullLogger<SongService>.Instance);
    }

    private void StubCoverArt() =>
        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CoverArt(CoverUrl, "coverartarchive"));

    /// <summary>
    /// Stubs the cover archive and, on its first call (which comes after the add read the artists and
    /// before it writes), runs <paramref name="beforeWrite"/>: the seam a racing request slips through.
    /// </summary>
    private void StubCoverArtThen(Func<Task> beforeWrite)
    {
        var fired = false;

        _coverArt.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (!fired)
                {
                    fired = true;
                    await beforeWrite();
                }

                return (CoverArt?)new CoverArt(CoverUrl, "coverartarchive");
            });
    }

    /// <summary>A single by Daft Punk, or by the artists given (the first is the main one).</summary>
    private static SongIdentity Identity(string mbRecordingId, string title, params IdentityArtist[] artists) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = artists.Length == 0 ? "Daft Punk" : string.Join(" feat. ", artists.Select(artist => artist.Name)),
        Artists = artists.Length == 0 ? [DaftPunk] : artists,
        OriginalDate = "2013",
        ReleaseOptions =
        [
            new ReleaseOption
            {
                Key = "s-" + mbRecordingId,
                MbReleaseId = "s-" + mbRecordingId,
                MbReleaseGroupId = "s-" + mbRecordingId + "-group",
                Title = title,
                AlbumArtist = "Daft Punk",
                PrimaryType = "Single",
                Status = "Official",
                Date = "2013-04-19",
            },
        ],
    };

    /// <summary>Runs <c>beforeSave</c> with the number of the save (1, 2, ...) before each save of its context.</summary>
    private sealed class BeforeSaveHook(Func<int, Task> beforeSave) : SaveChangesInterceptor
    {
        /// <summary>Gets how many saves the context started.</summary>
        public int Saves { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            await beforeSave(Saves).ConfigureAwait(false);

            return result;
        }
    }
}
