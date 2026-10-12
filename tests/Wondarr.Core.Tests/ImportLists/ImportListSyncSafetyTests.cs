using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Identity;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Importing;
using Wondarr.Core.Jobs;
using Wondarr.Core.Notifications;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The sync's safety rails (code review 2026-10-10, tranche 5a): a read that cannot be trusted drives
/// no removal (R-007, R-021), items that come back are brought back (R-019), unresolved items are
/// retried when their text changes (R-074), Mirror leaves a busy song alone, and a list's songs wait
/// for the first reference scan (owner question 15).
/// </summary>
public sealed class ImportListSyncSafetyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private const string PoisonTitle = "Title poison";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ISongService _songs = Substitute.For<ISongService>();
    private readonly IPlaylistWriter _playlists = Substitute.For<IPlaylistWriter>();
    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();
    private readonly HookedLock _fileLock = new();
    private readonly ScriptedProvider _provider = new();
    private readonly SearchOptions _searchValue = new();

    // ---- R-007: a read that cannot be trusted drives no removal ----

    [Theory]
    [InlineData("Mirror")]
    [InlineData("AddAndUnmonitor")]
    public async Task A_300_item_list_that_reads_zero_rows_deletes_and_unmonitors_nothing(string policy)
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(policy), CancellationToken.None);
        var plain = await SeedSongAsync(context, "Plain", addedBy: $"list:{list.Id}");
        var filed = await SeedSongAsync(context, "Filed", addedBy: $"list:{list.Id}", withFile: true);
        await SeedItemsAsync(context, list, 300, songs: [plain, filed]);

        _provider.Entries = [];
        var message = await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        message.Should().StartWith("Read returned 0 of 300 items; removals skipped.");
        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.Songs.CountAsync(song => song.Monitored)).Should().Be(2);
        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.SuspectReadMessage.Should().Be("Read returned 0 of 300 items; removals skipped");
        stored.LastCompleteReadCount.Should().Be(300);

        // The next complete read clears the suspicion and acts on the list as before.
        _provider.Entries = Entries(300);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.SuspectReadMessage.Should().BeNull();
        stored.LastCompleteReadCount.Should().Be(300);
    }

    // The thresholds: 0 rows for a list that held items; fewer than half of the last complete read
    // for a list of at least 20.
    [Theory]
    [InlineData(100, 49, true)]
    [InlineData(100, 50, false)]
    [InlineData(20, 9, true)]
    [InlineData(20, 10, false)]
    [InlineData(19, 5, false)]
    [InlineData(10, 0, true)]
    [InlineData(1, 0, true)]
    public async Task A_read_is_suspect_when_empty_or_under_half_of_a_list_of_twenty_or_more(int held, int read, bool suspect)
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        await SeedItemsAsync(context, list, held, songs: []);

        _provider.Entries = Entries(read);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        var removed = await context.ImportListItems.CountAsync(item => item.RemovedAt != null);
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();

        if (suspect)
        {
            removed.Should().Be(0);
            stored.SuspectReadMessage.Should().Be(string.Create(CultureInfo.InvariantCulture, $"Read returned {read} of {held} items; removals skipped"));
            stored.LastCompleteReadCount.Should().Be(held, "a suspect read does not become the new baseline");
        }
        else
        {
            removed.Should().Be(held - read);
            stored.SuspectReadMessage.Should().BeNull();
            stored.LastCompleteReadCount.Should().Be(read);
        }
    }

    [Fact]
    public async Task An_edit_of_the_source_followed_by_an_empty_read_deletes_nothing()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror", """{"playlist":"one","apiKey":"k1"}"""), CancellationToken.None);
        await SeedItemsAsync(context, list, 50, songs: []);

        var edited = await service.UpdateAsync(list.Id, Draft("Mirror", """{"playlist":"another","apiKey":"k1"}"""), CancellationToken.None);
        edited!.SourceEditedSinceRead.Should().BeTrue();

        _provider.Entries = [];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage
            .Should().Be("Read returned 0 of 50 items; removals skipped");
    }

    [Fact]
    public async Task An_edit_of_the_source_turns_off_only_the_under_half_rule_until_a_complete_read()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror", """{"playlist":"one"}"""), CancellationToken.None);
        await SeedItemsAsync(context, list, 50, songs: []);

        await service.UpdateAsync(list.Id, Draft("Mirror", """{"playlist":"smaller"}"""), CancellationToken.None);

        // The new playlist really is a fifth of the size: believed, and the flag is spent.
        _provider.Entries = Entries(10);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(40);
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.SourceEditedSinceRead.Should().BeFalse();
        stored.LastCompleteReadCount.Should().Be(10);
    }

    [Fact]
    public async Task Trusting_the_next_read_lets_an_empty_read_remove_everything_once()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        await SeedItemsAsync(context, list, 50, songs: []);

        (await service.TrustNextReadAsync(list.Id, CancellationToken.None))!.TrustNextRead.Should().BeTrue();

        _provider.Entries = [];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(50);
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.TrustNextRead.Should().BeFalse("the flag is spent by that read");
        stored.SuspectReadMessage.Should().BeNull();

        // A second empty read has nothing left to remove and nothing to doubt.
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(50);
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage.Should().BeNull();
    }

    [Fact]
    public async Task Trusting_the_next_read_does_not_trust_a_read_cut_at_a_cap_and_a_pasted_list_has_nothing_to_trust()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        await SeedItemsAsync(context, list, 5, songs: []);
        await service.TrustNextReadAsync(list.Id, CancellationToken.None);

        _provider.Entries = Entries(2);
        _provider.Truncated = true;
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.ImportLists.AsNoTracking().SingleAsync()).TrustNextRead.Should().BeTrue("a cut read does not spend it");

        var paste = await new PasteListService(context, _resolver, _songs, _time, NullLogger<PasteListService>.Instance)
            .CreateAsync("Queen - Bohemian Rhapsody", null, null, CancellationToken.None);
        (await service.TrustNextReadAsync(paste.Id, CancellationToken.None)).Should().BeNull();
        (await service.TrustNextReadAsync(9999, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_rotated_api_key_or_reordered_keys_are_not_a_source_edit_but_a_new_file_is()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror", """{"playlist":"one","apiKey":"k1"}"""), CancellationToken.None);

        var rotated = await service.UpdateAsync(list.Id, Draft("Mirror", """{"apiKey":"k2","playlist":"one"}"""), CancellationToken.None);
        rotated!.SourceEditedSinceRead.Should().BeFalse();

        var file = await service.UpdateAsync(
            list.Id,
            Draft("Mirror", """{"playlist":"one","apiKey":"k2"}""") with { SourceText = "a,b" },
            CancellationToken.None);
        file!.SourceEditedSinceRead.Should().BeTrue();
    }

    [Fact]
    public async Task An_add_only_list_has_nothing_to_lose_and_is_not_held_to_the_thresholds()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        await SeedItemsAsync(context, list, 30, songs: []);

        _provider.Entries = [];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(30);
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_read_is_recorded_and_warned_about_until_the_next_complete_read()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror") with { Name = "Road trip" }, CancellationToken.None);
        var health = new ImportListReadHealthCheck(context);

        (await health.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);

        _provider.Error = "YouTube Music answered HTTP 429.";
        await FluentActions.Awaiting(() => service.SyncAsync(list.Id, Quiet, CancellationToken.None))
            .Should().ThrowAsync<ImportListSyncException>();

        var warning = await health.CheckAsync(CancellationToken.None);
        warning.Type.Should().Be(HealthCheckResult.Warning);
        warning.Message.Should().Contain("Road trip").And.Contain("429").And.NotContain("..");

        _provider.Error = null;
        _provider.Entries = Entries(2);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        (await health.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task A_suspect_read_raises_a_health_warning_naming_the_list_that_clears_on_the_next_complete_read()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror") with { Name = "Gym mix" }, CancellationToken.None);
        await SeedItemsAsync(context, list, 300, songs: []);
        var health = new ImportListReadHealthCheck(context);

        _provider.Entries = [];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        var warning = await health.CheckAsync(CancellationToken.None);
        warning.Type.Should().Be(HealthCheckResult.Warning);
        warning.Message.Should().Be("Import list 'Gym mix': Read returned 0 of 300 items; removals skipped.");

        _provider.Entries = Entries(300);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        (await health.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);
    }

    // ---- a short read: the source announced more than it returned, without a cap ----

    [Fact]
    public async Task A_short_read_is_suspect_with_its_own_message_and_a_trusted_one_applies_its_removals_once()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        await SeedItemsAsync(context, list, 100, songs: []);

        _provider.Entries = Entries(50);
        _provider.ShortRead = "Deezer reported 100 tracks but returned 50";
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage
            .Should().Be("Deezer reported 100 tracks but returned 50; removals skipped");

        // The user says it is fine: the removals apply, the flag is spent, the baseline moves.
        await service.TrustNextReadAsync(list.Id, CancellationToken.None);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(50);
        var stored = await context.ImportLists.AsNoTracking().SingleAsync();
        stored.TrustNextRead.Should().BeFalse();
        stored.SuspectReadMessage.Should().BeNull();
        stored.LastCompleteReadCount.Should().Be(50);

        // The next short read is doubted again.
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage.Should().NotBeNull();
    }

    // ---- R-021: a source longer than a cap ----

    [Fact]
    public async Task A_provider_that_stopped_at_its_cap_adds_new_items_but_removes_nothing()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        var tail = await SeedSongAsync(context, "Tail", addedBy: $"list:{list.Id}");
        await SeedItemsAsync(context, list, 5, songs: [tail], songItemIndex: 4);

        // Newest first: a new love pushes the oldest item past the provider's cap.
        _provider.Entries = [.. Entries(4).Prepend(Entry("new")).Take(5)];
        _provider.Truncated = true;
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.ExternalId == "new")).Should().Be(1);
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.Songs.SingleAsync(song => song.Id == tail.Id)).Monitored.Should().BeTrue();
        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage
            .Should().Be("Read stopped at the source's cap after 5 items; removals skipped");
    }

    [Fact]
    public async Task A_truncated_read_removes_nothing_even_for_an_add_only_list()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        await SeedItemsAsync(context, list, 5, songs: []);

        _provider.Entries = Entries(3);
        _provider.Truncated = true;
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_loved_tracks_list_of_more_than_10000_items_under_mirror_keeps_the_song_past_the_cap()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);
        var tail = await SeedSongAsync(context, "Oldest love", addedBy: $"list:{list.Id}");
        var held = ImportListService.MaxItems + 1;
        await SeedItemsAsync(context, list, held, songs: [tail], songItemIndex: held - 1);

        // The source holds 10,001 items; the service reads 10,000 of them, and the 10,001st is the tail.
        _provider.Entries = Entries(held);
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.CountAsync(item => item.RemovedAt != null)).Should().Be(0);
        (await context.Songs.SingleAsync(song => song.Id == tail.Id)).Monitored.Should().BeTrue();
        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        (await context.ImportLists.AsNoTracking().SingleAsync()).SuspectReadMessage
            .Should().Be("The source is longer than Wondarr reads (10,000), so removals are never applied");
    }

    // ---- R-019 / owner question 13: returning items ----

    [Fact]
    public async Task A_song_mirror_deleted_comes_back_when_its_item_returns()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        var first = await context.Songs.AsNoTracking().SingleAsync(song => song.Title == "Title a");

        context.ChangeTracker.Clear();
        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync(song => song.Title == "Title a")).Should().Be(0, "Mirror deleted the song");
        var item = await context.ImportListItems.AsNoTracking().SingleAsync(candidate => candidate.ExternalId == "a");
        item.RemovedAt.Should().NotBeNull();
        item.SongId.Should().BeNull();

        _provider.Entries = [Entry("a"), Entry("b")];
        var message = await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        message.Should().Contain("1 back");
        context.ChangeTracker.Clear();
        item = await context.ImportListItems.AsNoTracking().SingleAsync(candidate => candidate.ExternalId == "a");
        item.RemovedAt.Should().BeNull();
        item.State.Should().Be(ImportListItemState.Added);
        var again = await context.Songs.AsNoTracking().SingleAsync(song => song.Title == "Title a");
        item.SongId.Should().Be(again.Id);
        again.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task A_song_the_policy_unmonitored_is_monitored_again_when_its_item_returns()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("AddAndUnmonitor"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();

        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored.Should().BeFalse();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored.Should().BeTrue();

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored.Should().BeTrue();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored.Should().BeFalse();
    }

    [Fact]
    public async Task A_song_the_user_unmonitored_stays_unmonitored_when_its_item_returns()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("AddAndUnmonitor"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        // The user turns the song off themselves, then the item leaves and returns.
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored = false;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored
            .Should().BeFalse("the policy did not unmonitor it: the user had");

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored.Should().BeFalse();
    }

    [Fact]
    public async Task A_sync_isolates_an_identity_that_fails_to_add_and_adds_the_rest()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("AddOnly"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("poison"), Entry("b")];
        var act = async () => await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _songs.Received().AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Is<SongAddOptions>(options => options.IsolateFailures),
            Arg.Any<CancellationToken>());

        context.ChangeTracker.Clear();
        var items = await context.ImportListItems.OrderBy(item => item.Position).ToListAsync();

        items.Select(item => item.State).Should().Equal(
            ImportListItemState.Added,
            ImportListItemState.Unresolved,
            ImportListItemState.Added);
        items[1].SongId.Should().BeNull();
        items[1].Reason.Should().Be("UNIQUE constraint failed: song.mb_recording_id");
        items[0].SongId.Should().NotBeNull();
        items[2].SongId.Should().NotBeNull();
        (await context.Songs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_song_the_user_unmonitors_through_the_song_service_stays_unmonitored_when_its_item_returns()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("AddAndUnmonitor"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();
        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        var song = await context.Songs.SingleAsync(row => row.Title == "Title a");
        song.Monitored.Should().BeFalse();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored.Should().BeTrue("the policy sets it");

        // The user monitors it, then unmonitors it again, both before the item returns.
        var songs = Songs.SongSeed.Service(context);
        await songs.UpdateAsync(song.Id, monitored: true, qualityProfileId: null, CancellationToken.None);
        await songs.UpdateAsync(song.Id, monitored: false, qualityProfileId: null, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored
            .Should().BeFalse("a user's change clears the flag at once");

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(row => row.Title == "Title a")).Monitored.Should().BeFalse();
    }

    [Fact]
    public async Task A_song_someone_monitored_after_the_policy_is_not_touched_when_its_item_returns()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("AddAndUnmonitor"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();
        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        // The user monitors the song again by hand; the next sync sees it and lets the flag go.
        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored = true;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored.Should().BeFalse();

        // Then they switch it off again, their own decision; the item returning must not undo it.
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored = false;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Songs.SingleAsync(song => song.Title == "Title a")).Monitored.Should().BeFalse();
    }

    // ---- R-074: unresolved items are retried when their text changes ----

    [Fact]
    public async Task An_unresolved_item_whose_text_changed_is_looked_up_again_and_one_that_did_not_is_not()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);

        // Nothing resolves "x" without an ISRC.
        _provider.Entries = [new("x", "Some Artist", "Some Title")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        (await context.ImportListItems.SingleAsync()).State.Should().Be(ImportListItemState.Unresolved);
        _resolver.ClearReceivedCalls();

        // Same text: left for the review screen, the resolver is not asked again.
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        await _resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await context.ImportListItems.SingleAsync()).State.Should().Be(ImportListItemState.Unresolved);

        // The source learned an ISRC: the item is pending again, and resolves.
        _provider.Entries = [new("x", "Some Artist", "Some Title", Isrc: "ISRC-X")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        var item = await context.ImportListItems.SingleAsync();
        item.State.Should().Be(ImportListItemState.Added);
        item.SongId.Should().NotBeNull();
    }

    // ---- Mirror's delete needs a busy check ----

    [Fact]
    public async Task Mirror_does_not_delete_a_song_with_a_grab_in_flight()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        var song = await context.Songs.AsNoTracking().SingleAsync(candidate => candidate.Title == "Title a");
        await SeedQueueItemAsync(context, song.Id, QueueItemState.Downloading);
        context.ChangeTracker.Clear();

        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        context.ChangeTracker.Clear();
        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        var kept = await context.Songs.SingleAsync(candidate => candidate.Id == song.Id);
        kept.Monitored.Should().BeFalse("it is unmonitored instead, like any song Mirror cannot delete");
        (await context.ImportListItems.SingleAsync(item => item.ExternalId == "a")).PolicyUnmonitored.Should().BeTrue();
    }

    [Fact]
    public async Task Mirror_does_not_delete_a_song_whose_file_lock_is_in_use()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        var song = await context.Songs.AsNoTracking().SingleAsync(candidate => candidate.Title == "Title a");
        context.ChangeTracker.Clear();

        // An import is placing the song's file right now.
        await using var held = await _fileLock.AcquireAsync(song.Id, CancellationToken.None);

        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mirror_does_not_delete_a_song_that_got_a_file_after_the_decision()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        var song = await context.Songs.AsNoTracking().SingleAsync(candidate => candidate.Title == "Title a");
        context.ChangeTracker.Clear();

        // The file lands after the policy chose to delete and before the song is deleted: an import
        // that finishes while Mirror waits for the song's lock.
        _fileLock.OnAcquire = id =>
        {
            using var other = _database.CreateContext(_time);
            other.SongFiles.Add(NewFile(id, "Title a"));
            other.SaveChanges();
        };
        _provider.Entries = [Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mirror_leaves_alone_a_song_that_vanished_before_it_could_be_deleted()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror"), CancellationToken.None);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        var song = await context.Songs.AsNoTracking().SingleAsync(candidate => candidate.Title == "Title a");
        context.ChangeTracker.Clear();

        // Somebody deletes the song between the policy's snapshot and its delete.
        _fileLock.OnAcquire = id =>
        {
            using var other = _database.CreateContext(_time);
            other.Songs.Where(candidate => candidate.Id == id).ExecuteDelete();
        };
        _provider.Entries = [Entry("b")];

        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        await _songs.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync(candidate => candidate.Id == song.Id)).Should().Be(0);
    }

    // ---- the hourly playlist refresh respects the per-list gate ----

    [Fact]
    public async Task The_hourly_playlist_refresh_skips_a_list_that_is_being_synced()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft("Mirror") with { PlexPlaylist = true }, CancellationToken.None);

        // A first sync is mid-way: it holds the gate and has not recorded a Plex playlist key yet.
        using (ImportListSyncGates.TryEnter(list.Id))
        {
            (await service.RefreshPlaylistsAsync(CancellationToken.None)).Should().Be(0);
            await _playlists.DidNotReceive().WriteAsync(list.Id, Arg.Any<CancellationToken>());
        }

        _playlists.WriteAsync(list.Id, Arg.Any<CancellationToken>()).Returns("written");
        (await service.RefreshPlaylistsAsync(CancellationToken.None)).Should().Be(1);
        await _playlists.Received(1).WriteAsync(list.Id, Arg.Any<CancellationToken>());
    }

    // ---- owner question 15: songs wait for the reference scan ----

    [Fact]
    public async Task A_list_added_song_is_not_searched_while_a_reference_row_is_pending_and_is_after_the_scan_or_the_cap()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        var file = await AddReferenceFileAsync(context, ReferenceFileState.Pending, message: null);

        _provider.Entries = [Entry("a")];
        var message = await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        message.Should().Contain("not searched until the reference scan of Old MP3s finishes");
        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Is<SongAddOptions>(options => !options.SearchOnAdd),
            Arg.Any<CancellationToken>());

        // Still scanning, and the song is young: nothing is queued, and Health says why.
        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        var notice = await new ImportListScanWaitHealthCheck(context, _time).CheckAsync(CancellationToken.None);
        notice.Type.Should().Be(HealthCheckResult.Notice);
        notice.Message.Should().Be("Import list songs wait for the reference scan of Old MP3s to finish");

        // Past the cap the song is searched anyway, once, and Health warns.
        _time.Advance(TimeSpan.FromHours(25));
        var overdue = await new ImportListScanWaitHealthCheck(context, _time).CheckAsync(CancellationToken.None);
        overdue.Type.Should().Be(HealthCheckResult.Warning);
        overdue.Message.Should().Contain("Old MP3s");

        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(1);
        await _commands.Received(1).EnqueueAsync(
            SongSearchCommandHandler.CommandName,
            Arg.Is<string?>(body => body != null && body.Contains("\"songId\"")),
            CommandTrigger.Unspecified,
            Arg.Any<CancellationToken>());

        // The next hourly run does not queue it again, scan or no scan.
        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        file.State = ReferenceFileState.Identified;
        await context.SaveChangesAsync();
        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        await _commands.Received(1).EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_scan_finishes_the_waiting_song_is_searched_by_a_song_search_once()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        var file = await AddReferenceFileAsync(context, ReferenceFileState.Pending, message: null);

        _provider.Entries = [Entry("a"), Entry("b")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        file.State = ReferenceFileState.Identified;
        await context.SaveChangesAsync();

        (await new ImportListScanWaitHealthCheck(context, _time).CheckAsync(CancellationToken.None)).Type
            .Should().Be(HealthCheckResult.Ok);
        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(2);
        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        await _commands.Received(2).EnqueueAsync(
            SongSearchCommandHandler.CommandName,
            Arg.Any<string?>(),
            CommandTrigger.Unspecified,
            Arg.Any<CancellationToken>());

        // And a list that syncs after the scan searches at once.
        _songs.ClearReceivedCalls();
        _provider.Entries = [Entry("a"), Entry("b"), Entry("c")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Is<SongAddOptions>(options => options.SearchOnAdd),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deferred_reference_row_does_not_hold_back_the_search()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        await AddReferenceFileAsync(context, ReferenceFileState.Pending, message: "AcoustID unavailable; the next scan retries this file");

        _provider.Entries = [Entry("a")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Is<SongAddOptions>(options => options.SearchOnAdd),
            Arg.Any<CancellationToken>());
        (await new ImportListScanWaitHealthCheck(context, _time).CheckAsync(CancellationToken.None)).Type
            .Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task Nothing_is_queued_when_search_on_add_is_off()
    {
        _searchValue.SearchOnAdd = false;
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        var file = await AddReferenceFileAsync(context, ReferenceFileState.Pending, message: null);

        _provider.Entries = [Entry("a")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);
        file.State = ReferenceFileState.Identified;
        await context.SaveChangesAsync();
        _time.Advance(TimeSpan.FromHours(48));

        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        await _commands.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_queued_when_no_list_song_is_waiting_for_its_first_search()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);

        _provider.Entries = [Entry("a")];
        await service.SyncAsync(list.Id, Quiet, CancellationToken.None);

        // The song was added with a search (no scan was running), so a search run exists for it.
        var song = await context.Songs.AsNoTracking().SingleAsync();
        context.SearchRuns.Add(new SearchRun { SongId = song.Id, Trigger = SearchTrigger.Automatic, StartedAt = Now.UtcDateTime });
        await context.SaveChangesAsync();

        (await service.QueueSearchForWaitingSongsAsync(CancellationToken.None)).Should().Be(0);
        await _commands.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pasted_list_is_a_manual_add_and_searches_regardless_of_a_pending_reference_row()
    {
        await using var context = await ContextAsync();
        var reference = new ReferenceLibrary { Name = "Old MP3s", RootPath = "/old" };
        context.ReferenceLibraries.Add(reference);
        context.ReferenceFiles.Add(new ReferenceFile
        {
            ReferenceLibrary = reference,
            RelativePath = "a.mp3",
            State = ReferenceFileState.Pending,
            LastSeenAt = Now.UtcDateTime,
        });
        await context.SaveChangesAsync();
        WireSongs(context);
        var paste = new PasteListService(context, _resolver, _songs, _time, NullLogger<PasteListService>.Instance);
        var pasted = await paste.CreateAsync("ISRC-P", null, null, CancellationToken.None);

        await paste.ProcessAsync(pasted.Id, Quiet, CancellationToken.None);

        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Is<SongAddOptions>(options => options.SearchOnAdd),
            Arg.Any<CancellationToken>());
    }

    // ---- R-020: the unique index ----

    [Fact]
    public async Task A_duplicate_item_for_one_list_is_refused_by_the_database()
    {
        await using var context = await ContextAsync();
        var service = NewService(context);
        var list = await service.CreateAsync(Draft(ImportList.AddOnlyPolicy), CancellationToken.None);
        context.ImportListItems.Add(new ImportListItem { ImportListId = list.Id, ExternalId = "a" });
        await context.SaveChangesAsync();

        context.ImportListItems.Add(new ImportListItem { ImportListId = list.Id, ExternalId = "a" });

        await context.Invoking(db => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private static Task Quiet(string line) => Task.CompletedTask;

    private static async Task<ReferenceFile> AddReferenceFileAsync(WondarrDbContext context, ReferenceFileState state, string? message)
    {
        var reference = new ReferenceLibrary { Name = "Old MP3s", RootPath = "/old" };
        var file = new ReferenceFile
        {
            ReferenceLibrary = reference,
            RelativePath = "a.mp3",
            State = state,
            Message = message,
            LastSeenAt = Now.UtcDateTime,
        };
        context.ReferenceLibraries.Add(reference);
        context.ReferenceFiles.Add(file);
        await context.SaveChangesAsync();

        return file;
    }

    private static ImportListDraft Draft(string policy, string settings = "{}") =>
        new(
            ScriptedProvider.ScriptedType,
            "My playlist",
            JsonDocument.Parse(settings).RootElement.Clone(),
            SourceText: null,
            Policy: policy,
            QualityProfileId: null,
            LibraryId: null);

    private static ImportListEntry Entry(string id) =>
        new(id, "Artist " + id, "Title " + id, Isrc: "ISRC-" + id);

    /// <summary>Entries i0 to i{count-1}, the ids <see cref="SeedItemsAsync"/> seeds.</summary>
    private static List<ImportListEntry> Entries(int count) =>
        [.. Enumerable.Range(0, count).Select(index => Entry("i" + index.ToString(CultureInfo.InvariantCulture)))];

    private static SongFile NewFile(long songId, string title) => new()
    {
        SongId = songId,
        Path = "/music/" + title + ".flac",
        Codec = "flac",
        Container = "flac",
        QualityId = 36,
        SourceType = "soulseek",
        ImportedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Seeds <paramref name="count"/> items i0..i{count-1} as already added, as a complete read of the
    /// list would have left them; <paramref name="songs"/> are attached to the first items, or to the
    /// one at <paramref name="songItemIndex"/> when it is given.
    /// </summary>
    private static async Task SeedItemsAsync(
        WondarrDbContext context,
        ImportList list,
        int count,
        IReadOnlyList<Song> songs,
        int? songItemIndex = null)
    {
        for (var index = 0; index < count; index++)
        {
            var id = "i" + index.ToString(CultureInfo.InvariantCulture);
            var songIndex = songItemIndex is { } at ? (index == at ? 0 : -1) : (index < songs.Count ? index : -1);
            var entry = Entry(id);

            context.ImportListItems.Add(new ImportListItem
            {
                ImportListId = list.Id,
                ExternalId = id,
                Position = index,
                Raw = ImportListItemJson.Write(entry),
                State = ImportListItemState.Added,
                SongId = songIndex >= 0 ? songs[songIndex].Id : null,
            });
        }

        list.LastCompleteReadCount = count;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task<Song> SeedSongAsync(
        WondarrDbContext context,
        string title,
        string addedBy,
        bool withFile = false)
    {
        var artist = new Artist { Name = title, SortName = title };
        context.Artists.Add(artist);

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtist = artist,
            MbRecordingId = Guid.NewGuid().ToString(),
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = addedBy,
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        if (withFile)
        {
            context.SongFiles.Add(NewFile(song.Id, title));
            await context.SaveChangesAsync();
        }

        return song;
    }

    private async Task SeedQueueItemAsync(WondarrDbContext context, long songId, QueueItemState state)
    {
        var run = new SearchRun { SongId = songId, Trigger = SearchTrigger.Automatic, StartedAt = _time.GetUtcNow().UtcDateTime };
        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = "peer\u001fMusic\\inflight.flac",
            DisplayName = "inflight.flac",
            RemotePath = "Music\\inflight.flac",
            Provider = "peer",
        };
        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        context.QueueItems.Add(new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = SourceTypes.Soulseek,
            Destination = $"wondarr/{run.Id}",
            State = state,
            Attempt = 1,
        });
        await context.SaveChangesAsync();
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        var context = _database.CreateContext(_time);
        WireSongs(context);

        return context;
    }

    /// <summary>
    /// The resolver answers an <c>ISRC-x</c> input with a recording of its own; the song service adds a
    /// real row for each identity (or finds the one that is there) and deletes real rows.
    /// </summary>
    private void WireSongs(WondarrDbContext context)
    {
        _resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<string>().StartsWith("ISRC-", StringComparison.Ordinal)
                ? new ResolveResult { Status = ResolveStatus.Resolved, Identity = Identity(call.Arg<string>()[5..].ToLowerInvariant()) }
                : new ResolveResult { Status = ResolveStatus.Unresolved, Reason = "No match." }));

        _songs
            .AddIdentitiesAsync(Arg.Any<IReadOnlyList<SongIdentity>>(), Arg.Any<SongAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var results = new List<SongAddResult>();

                foreach (var identity in call.Arg<IReadOnlyList<SongIdentity>>())
                {
                    if (identity.Title == PoisonTitle)
                    {
                        // A failed result carries an unsaved placeholder, never a stored song.
                        results.Add(new SongAddResult(identity, SongAddOutcome.Failed, new Song { Title = identity.Title }, "UNIQUE constraint failed: song.mb_recording_id"));
                        continue;
                    }

                    var existing = await context.Songs.FirstOrDefaultAsync(song => song.MbRecordingId == identity.MbRecordingId);

                    if (existing is not null)
                    {
                        results.Add(new SongAddResult(identity, SongAddOutcome.AlreadyExists, existing));
                        continue;
                    }

                    var created = await SeedSongAsync(context, identity.Title, call.Arg<SongAddOptions>().AddedBy);
                    created.MbRecordingId = identity.MbRecordingId;
                    await context.SaveChangesAsync();
                    results.Add(new SongAddResult(identity, SongAddOutcome.Added, created));
                }

                return (IReadOnlyList<SongAddResult>)results;
            });

        _songs.DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            context.Songs.Remove(await context.Songs.SingleAsync(song => song.Id == call.Arg<long>()));
            await context.SaveChangesAsync();

            return true;
        });
    }

    private static SongIdentity Identity(string key) => new()
    {
        Source = "musicbrainz",
        MbRecordingId = Guid.NewGuid().ToString(),
        Title = "Title " + key,
        ArtistCredit = "Artist " + key,
        Artists = [new IdentityArtist("Artist " + key, "Artist " + key, null, null, ArtistRole.Main, 0)],
    };

    private ImportListService NewService(WondarrDbContext context) =>
        new(
            context,
            new PasteListService(context, _resolver, _songs, _time, NullLogger<PasteListService>.Instance),
            _songs,
            _fileLock,
            _commands,
            OptionsMonitorOf(_searchValue),
            _playlists,
            [_provider],
            _time,
            NullLogger<ImportListService>.Instance);

    private static IOptionsMonitor<SearchOptions> OptionsMonitorOf(SearchOptions value)
    {
        var monitor = Substitute.For<IOptionsMonitor<SearchOptions>>();
        monitor.CurrentValue.Returns(value);

        return monitor;
    }

    /// <summary>The real per-song lock, with a hook that runs as a caller takes it.</summary>
    private sealed class HookedLock : ISongFileLock
    {
        private readonly SongFileLock _inner = new();

        public Action<long>? OnAcquire { get; set; }

        public async ValueTask<IAsyncDisposable> AcquireAsync(long songId, CancellationToken cancellationToken)
        {
            var handle = await _inner.AcquireAsync(songId, cancellationToken);
            OnAcquire?.Invoke(songId);

            return handle;
        }
    }

    /// <summary>A provider whose source, completeness and failure the test sets directly.</summary>
    private sealed class ScriptedProvider : IImportListProvider
    {
        public const string ScriptedType = "scripted";

        public IReadOnlyList<ImportListEntry> Entries { get; set; } = [];

        public bool Truncated { get; set; }

        public string? ShortRead { get; set; }

        public string? Error { get; set; }

        public string Type => ScriptedType;

        public string DisplayName => "Scripted";

        public IReadOnlyList<NotificationField> Fields { get; } =
            [new("playlist", "Playlist", "text", false), new("apiKey", "API key", "password", false, Secret: true)];

        public IReadOnlyList<string> Validate(JsonElement settings, string? sourceText) => [];

        public Task<ImportListFetchResult> FetchAsync(ImportList list, CancellationToken cancellationToken)
        {
            return Task.FromResult(Error is null
                ? new ImportListFetchResult(Entries, Truncated: Truncated, ShortRead: ShortRead)
                : ImportListFetchResult.Failed(Error));
        }
    }
}
