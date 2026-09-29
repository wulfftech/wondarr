using Wondarr.Core.Domain;
using Wondarr.Core.Paging;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

public sealed class QueueServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task State_and_progress_timestamps_move_only_when_they_change()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var (songId, runId, candidateId) = await SourceTestData.SeedSongWithCandidateAsync(database, time);

        var service = new QueueService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var item = await service.AddAsync(NewItem(songId, runId, candidateId), token);
        item.StateChangedAt.Should().Be(Now);
        item.LastProgressAt.Should().Be(Now);

        // The state moved and bytes arrived: both timestamps move.
        time.Advance(TimeSpan.FromMinutes(5));
        var downloading = await service.GetAsync(item.Id, token);
        downloading!.State = QueueItemState.Downloading;
        downloading.BytesTransferred = 100;
        await service.UpdateAsync(downloading, token);
        downloading.StateChangedAt.Should().Be(Now.AddMinutes(5));
        downloading.LastProgressAt.Should().Be(Now.AddMinutes(5));

        // A poll that saw nothing new writes neither timestamp.
        time.Advance(TimeSpan.FromMinutes(5));
        var idle = await service.GetAsync(item.Id, token);
        await service.UpdateAsync(idle!, token);
        idle!.StateChangedAt.Should().Be(Now.AddMinutes(5));
        idle.LastProgressAt.Should().Be(Now.AddMinutes(5));

        // Bytes grew but the state did not: progress moves, the state timestamp does not.
        time.Advance(TimeSpan.FromMinutes(5));
        var growing = await service.GetAsync(item.Id, token);
        growing!.BytesTransferred = 200;
        await service.UpdateAsync(growing, token);
        growing.LastProgressAt.Should().Be(Now.AddMinutes(15));
        growing.StateChangedAt.Should().Be(Now.AddMinutes(5));

        // The state moved but no bytes did: the state timestamp moves, progress does not.
        time.Advance(TimeSpan.FromMinutes(5));
        var completed = await service.GetAsync(item.Id, token);
        completed!.State = QueueItemState.Completed;
        await service.UpdateAsync(completed, token);
        completed.StateChangedAt.Should().Be(Now.AddMinutes(20));
        completed.LastProgressAt.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public async Task Active_items_are_everything_not_yet_finished_oldest_first()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var alpha = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Alpha");
        var bravo = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Bravo");

        // One active grab per song is a database rule, so the second active item needs a song of its own.
        var charlie = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Charlie");

        var service = new QueueService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var queued = await service.AddAsync(
            NewItem(alpha.SongId, alpha.SearchRunId, alpha.CandidateId, QueueItemState.Queued), token);
        time.Advance(TimeSpan.FromMinutes(1));
        var importing = await service.AddAsync(
            NewItem(charlie.SongId, charlie.SearchRunId, charlie.CandidateId, QueueItemState.Importing), token);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.AddAsync(
            NewItem(bravo.SongId, bravo.SearchRunId, bravo.CandidateId, QueueItemState.Failed), token);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.AddAsync(
            NewItem(bravo.SongId, bravo.SearchRunId, bravo.CandidateId, QueueItemState.Cancelled), token);

        var active = await service.GetActiveAsync(token);
        active.Select(entry => entry.Id).Should().ContainInOrder(queued.Id, importing.Id);

        (await service.HasActiveForSongAsync(alpha.SongId, token)).Should().BeTrue();
        (await service.HasActiveForSongAsync(bravo.SongId, token)).Should().BeFalse();
        (await service.HasActiveForSongAsync(alpha.SongId + 999, token)).Should().BeFalse();

        // Once the import lands, the song is free to be searched again.
        importing.State = QueueItemState.Imported;
        await service.UpdateAsync(importing, token);

        (await service.GetActiveAsync(token)).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
    }

    [Fact]
    public async Task The_queue_pages_and_sorts_by_created_at_state_and_progress()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var first = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Alpha");
        // Two active states for one song would break the "one download per song" index.
        var second = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Bravo");
        var third = await SourceTestData.SeedSongWithCandidateAsync(database, time, "Charlie");

        var service = new QueueService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var queued = await service.AddAsync(
            NewItem(first.SongId, first.SearchRunId, first.CandidateId, QueueItemState.Queued, progress: 0.5), token);
        time.Advance(TimeSpan.FromMinutes(1));
        var downloading = await service.AddAsync(
            NewItem(second.SongId, second.SearchRunId, second.CandidateId, QueueItemState.Downloading, progress: 0.9), token);
        time.Advance(TimeSpan.FromMinutes(1));
        var failed = await service.AddAsync(
            NewItem(third.SongId, third.SearchRunId, third.CandidateId, QueueItemState.Failed, progress: 0.1), token);

        var newest = await service.GetPageAsync(new PagingSpec(1, 20, null, true), token);
        newest.TotalRecords.Should().Be(3);
        newest.Records.Select(entry => entry.Id).Should().ContainInOrder(failed.Id, downloading.Id, queued.Id);
        newest.Records[0].Song.Title.Should().Be("Charlie");

        var oldest = await service.GetPageAsync(new PagingSpec(1, 20, "createdAt", false), token);
        oldest.Records.Select(entry => entry.Id).Should().ContainInOrder(queued.Id, downloading.Id, failed.Id);

        var byProgress = await service.GetPageAsync(new PagingSpec(1, 20, "progress", false), token);
        byProgress.Records.Select(entry => entry.Id).Should().ContainInOrder(failed.Id, queued.Id, downloading.Id);

        var byState = await service.GetPageAsync(new PagingSpec(1, 20, "state", false), token);
        byState.Records.Select(entry => entry.State).Should().ContainInOrder(
            QueueItemState.Downloading,
            QueueItemState.Failed,
            QueueItemState.Queued);

        var page = await service.GetPageAsync(new PagingSpec(1, 2, null, true), token);
        page.TotalRecords.Should().Be(3);
        page.Records.Should().HaveCount(2);
    }

    private static QueueItem NewItem(
        long songId,
        long searchRunId,
        long candidateId,
        QueueItemState state = QueueItemState.Queued,
        double progress = 0) => new()
        {
            SongId = songId,
            SearchRunId = searchRunId,
            CandidateId = candidateId,
            SourceType = SourceTypes.Soulseek,
            Destination = "wondarr/17",
            State = state,
            Progress = progress,
        };
}
