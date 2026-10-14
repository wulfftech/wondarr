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
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
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
    private readonly IPlaylistWriter _playlists = Substitute.For<IPlaylistWriter>();
    private readonly ImportListSyncGates _gates = new();

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

    // R-038: a stored secret is reused only for the address it was saved for.
    [Fact]
    public async Task A_masked_secret_sent_with_a_new_host_is_refused_and_the_row_is_unchanged()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(
            Draft(FakeProvider.FakeType) with { Settings = Json("""{"url":"http://lists.local/a","apiKey":"s3cret"}""") },
            CancellationToken.None);

        var update = async () => await service.UpdateAsync(
            list.Id,
            Draft(FakeProvider.FakeType) with
            {
                Settings = Json($$"""{"url":"http://attacker.example/a","apiKey":"{{NotificationSecrets.Mask}}"}"""),
            },
            CancellationToken.None);

        var failure = (await update.Should().ThrowAsync<ImportListValidationException>()).Which;
        failure.Field.Should().Be("apiKey");
        failure.Detail.Should().Be("Enter the API key again: the address changed.");

        (await context.ImportLists.AsNoTracking().SingleAsync()).Settings.Should().Contain("lists.local");
    }

    [Fact]
    public async Task A_masked_secret_with_the_same_host_is_kept_and_a_new_host_with_the_secret_works()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(
            Draft(FakeProvider.FakeType) with { Settings = Json("""{"url":"http://lists.local/a","apiKey":"s3cret"}""") },
            CancellationToken.None);

        await service.UpdateAsync(
            list.Id,
            Draft(FakeProvider.FakeType) with
            {
                Settings = Json($$"""{"url":"http://LISTS.local:80/b","apiKey":"{{NotificationSecrets.Mask}}"}"""),
            },
            CancellationToken.None);

        (await context.ImportLists.AsNoTracking().SingleAsync()).Settings.Should().Contain("s3cret");

        await service.UpdateAsync(
            list.Id,
            Draft(FakeProvider.FakeType) with { Settings = Json("""{"url":"http://new.example/a","apiKey":"fresh-key"}""") },
            CancellationToken.None);

        (await context.ImportLists.AsNoTracking().SingleAsync()).Settings.Should().Contain("fresh-key");
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

    [Theory]
    [InlineData("AddOnly", true, 1)]
    [InlineData("AddAndUnmonitor", false, 1)]
    [InlineData("Mirror", false, 0)]
    public async Task A_policy_applies_to_a_song_only_this_list_added_when_it_leaves_the_source(
        string policy,
        bool monitoredAfter,
        int songsAfter)
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType) with { Policy = policy }, CancellationToken.None);
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111", addedBy: $"list:{list.Id}");
        var daft = await SeedSongAsync(context, "Get Lucky", "22222222-2222-2222-2222-222222222222", addedBy: "ui");
        StubIsrcs(queen, daft);
        _songs.DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            context.Songs.Remove(await context.Songs.SingleAsync(song => song.Id == call.Arg<long>()));
            await context.SaveChangesAsync();

            return true;
        });

        _provider.Entries =
        [
            new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604"),
            new("b", "Daft Punk", "Get Lucky", Isrc: "USQX91300108"),
        ];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        // Both leave the source, which now holds one other item (an empty read would be a suspect one).
        // The song added by hand ("ui") is never the list's to touch.
        _provider.Entries = [new("z", "Somebody", "Else")];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync(song => song.Id == queen.Id)).Should().Be(songsAfter);
        if (songsAfter == 1)
        {
            (await context.Songs.SingleAsync(song => song.Id == queen.Id)).Monitored.Should().Be(monitoredAfter);
        }

        (await context.Songs.SingleAsync(song => song.Id == daft.Id)).Monitored.Should().BeTrue();
    }

    [Fact]
    public async Task Mirror_unmonitors_a_song_whose_grab_started_between_the_check_and_the_delete()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType) with { Policy = "Mirror" }, CancellationToken.None);
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111", addedBy: $"list:{list.Id}");
        StubIsrcs(queen, queen);

        // The sync's own check found nothing in flight; the song service then finds a queue item that started since.
        _songs.DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(call => throw new SongsBusyException([call.Arg<long>()]));

        _provider.Entries = [new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604")];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        _provider.Entries = [new("z", "Somebody", "Else")];
        var message = await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        context.ChangeTracker.Clear();
        var kept = await context.Songs.SingleAsync(song => song.Id == queen.Id);
        kept.Monitored.Should().BeFalse("a busy song is unmonitored, as Mirror does for a song it cannot delete");
        message.Should().NotContain("Failed");
        await _songs.Received(1).DeleteAsync(queen.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mirror_never_deletes_a_song_that_has_a_file_or_that_another_list_still_wants()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType) with { Policy = "Mirror" }, CancellationToken.None);
        var other = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);
        var queen = await SeedSongAsync(context, "Bohemian Rhapsody", "11111111-1111-1111-1111-111111111111", addedBy: $"list:{list.Id}", withFile: true);
        var daft = await SeedSongAsync(context, "Get Lucky", "22222222-2222-2222-2222-222222222222", addedBy: $"list:{list.Id}");
        StubIsrcs(queen, daft);

        _provider.Entries =
        [
            new("a", "Queen", "Bohemian Rhapsody", Isrc: "GBUM71029604"),
            new("b", "Daft Punk", "Get Lucky", Isrc: "USQX91300108"),
        ];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);
        _provider.Entries = [new("b", "Daft Punk", "Get Lucky", Isrc: "USQX91300108")];
        await service.SyncAsync(other.Id, _ => Task.CompletedTask, CancellationToken.None);

        _provider.Entries = [new("z", "Somebody", "Else")];
        await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Id == queen.Id)).Monitored.Should().BeFalse("a song with a file is unmonitored, never deleted");
        (await context.Songs.SingleAsync(song => song.Id == daft.Id)).Monitored.Should().BeTrue("the other list still wants it");
        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
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
    public async Task A_provider_that_times_out_records_that_it_did_not_answer_and_does_not_escape_the_job()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(FakeProvider.FakeType), CancellationToken.None);

        // What the attempt limit raises, as HttpClient.Timeout did: not the caller's cancellation.
        _provider.Throws = new TaskCanceledException("Deezer did not answer within 30 s.");

        var sync = async () => await service.SyncAsync(list.Id, _ => Task.CompletedTask, CancellationToken.None);

        await sync.Should().ThrowAsync<ImportListSyncException>().WithMessage("The source did not answer in time.");
        (await context.ImportLists.AsNoTracking().SingleAsync()).LastSyncMessage
            .Should().Be("Failed: The source did not answer in time.");
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

    private static async Task<Song> SeedSongAsync(
        WondarrDbContext context,
        string title,
        string mbRecordingId,
        string addedBy = "api",
        bool withFile = false)
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
            AddedBy = addedBy,
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        if (withFile)
        {
            context.SongFiles.Add(new SongFile
            {
                SongId = song.Id,
                Path = "/music/" + title + ".flac",
                Codec = "flac",
                Container = "flac",
                QualityId = 36,
                SourceType = "soulseek",
                ImportedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await context.SaveChangesAsync();
        }

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

    private static Microsoft.Extensions.Options.IOptionsMonitor<Wondarr.Core.Searching.SearchOptions> Options()
    {
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Wondarr.Core.Searching.SearchOptions>>();
        monitor.CurrentValue.Returns(new Wondarr.Core.Searching.SearchOptions());

        return monitor;
    }

    private ImportListService NewService(WondarrDbContext context) =>
        new(
            context,
            new PasteListService(context, _resolver, _songs, _timeProvider, NullLogger<PasteListService>.Instance),
            _songs,
            new SongFileLock(),
            Substitute.For<ICommandQueue>(),
            Options(),
            _playlists,
            _gates,
            [_provider, new CsvImportListProvider()],
            _timeProvider,
            NullLogger<ImportListService>.Instance);

    /// <summary>A provider whose source the test sets directly.</summary>
    private sealed class FakeProvider : IImportListProvider
    {
        public const string FakeType = "fake";

        public IReadOnlyList<ImportListEntry> Entries { get; set; } = [];

        public string? Error { get; set; }

        /// <summary>When set, the read throws it, as a provider whose HTTP call timed out does.</summary>
        public Exception? Throws { get; set; }

        public string Type => FakeType;

        public string DisplayName => "Fake";

        public IReadOnlyList<NotificationField> Fields { get; } =
            [new("url", "URL", "url", false, Target: true), new("apiKey", "API key", "password", false, Secret: true)];

        public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) => [];

        public Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken) =>
            Throws is not null ? Task.FromException<ImportListFetchResult>(Throws) : Task.FromResult(Error is null ? new ImportListFetchResult(Entries) : ImportListFetchResult.Failed(Error));
    }
}
