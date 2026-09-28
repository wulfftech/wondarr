using Compilarr.Core.Domain;
using Compilarr.Core.Identity;
using Compilarr.Core.ImportLists;
using Compilarr.Core.Metadata;
using Compilarr.Core.Paging;
using Compilarr.Core.Persistence;
using Compilarr.Core.Songs;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Compilarr.Core.Tests.ImportLists;

/// <summary>
/// The pasted-list pipeline against a real migrated SQLite database: what a pasted block becomes,
/// what one processing run does with the lines, and the two things a user can do with a line that
/// did not resolve. The resolver and the song service are fakes, so nothing reaches the network.
/// </summary>
public class PasteListServiceTests : IDisposable
{
    /// <summary>A fixed clock, so the list's name and its last-synced instant are predictable.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 31, 0, TimeSpan.Zero);

    /// <summary>The line the fake resolver answers with a MusicBrainz identity.</summary>
    private const string ResolvableLine = "Queen - Bohemian Rhapsody";

    /// <summary>The line the fake resolver answers with a Deezer-only identity.</summary>
    private const string DeezerLine = "Nobody - Nothing";

    /// <summary>The line the fake resolver knows nothing about.</summary>
    private const string UnresolvedLine = "Mystery - Song";

    /// <summary>The line the fake resolver cannot look up at all.</summary>
    private const string UnsupportedLine = "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT";

    /// <summary>The line whose lookup throws.</summary>
    private const string FailingLine = "Broken - Line";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ISongService _songs = Substitute.For<ISongService>();

    [Fact]
    public async Task CreateAsync_drops_blanks_and_comments_skips_a_duplicate_and_keeps_line_numbers()
    {
        await using var context = await ContextAsync();

        var list = await NewService(context).CreateAsync(
            $"""
            {ResolvableLine}
            {DeezerLine}
            QUEEN - bohemian rhapsody

            # a comment
            {UnsupportedLine}
            """,
            qualityProfileId: null,
            libraryId: null,
            CancellationToken.None);

        list.Type.Should().Be("paste");
        list.Name.Should().Be("Pasted list 2026-09-28 10:31 UTC");
        list.Policy.Should().Be("AddOnly");
        list.Settings.Should().Be("{}");
        list.QualityProfileId.Should().Be(SeedData.StandardProfileId);
        list.LibraryId.Should().Be(SeedData.DefaultLibraryId);

        var items = await context.ImportListItems.OrderBy(item => item.Id).ToListAsync();

        // Six pasted lines, of which the blank one and the comment are not lines of the list at all:
        // the Queen line, the Nobody line, its case-only duplicate and the Spotify link remain.
        items.Should().HaveCount(4);
        items.Select(item => item.ExternalId).Should().Equal("1", "2", "3", "4");
        items.Select(item => item.State).Should().Equal(
            ImportListItemState.Pending,
            ImportListItemState.Pending,
            ImportListItemState.Skipped,
            ImportListItemState.Pending);

        items[0].Reason.Should().BeNull();
        items[2].Reason.Should().Be("Duplicate of line 1");

        var first = ImportListItemJson.ReadLine(items[0]);
        first.Line.Should().Be(ResolvableLine);
        first.Artist.Should().Be("Queen");
        first.Title.Should().Be("Bohemian Rhapsody");

        // A link is not "Artist - Title", so there is nothing to parse out of it.
        var link = ImportListItemJson.ReadLine(items[3]);
        link.Line.Should().Be(UnsupportedLine);
        link.Artist.Should().BeNull();
        link.Title.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_rejects_a_list_of_more_than_a_thousand_lines()
    {
        await using var context = await ContextAsync();
        var text = string.Join('\n', Enumerable.Range(1, ImportListLimits.MaxLines + 1).Select(index => $"Artist {index} - Title {index}"));

        var act = async () => await NewService(context).CreateAsync(text, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();

        (await context.ImportLists.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_adds_both_resolved_lines_in_one_batch_and_reviews_the_rest()
    {
        await using var context = await ContextAsync();
        StubResolver();

        // A line's song id is a foreign key, so the batch's songs are real rows.
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111");
        var nobody = await SeedSongAsync(context, "Nothing", "44444444-4444-4444-4444-444444444444");

        IReadOnlyList<SongIdentity>? batched = null;
        SongAddOptions? options = null;

        _songs
            .AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                batched = call.Arg<IReadOnlyList<SongIdentity>>();
                options = call.Arg<SongAddOptions>();

                return Task.FromResult<IReadOnlyList<SongAddResult>>(
                [
                    new SongAddResult(batched[0], SongAddOutcome.Added, queen),
                    new SongAddResult(batched[1], SongAddOutcome.AlreadyExists, nobody),
                ]);
            });

        var service = NewService(context);
        var list = await service.CreateAsync(
            $"{ResolvableLine}\n{DeezerLine}\n{UnresolvedLine}\n{UnsupportedLine}\n{FailingLine}",
            null,
            null,
            CancellationToken.None);

        var progress = new List<string>();
        var summary = await service.ProcessAsync(list.Id, message =>
        {
            progress.Add(message);

            return Task.CompletedTask;
        }, CancellationToken.None);

        summary.Should().Be("5 lines: 1 added, 1 already in the library, 2 unresolved, 1 skipped");

        var items = await context.ImportListItems.OrderBy(item => item.Id).ToListAsync();

        items.Select(item => item.State).Should().Equal(
            ImportListItemState.Added,
            ImportListItemState.Added,
            ImportListItemState.Unresolved,
            ImportListItemState.Skipped,
            ImportListItemState.Unresolved);

        items[0].SongId.Should().Be(queen.Id);
        items[0].Reason.Should().BeNull();
        items[1].SongId.Should().Be(nobody.Id);
        items[1].Reason.Should().Be(PasteListService.AlreadyInLibraryReason);

        // The unsupported line keeps the resolver's own reason; the failing one names the failure.
        items[3].Reason.Should().Be("Spotify links are imported through CSV exports (Phase 6)");
        items[4].Reason.Should().Be("Lookup failed: MusicBrainz is unavailable");

        var candidates = ImportListItemJson.ReadCandidates(items[2]);
        candidates.Should().HaveCount(2);
        candidates[0].MbRecordingId.Should().Be("11111111-1111-1111-1111-111111111111");
        candidates[0].Title.Should().Be("Mystery Song");
        candidates[0].Score.Should().BeApproximately(88.5, 0.001);
        candidates[1].DeezerId.Should().Be(700);

        // One batch, in line order, so the album policy can group the artist's songs.
        batched.Should().NotBeNull();
        batched!.Should().HaveCount(2);
        batched[0].MbRecordingId.Should().Be("11111111-1111-1111-1111-111111111111");
        batched[1].DeezerId.Should().Be(700);
        options!.AddedBy.Should().Be($"list:{list.Id}");
        options.QualityProfileId.Should().Be(SeedData.StandardProfileId);
        options.LibraryId.Should().Be(SeedData.DefaultLibraryId);

        progress.Should().Equal(
            "Resolved 1 of 5 lines",
            "Resolved 2 of 5 lines",
            "Resolved 3 of 5 lines",
            "Resolved 4 of 5 lines",
            "Resolved 5 of 5 lines");

        list.LastSyncedAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Resolving_an_unresolved_line_adds_the_song_and_skipping_one_ends_it()
    {
        await using var context = await ContextAsync();
        StubResolver();

        var service = NewService(context);
        var list = await service.CreateAsync($"{UnresolvedLine}\n{Mystery2Line()}", null, null, CancellationToken.None);
        await service.ProcessAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        var items = await context.ImportListItems.OrderBy(item => item.Id).ToListAsync();
        items.Should().OnlyContain(item => item.State == ImportListItemState.Unresolved);

        // The line's song id is a foreign key, so the resolved song is a real row: the item points at it.
        var stored = await SeedSongAsync(context);

        _songs
            .AddAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SongAddResult(
                Identity("22222222-2222-2222-2222-222222222222", "Mystery Song"),
                SongAddOutcome.Added,
                stored)));

        var resolved = await service.ResolveItemAsync(items[0].Id, "22222222-2222-2222-2222-222222222222", null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.State.Should().Be(ImportListItemState.Added);
        resolved.SongId.Should().Be(stored.Id);
        resolved.Reason.Should().BeNull();

        await _songs.Received(1).AddAsync(
            "22222222-2222-2222-2222-222222222222",
            null,
            Arg.Is<SongAddOptions>(addOptions => addOptions.AddedBy == $"list:{list.Id}"),
            Arg.Any<CancellationToken>());

        var skipped = await service.SkipItemAsync(items[1].Id, CancellationToken.None);

        skipped.Should().NotBeNull();
        skipped!.State.Should().Be(ImportListItemState.Skipped);
        skipped.Reason.Should().Be(PasteListService.SkippedByUserReason);

        // The summary counts the resolved line as added and the skipped one as skipped.
        var summary = await service.GetSummaryAsync(list.Id, CancellationToken.None);

        summary.Should().NotBeNull();
        summary!.Added.Should().Be(1);
        summary.Skipped.Should().Be(1);
        summary.Unresolved.Should().Be(0);
    }

    [Fact]
    public async Task GetItemsAsync_filters_by_list_and_state_and_reads_back_in_line_order()
    {
        await using var context = await ContextAsync();
        StubResolver();

        var service = NewService(context);
        var first = await service.CreateAsync($"{UnresolvedLine}\n{Mystery2Line()}", null, null, CancellationToken.None);
        await service.CreateAsync($"{ResolvableLine}", null, null, CancellationToken.None);
        await service.ProcessAsync(first.Id, _ => Task.CompletedTask, CancellationToken.None);

        var unresolved = await service.GetItemsAsync(
            new PagingSpec(1, 20, "line", descending: true),
            first.Id,
            ImportListItemState.Unresolved,
            CancellationToken.None);

        unresolved.TotalRecords.Should().Be(2);

        // An explicit "line" key honours the direction; the default key is always line order.
        unresolved.Records.Select(item => item.ExternalId).Should().Equal("2", "1");

        var byDefault = await service.GetItemsAsync(
            new PagingSpec(1, 20, null, descending: true),
            first.Id,
            null,
            CancellationToken.None);

        byDefault.Records.Select(item => item.ExternalId).Should().Equal("1", "2");
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The second line the fake resolver knows nothing about.</summary>
    private static string Mystery2Line() => "Another - Mystery";

    /// <summary>Answers every line the way the acceptance criteria name.</summary>
    private void StubResolver()
    {
        _resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var line = call.Arg<string>();

                return Task.FromResult(line switch
                {
                    ResolvableLine => new ResolveResult
                    {
                        Status = ResolveStatus.Resolved,
                        Identity = Identity("11111111-1111-1111-1111-111111111111", "Bohemian Rhapsody"),
                    },
                    DeezerLine => new ResolveResult
                    {
                        Status = ResolveStatus.ResolvedDeezerOnly,
                        Identity = Identity("", "Nothing"),
                    },
                    UnsupportedLine => new ResolveResult
                    {
                        Status = ResolveStatus.Unsupported,
                        Reason = "Spotify links are imported through CSV exports (Phase 6)",
                    },
                    FailingLine => throw new MetadataProviderException("musicbrainz", System.Net.HttpStatusCode.ServiceUnavailable, "MusicBrainz is unavailable"),
                    _ => new ResolveResult
                    {
                        Status = ResolveStatus.Unresolved,
                        Reason = $"No match on MusicBrainz or Deezer for '{line}'",
                        Candidates =
                        [
                            new SongCandidate
                            {
                                Source = "musicbrainz",
                                MbRecordingId = "11111111-1111-1111-1111-111111111111",
                                Title = "Mystery Song",
                                ArtistCredit = "Mystery",
                                DurationMs = 200_000,
                                Score = 88.5,
                            },
                            new SongCandidate
                            {
                                Source = "deezer",
                                DeezerId = 700,
                                Title = "Mystery Song",
                                ArtistCredit = "Mystery",
                                Score = 71.0,
                            },
                        ],
                    },
                });
            });

        // A run with nothing to add never calls the song service; this keeps that path explicit.
        _songs
            .AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SongAddResult>>([]));
    }

    /// <summary>One identity, with a Deezer-only one carrying an id and no MBID.</summary>
    private static SongIdentity Identity(string mbRecordingId, string title) => new()
    {
        Source = mbRecordingId.Length > 0 ? "musicbrainz" : "deezer",
        MbRecordingId = mbRecordingId.Length > 0 ? mbRecordingId : null,
        DeezerId = mbRecordingId.Length > 0 ? null : 700,
        Title = title,
        ArtistCredit = title,
        Artists = [new IdentityArtist(title, title, null, null, ArtistRole.Main, 0)],
    };

    /// <summary>One stored song, so a resolved line's song id is a real row.</summary>
    private static async Task<Song> SeedSongAsync(
        CompilarrDbContext context,
        string title = "Mystery Song",
        string mbRecordingId = "22222222-2222-2222-2222-222222222222")
    {
        var artist = new Artist { Name = title, SortName = title };
        context.Artists.Add(artist);

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtist = artist,
            MbRecordingId = mbRecordingId,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song;
    }

    private async Task<CompilarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private PasteListService NewService(CompilarrDbContext context) =>
        new(context, _resolver, _songs, _timeProvider, NullLogger<PasteListService>.Instance);
}
