using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.ImportLists;
using Wondarr.Core.ImportLists.Csv;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// Synced import lists: settings validation, the sync's diff against the source (new, still there,
/// gone, re-ordered), resolution by the strongest id first, a failed read, and the schedule.
/// </summary>
public sealed class ImportListServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ISongService _songs = Substitute.For<ISongService>();
    private readonly FakeProvider _provider = new();

    [Fact]
    public async Task Create_validates_the_type_the_policy_and_the_providers_settings()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);

        var unknown = async () => await service.CreateAsync(Draft("nope"), CancellationToken.None);
        (await unknown.Should().ThrowAsync<ImportListValidationException>()).Which.Field.Should().Be("type");

        var policy = async () => await service.CreateAsync(Draft(FakeProvider.FakeType) with { Policy = "Sometimes" }, CancellationToken.None);
        (await policy.Should().ThrowAsync<ImportListValidationException>()).Which.Field.Should().Be("policy");

        var csv = async () => await service.CreateAsync(Draft(ImportList.CsvType), CancellationToken.None);
        (await csv.Should().ThrowAsync<ImportListValidationException>()).Which.Detail.Should().Contain("Upload a CSV file");

        var list = await service.CreateAsync(Draft(FakeProvider.FakeType) with { Policy = "mirror" }, CancellationToken.None);
        list.Policy.Should().Be("Mirror");
        list.LibraryId.Should().Be(SeedData.DefaultLibraryId);
        list.QualityProfileId.Should().Be(SeedData.StandardProfileId);
    }

    [Fact]
    public async Task A_secret_sent_back_masked_keeps_its_stored_value()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(
            Draft(FakeProvider.FakeType) with { Settings = Json("""{"apiKey":"s3cret"}""") },
            CancellationToken.None);

        await service.UpdateAsync(
            list.Id,
            Draft(FakeProvider.FakeType) with { Settings = Json($$"""{"apiKey":"{{NotificationSecrets.Mask}}"}""") },
            CancellationToken.None);

        (await context.ImportLists.AsNoTracking().SingleAsync()).Settings.Should().Contain("s3cret");
    }

    [Fact]
    public async Task Sync_adds_new_items_reorders_known_ones_and_marks_gone_ones()
    {
        await using var context = await ContextAsync();
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111");
        var daft = await SeedSongAsync(context, "Get Lucky", "22222222-2222-2222-2222-222222222222");
        StubIsrcs(queen, daft);
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);

        _provider.Entries =
        [
            new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604"),
            new("b", "Daft Punk", "Get Lucky", Isrc: "USQX91300108"),
        ];

        var first = await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        first.Should().StartWith("Read 2 items (2 new, 0 no longer in the list)");
        var items = await Items(context);
        items.Select(item => (item.ExternalId, item.Position, item.State, item.SongId))
            .Should().Equal(("a", 0, ImportListItemState.Added, queen.Id), ("b", 1, ImportListItemState.Added, daft.Id));

        // The source drops "a", moves "b" to the top and gains "c" (which nothing resolves).
        _provider.Entries =
        [
            new("b", "Daft Punk", "Get Lucky", Isrc: "USQX91300108"),
            new("c", "Mystery", "Song"),
        ];
        _timeProvider.Advance(TimeSpan.FromHours(1));

        var second = await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        second.Should().StartWith("Read 2 items (1 new, 1 no longer in the list)");
        items = await Items(context);
        items.Single(item => item.ExternalId == "a").RemovedAt.Should().Be(Now.UtcDateTime.AddHours(1));
        items.Single(item => item.ExternalId == "b").Position.Should().Be(0);
        items.Single(item => item.ExternalId == "c").Position.Should().Be(1);
        items.Single(item => item.ExternalId == "c").State.Should().Be(ImportListItemState.Unresolved);

        // The batch add was asked for each new item once, never again for a known one.
        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Is<IReadOnlyList<SongIdentity>>(batch => batch.Count == 2),
            Arg.Is<SongAddOptions>(options => options.AddedBy == $"list:{list.Id}"),
            Arg.Any<CancellationToken>());

        // An item that comes back is no longer marked gone.
        _provider.Entries = [new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604")];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);
        (await Items(context)).Single(item => item.ExternalId == "a").RemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_item_is_resolved_by_its_isrc_before_its_text()
    {
        await using var context = await ContextAsync();
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111");
        StubIsrcs(queen, queen);
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        _provider.Entries = [new("a", "Queen", "Bohemian Rhapsody - Remastered 2011", Isrc: "gbum71029604")];

        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        await _resolver.Received(1).ResolveAsync("GBUM71029604", Arg.Any<CancellationToken>());
        await _resolver.DidNotReceive().ResolveAsync("Queen - Bohemian Rhapsody - Remastered 2011", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_text_match_whose_length_is_far_from_the_lists_goes_to_review()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        _resolver
            .ResolveAsync("Queen - Bohemian Rhapsody", Arg.Any<CancellationToken>())
            .Returns(new ResolveResult
            {
                Status = ResolveStatus.Resolved,
                Identity = Identity("11111111-1111-1111-1111-111111111111", "Bohemian Rhapsody", durationMs: 355_000),
            });
        _resolver
            .SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SongCandidate>>([]));

        // The list says four minutes and a half: a live version, perhaps, not the 5:55 album take.
        _provider.Entries = [new("a", "Queen", "Bohemian Rhapsody", DurationMs: 270_000)];

        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        var item = (await Items(context)).Single();
        item.State.Should().Be(ImportListItemState.Unresolved);
        item.Reason.Should().Be("The closest match is 355 s long; the list says 270 s.");
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_records_why_and_fails_the_sync()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        _provider.Error = "The playlist is private.";

        var sync = async () => await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        await sync.Should().ThrowAsync<ImportListSyncException>().WithMessage("The playlist is private.");
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.LastSyncMessage.Should().Be("Failed: The playlist is private.");
        stored.LastSyncedAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Due_lists_are_enabled_synced_lists_whose_interval_has_passed()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var never = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        var recent = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        var old = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        await service.CreateAsync(Draft(FakeProvider.FakeType) with { Enabled = false }, CancellationToken.None);
        await service.CreateAsync(Draft(FakeProvider.FakeType) with { SyncIntervalHours = 0 }, CancellationToken.None);

        recent.LastSyncedAt = Now.UtcDateTime.AddHours(-1);
        old.LastSyncedAt = Now.UtcDateTime.AddHours(-25);
        await context.SaveChangesAsync();

        var due = await service.GetDueAsync(CancellationToken.None);

        due.Should().Equal(never.Id, old.Id);
    }

    [Fact]
    public async Task Deleting_a_list_keeps_the_songs_it_added()
    {
        await using var context = await ContextAsync();
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111");
        StubIsrcs(queen, queen);
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        _provider.Entries = [new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604")];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        (await service.DeleteAsync(list.Id, CancellationToken.None)).Should().BeTrue();

        (await context.ImportListItems.CountAsync()).Should().Be(0);
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private static ImportListDraft Draft(string type) =>
        new(type, "My playlist", Json("{}"), SourceText: null, Policy: null, QualityProfileId: null, LibraryId: null);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static async Task<List<ImportListItem>> Items(WondarrDbContext context) =>
        await context.ImportListItems.AsNoTracking().OrderBy(item => item.Id).ToListAsync();

    private static SongIdentity Identity(string mbRecordingId, string title, int? durationMs = null) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = mbRecordingId,
        Title = title,
        ArtistCredit = title,
        DurationMs = durationMs,
        Artists = [new IdentityArtist(title, title, null, null, ArtistRole.Main, 0)],
    };

    private static async Task<Song> SeedSongAsync(WondarrDbContext context, string title, string mbRecordingId)
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
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song;
    }

    /// <summary>The two ISRCs resolve to the two songs; the batch add answers with those songs.</summary>
    private void StubIsrcs(Song queen, Song daft)
    {
        _resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<string>() switch
            {
                "GBUM71029604" => new ResolveResult { Status = ResolveStatus.Resolved, Identity = Identity(queen.MbRecordingId!, queen.Title) },
                "USQX91300108" => new ResolveResult { Status = ResolveStatus.Resolved, Identity = Identity(daft.MbRecordingId!, daft.Title) },
                _ => new ResolveResult { Status = ResolveStatus.Unresolved, Reason = "No match." },
            }));

        _songs
            .AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<SongAddResult>>(
                [.. call.Arg<IReadOnlyList<SongIdentity>>().Select(identity => new SongAddResult(
                    identity,
                    SongAddOutcome.Added,
                    identity.MbRecordingId == queen.MbRecordingId ? queen : daft))]));
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    private ImportListService NewService(WondarrDbContext context) =>
        new(
            context,
            new PasteListService(context, _resolver, _songs, _timeProvider, NullLogger<PasteListService>.Instance),
            [_provider, new CsvImportListProvider()],
            _timeProvider,
            NullLogger<ImportListService>.Instance);

    /// <summary>A provider whose source the test sets directly.</summary>
    private sealed class FakeProvider : IImportListProvider
    {
        public const string FakeType = "fake";

        public IReadOnlyList<ImportListEntry> Entries { get; set; } = [];

        public string? Error { get; set; }

        public string Type => FakeType;

        public string DisplayName => "Fake";

        public IReadOnlyList<NotificationField> Fields { get; } =
            [new("apiKey", "API key", "password", false, Secret: true)];

        public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) => [];

        public Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken) =>
            Task.FromResult(Error is null ? new ImportListFetchResult(Entries) : ImportListFetchResult.Failed(Error));
    }
}
