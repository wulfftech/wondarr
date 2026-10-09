using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>The mass editor, the tag list and the tag rules against a real migrated SQLite database.</summary>
public sealed class SongEditorServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Tags_are_trimmed_lower_cased_deduplicated_and_validated()
    {
        SongTags.Normalize([" Chill ", "CHILL", "", "  ", "Road Trip"]).Should().Equal("chill", "road trip");

        var comma = () => SongTags.Normalize(["a,b"]);
        comma.Should().Throw<ArgumentException>();

        var tooLong = () => SongTags.Normalize([new string('x', 65)]);
        tooLong.Should().Throw<ArgumentException>();

        SongTags.Normalize([new string('x', 64)]).Should().HaveCount(1);

        var tooMany = () => SongTags.Normalize(Enumerable.Range(0, 51).Select(i => $"t{i}"));
        tooMany.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Tags_add_remove_and_replace_are_normalised_and_merged()
    {
        await using var context = await ContextAsync();
        var a = await SongSeed.AddAsync(context, "A", tags: ["chill"]);
        var b = await SongSeed.AddAsync(context, "B");
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        await editor.EditAsync(new SongEditorRequest([a.Id, b.Id], Tags: [" Chill", "NEW", "new"], ApplyTags: "add"), CancellationToken.None);
        (await TagsAsync(a.Id)).Should().Equal("chill", "new");
        (await TagsAsync(b.Id)).Should().Equal("chill", "new");

        await editor.EditAsync(new SongEditorRequest([a.Id, b.Id], Tags: ["CHILL"], ApplyTags: "remove"), CancellationToken.None);
        (await TagsAsync(a.Id)).Should().Equal("new");

        await editor.EditAsync(new SongEditorRequest([a.Id], Tags: ["x", "y"], ApplyTags: "replace"), CancellationToken.None);
        (await TagsAsync(a.Id)).Should().Equal("x", "y");
        (await TagsAsync(b.Id)).Should().Equal("new");

        var tags = await editor.GetTagsAsync(CancellationToken.None);
        tags.Should().Equal(new SongTagCount("new", 1), new SongTagCount("x", 1), new SongTagCount("y", 1));
    }

    [Fact]
    public async Task Monitored_and_profile_change_in_one_call_and_the_result_carries_the_songs()
    {
        await using var context = await ContextAsync();
        var a = await SongSeed.AddAsync(context, "A");
        var b = await SongSeed.AddAsync(context, "B");
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        var result = await editor.EditAsync(
            new SongEditorRequest([b.Id, a.Id], Monitored: false, QualityProfileId: SeedData.LosslessProfileId),
            CancellationToken.None);

        result.Songs.Select(song => song.Title).Should().Equal("B", "A");
        result.Songs.Should().OnlyContain(song => !song.Monitored && song.QualityProfileId == SeedData.LosslessProfileId);
        result.MoveCommandIds.Should().BeEmpty();

        await using var fresh = _database.CreateContext(_time);
        (await fresh.Songs.CountAsync(song => !song.Monitored && song.QualityProfileId == SeedData.LosslessProfileId)).Should().Be(2);
    }

    [Fact]
    public async Task A_library_change_queues_MoveSongs_for_only_the_songs_not_already_there()
    {
        await using var context = await ContextAsync();
        var library = await SongSeed.AddLibraryAsync(context);
        var here = await SongSeed.AddAsync(context, "Here", libraryId: library.Id);
        var there1 = await SongSeed.AddAsync(context, "There1");
        var there2 = await SongSeed.AddAsync(context, "There2");

        var commands = Substitute.For<ICommandQueue>();
        string? body = null;
        commands.EnqueueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CommandTrigger>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                body = call.ArgAt<string?>(1);

                return Task.FromResult(new CommandRecord { Id = 77, Name = call.ArgAt<string>(0) });
            });

        var result = await SongSeed.Editor(context, commands).EditAsync(
            new SongEditorRequest([here.Id, there1.Id, there2.Id], LibraryId: library.Id),
            CancellationToken.None);

        result.MoveCommandIds.Should().Equal(77);
        await commands.Received(1).EnqueueAsync("MoveSongs", Arg.Any<string?>(), CommandTrigger.Manual, Arg.Any<CancellationToken>());

        using var document = JsonDocument.Parse(body!);
        document.RootElement.GetProperty("name").GetString().Should().Be("MoveSongs");
        document.RootElement.GetProperty("libraryId").GetInt64().Should().Be(library.Id);
        document.RootElement.GetProperty("songIds").EnumerateArray().Select(id => id.GetInt64())
            .Should().BeEquivalentTo([there1.Id, there2.Id]);
    }

    [Fact]
    public async Task An_unknown_id_changes_nothing()
    {
        await using var context = await ContextAsync();
        var a = await SongSeed.AddAsync(context, "A");
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        var act = () => editor.EditAsync(new SongEditorRequest([a.Id, 9999], Monitored: false), CancellationToken.None);

        (await act.Should().ThrowAsync<SongsNotFoundException>()).Which.MissingIds.Should().Equal(9999);

        await using var fresh = _database.CreateContext(_time);
        (await fresh.Songs.SingleAsync()).Monitored.Should().BeTrue();
    }

    [Fact]
    public async Task Unusable_requests_are_refused()
    {
        await using var context = await ContextAsync();
        var a = await SongSeed.AddAsync(context, "A");
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        await Refused(editor, new SongEditorRequest([a.Id]));
        await Refused(editor, new SongEditorRequest([a.Id], Tags: ["x"]));
        await Refused(editor, new SongEditorRequest([a.Id], Tags: ["x"], ApplyTags: "merge"));
        await Refused(editor, new SongEditorRequest([a.Id], ApplyTags: "add"));
        await Refused(editor, new SongEditorRequest([a.Id], QualityProfileId: 999));
        await Refused(editor, new SongEditorRequest([a.Id], LibraryId: 999));
        await Refused(editor, new SongEditorRequest([], Monitored: true));
        await Refused(editor, new SongEditorRequest(null, Monitored: true));
        await Refused(editor, new SongEditorRequest([a.Id, a.Id], Monitored: true));
        await Refused(editor, new SongEditorRequest([.. Enumerable.Range(1, 1001).Select(i => (long)i)], Monitored: true));
        await Refused(editor, new SongEditorRequest([a.Id], Tags: [new string('x', 65)], ApplyTags: "add"));
    }

    [Fact]
    public async Task Deleting_removes_the_songs_and_an_unknown_id_deletes_nothing()
    {
        await using var context = await ContextAsync();
        var a = await SongSeed.AddAsync(context, "A", fileQuality: SongSeed.HighQuality);
        var b = await SongSeed.AddAsync(context, "B");
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        var bad = () => editor.DeleteAsync([a.Id, 9999], CancellationToken.None);
        await bad.Should().ThrowAsync<SongsNotFoundException>();
        (await context.Songs.CountAsync()).Should().Be(2);

        (await editor.DeleteAsync([a.Id, b.Id], CancellationToken.None)).Should().Be(2);
        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_song_still_downloading_is_refused_and_nothing_is_deleted()
    {
        await using var context = await ContextAsync();
        var busy = await SongSeed.AddAsync(context, "Busy");
        var done = await SongSeed.AddAsync(context, "Done");
        await AddQueueItemAsync(context, busy.Id, QueueItemState.Downloading);
        await AddQueueItemAsync(context, done.Id, QueueItemState.Imported);
        var editor = SongSeed.Editor(context, Substitute.For<ICommandQueue>());

        var act = () => editor.DeleteAsync([busy.Id, done.Id], CancellationToken.None);

        (await act.Should().ThrowAsync<SongsBusyException>()).Which.BusyIds.Should().Equal(busy.Id);
        context.ChangeTracker.Clear();
        (await context.Songs.CountAsync()).Should().Be(2);

        // A finished queue item does not hold a song back; it goes with the song.
        (await editor.DeleteAsync([done.Id], CancellationToken.None)).Should().Be(1);
        context.ChangeTracker.Clear();
        (await context.QueueItems.CountAsync(item => item.SongId == done.Id)).Should().Be(0);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task Refused(SongEditorService editor, SongEditorRequest request)
    {
        var act = () => editor.EditAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private async Task<List<string>> TagsAsync(long id)
    {
        await using var fresh = _database.CreateContext(_time);

        return (await fresh.Songs.AsNoTracking().SingleAsync(song => song.Id == id)).Tags;
    }

    private async Task AddQueueItemAsync(WondarrDbContext context, long songId, QueueItemState state)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var run = new SearchRun { SongId = songId, StartedAt = now };
        var candidate = new CandidateRecord
        {
            SearchRun = run,
            SongId = songId,
            SourceType = "soulseek",
            BlocklistKey = "user/path/" + songId,
            DisplayName = "file.flac",
            RemotePath = "path/file.flac",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        context.QueueItems.Add(new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = "soulseek",
            Destination = "wondarr/" + songId,
            State = state,
            StateChangedAt = now,
            LastProgressAt = now,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }
}
