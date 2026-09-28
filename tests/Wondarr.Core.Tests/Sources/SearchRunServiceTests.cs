using Wondarr.Core.Domain;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Sources;

public sealed class SearchRunServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_run_records_its_candidates_and_closes_with_a_count()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var songId = await SourceTestData.SeedSongAsync(database, time);

        var service = new SearchRunService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var run = await service.StartAsync(songId, SearchTrigger.Manual, token);
        run.StartedAt.Should().Be(Now);
        run.Outcome.Should().BeNull();
        run.FinishedAt.Should().BeNull();

        var candidates = new List<CandidateRecord>
        {
            Candidate(songId, "one.flac", score: 400, accepted: false),
            Candidate(songId, "two.flac", score: 900, accepted: true),
            Candidate(songId, "three.flac", score: 650, accepted: true),
        };

        await service.AddCandidatesAsync(run.Id, candidates, token);
        candidates.Should().OnlyContain(candidate => candidate.SearchRunId == run.Id);

        time.Advance(TimeSpan.FromSeconds(30));
        await service.FinishAsync(
            run.Id,
            SearchOutcome.Grabbed,
            ["soulseek"],
            ["aphex twin - alpha"],
            "picked the flac",
            token);

        var stored = await service.GetLatestAsync(songId, token);
        stored.Should().NotBeNull();
        stored!.Id.Should().Be(run.Id);
        stored.SongId.Should().Be(songId);
        stored.Trigger.Should().Be(SearchTrigger.Manual);
        stored.FinishedAt.Should().Be(Now.AddSeconds(30));
        stored.Outcome.Should().Be(SearchOutcome.Grabbed);
        stored.CandidateCount.Should().Be(3);
        stored.Sources.Should().Equal("soulseek");
        stored.Queries.Should().Equal("aphex twin - alpha");
        stored.Message.Should().Be("picked the flac");

        // The JSON columns hold their CLR shape back, and the instants stay UTC.
        stored.StartedAt.Kind.Should().Be(DateTimeKind.Utc);
        stored.FinishedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        var found = await service.GetCandidatesAsync(run.Id, token);
        found.Select(candidate => candidate.Score).Should().ContainInOrder(900, 650, 400);
        found[0].Accepted.Should().BeTrue();
        found[0].DisplayName.Should().Be("two.flac");
        found[0].Normalised.Should().Be("{}");
        found[0].ScoreBreakdown.Should().Be("{}");
        found[0].Rejections.Should().Be("[]");
        found.Should().OnlyContain(candidate => candidate.SearchRunId == run.Id);
    }

    [Fact]
    public async Task Recent_starts_come_back_newest_first_for_the_backoff()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var songId = await SourceTestData.SeedSongAsync(database, time);

        var service = new SearchRunService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var first = await service.StartAsync(songId, SearchTrigger.Automatic, token);
        await service.FinishAsync(first.Id, SearchOutcome.NoResults, [], [], null, token);

        time.Advance(TimeSpan.FromHours(6));
        await service.StartAsync(songId, SearchTrigger.Automatic, token);

        time.Advance(TimeSpan.FromHours(6));
        var third = await service.StartAsync(songId, SearchTrigger.Upgrade, token);

        var starts = await service.GetRecentStartsAsync(songId, 2, token);
        starts.Should().HaveCount(2);
        starts.Should().ContainInOrder(Now.AddHours(12), Now.AddHours(6));
        starts.Should().OnlyContain(start => start.Kind == DateTimeKind.Utc);

        (await service.GetRecentStartsAsync(songId, 0, token)).Should().BeEmpty();

        var latest = await service.GetLatestAsync(songId, token);
        latest!.Id.Should().Be(third.Id);
        latest.Sources.Should().BeEmpty();
        latest.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_song_takes_its_runs_candidates_and_queue_items()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var (songId, runId, candidateId) = await SourceTestData.SeedSongWithCandidateAsync(database, time);

        var queue = new QueueService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        await queue.AddAsync(
            new QueueItem
            {
                SongId = songId,
                SearchRunId = runId,
                CandidateId = candidateId,
                SourceType = SourceTypes.Soulseek,
                Destination = "wondarr/17",
                State = QueueItemState.Downloading,
            },
            token);

        await using (var delete = database.CreateContext(time))
        {
            await delete.Songs.Where(song => song.Id == songId).ExecuteDeleteAsync(token);
        }

        await using var reread = database.CreateContext(time);
        (await reread.SearchRuns.CountAsync(token)).Should().Be(0);
        (await reread.Candidates.CountAsync(token)).Should().Be(0);
        (await reread.QueueItems.CountAsync(token)).Should().Be(0);
    }

    private static CandidateRecord Candidate(long songId, string fileName, int score, bool accepted) => new()
    {
        SongId = songId,
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = BlocklistKeys.Soulseek("peer", $"Music\\{fileName}"),
        DisplayName = fileName,
        RemotePath = $"Music\\{fileName}",
        Provider = "peer",
        Score = score,
        Accepted = accepted,
    };
}