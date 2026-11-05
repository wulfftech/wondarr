using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Importing;
using Wondarr.Core.Media;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;
using Wondarr.Core.Verification;
using Xunit;

namespace Wondarr.Core.Tests.Importing;

/// <summary>
/// A run is marked <c>Grabbed</c> when it hands a candidate to a client, so a run whose every grab then
/// failed or was rejected must be recorded as fruitless when its last attempt ends: the song's backoff
/// counts it as a failed search instead of resetting to the first step (MATCHING_ENGINE §6.6).
/// </summary>
public sealed class FruitlessRunTests
{
    private const string DifferentRecording = "Fingerprint belongs to a different recording";

    [Fact]
    public async Task A_run_whose_every_grab_the_import_rejected_is_fruitless_and_the_backoff_steps_up()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = RejectedByVerifier();
        host.Search.Next = null;

        var seed = await host.SeedAsync(options => options.Attempt = host.Options.MaxAutoAttemptsPerSearch);
        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);
        await AddEarlierFailedGrabsAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 3);
        var firstStarted = host.Time.GetUtcNow().UtcDateTime;

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        var first = await RunAsync(host.Database, host.Time, seed.SearchRunId);
        first.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        first.Message.Should().Contain("Nothing this run grabbed was imported:").And.Contain(DifferentRecording);

        var options = new SearchOptions();
        var runs = await RunsOfAsync(host.Database, host.Time, seed.SongId);
        SearchBackoff.IsEligible(runs, options, firstStarted.AddMinutes(59), _ => true).Should().BeFalse();
        SearchBackoff.IsEligible(runs, options, firstStarted.AddHours(1), _ => true).Should().BeTrue();

        // The same song, searched again two hours later: every grab rejected again.
        var secondRunId = await RestartAsync(host, seed, firstStarted.AddHours(2));
        var secondStarted = firstStarted.AddHours(2);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        (await RunAsync(host.Database, host.Time, secondRunId)).Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);

        runs = await RunsOfAsync(host.Database, host.Time, seed.SongId);
        SearchBackoff.IsEligible(runs, options, secondStarted.AddHours(1), _ => true).Should().BeFalse("two fruitless runs wait 6 h");
        SearchBackoff.IsEligible(runs, options, secondStarted.AddHours(6), _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_run_whose_last_attempt_is_a_missing_download_after_rejections_is_fruitless()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Search.Next = null;

        var seed = await host.SeedAsync(options =>
        {
            options.Attempt = host.Options.MaxAutoAttemptsPerSearch;
            options.CreateDownload = false;
        });

        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);
        await AddEarlierFailedGrabsAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 3);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        var run = await RunAsync(host.Database, host.Time, seed.SearchRunId);
        run.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        run.Message.Should().Contain("Nothing this run grabbed was imported: Downloaded file is missing");
    }

    [Fact]
    public async Task A_run_with_another_grab_on_its_way_stays_a_grab_when_one_is_rejected()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = RejectedByVerifier();
        host.Search.Next = null;

        var seed = await host.SeedAsync(options => options.Attempt = 2);
        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);

        // A song may have one grab in flight, and the rejected item is saved as failed before the next
        // attempt is asked for: a grab the search queued in that window is in flight when the run is judged.
        host.Search.OnGrabAsync = () => AddGrabAsync(
            host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 3, QueueItemState.Downloading);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        (await RunAsync(host.Database, host.Time, seed.SearchRunId)).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_run_where_an_earlier_grab_imported_stays_a_grab_when_the_last_is_rejected()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = RejectedByVerifier();
        host.Search.Next = null;

        var seed = await host.SeedAsync(options => options.Attempt = 2);
        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);
        await AddGrabAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 1, QueueItemState.Imported);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        (await RunAsync(host.Database, host.Time, seed.SearchRunId)).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_bundled_partners_rejection_leaves_the_other_songs_run_a_grab()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Verifier.Result = RejectedByVerifier();
        host.Search.Next = null;

        var seed = await host.SeedAsync(options => options.Attempt = host.Options.MaxAutoAttemptsPerSearch);
        var runSong = await host.SeedOtherSongAsync("6b6b6b6b-0000-4000-8000-000000000001");

        // The item is the partner's: it sits on the run of another song.
        await using (var context = host.Database.CreateContext(host.Time))
        {
            var run = await context.SearchRuns.SingleAsync(entry => entry.Id == seed.SearchRunId);
            run.SongId = runSong;
            run.Outcome = SearchOutcome.Grabbed;
            run.Message = "Grabbed candidate.";
            await context.SaveChangesAsync();
        }

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Rejected);

        var after = await RunAsync(host.Database, host.Time, seed.SearchRunId);
        after.Outcome.Should().Be(SearchOutcome.Grabbed);
        after.Message.Should().Be("Grabbed candidate.");
    }

    [Fact]
    public async Task A_failure_after_the_file_was_right_with_nothing_imported_makes_the_run_fruitless()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Transcoder.Failure = new TranscodeException("ffmpeg exited 1: nope");
        host.Search.Next = null;

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
            options.Attempt = host.Options.MaxAutoAttemptsPerSearch;
        });

        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        // No further attempt follows a failure after the file passed, so the run brought the song nothing.
        var run = await RunAsync(host.Database, host.Time, seed.SearchRunId);
        run.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        run.Message.Should().Contain("Nothing this run grabbed was imported: Transcode failed");
    }

    [Fact]
    public async Task A_failure_after_the_file_was_right_leaves_a_run_with_an_imported_grab_a_grab()
    {
        await using var host = await ImportTestHost.CreateAsync();
        host.Transcoder.Failure = new TranscodeException("ffmpeg exited 1: nope");

        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.YouTube;
            options.DownloadName = "dQw4w9WgXcQ.opus";
            options.RemotePath = "dQw4w9WgXcQ";
            options.Attempt = 2;
        });

        await SetGrabbedAsync(host.Database, host.Time, seed.SearchRunId);
        await AddGrabAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 1, QueueItemState.Imported);

        (await host.FreshImport().ImportAsync(seed.QueueItemId, CancellationToken.None)).Should().Be(ImportOutcome.Failed);

        (await RunAsync(host.Database, host.Time, seed.SearchRunId)).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_plain_transfer_failure_after_three_rejections_makes_the_run_fruitless()
    {
        await using var host = await QueueTestHost.CreateAsync();
        host.SearchService.Next = null;
        var seed = await host.SeedAsync(options => options.Attempt = host.Search.MaxAutoAttemptsPerSearch);
        await host.SetRunOutcomeAsync(seed.SearchRunId, SearchOutcome.Grabbed);
        await AddEarlierFailedGrabsAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 3);
        host.Source.Status = new DownloadStatus(DownloadState.Failed, 0, 0, Message: "Peer went offline");

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        (await host.BlocklistAsync()).Should().BeEmpty();

        var run = await host.SearchRunAsync(seed.SearchRunId);
        run.Outcome.Should().Be(SearchOutcome.NoAcceptableCandidate);
        run.Message.Should().Contain("Nothing this run grabbed was imported: Peer went offline");
    }

    [Fact]
    public async Task A_plain_transfer_failure_leaves_a_run_with_an_imported_grab_a_grab()
    {
        await using var host = await QueueTestHost.CreateAsync();
        host.SearchService.Next = null;
        var seed = await host.SeedAsync(options => options.Attempt = 2);
        await host.SetRunOutcomeAsync(seed.SearchRunId, SearchOutcome.Grabbed);
        await AddGrabAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 1, QueueItemState.Imported);
        host.Source.Status = new DownloadStatus(DownloadState.Failed, 0, 0, Message: "Peer went offline");

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        (await host.SearchRunAsync(seed.SearchRunId)).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    [Fact]
    public async Task A_blocklisted_tracker_failure_leaves_a_run_with_an_imported_grab_a_grab()
    {
        await using var host = await QueueTestHost.CreateAsync();
        host.SearchService.Next = null;
        host.Source.SourceType = SourceTypes.Newznab;
        var seed = await host.SeedAsync(options =>
        {
            options.SourceType = SourceTypes.Newznab;
            options.Attempt = 2;
        });

        await host.SetRunOutcomeAsync(seed.SearchRunId, SearchOutcome.Grabbed);
        await AddGrabAsync(host.Database, host.Time, seed.SongId, seed.SearchRunId, seed.CandidateId, 1, QueueItemState.Imported);
        host.Source.Status = new DownloadStatus(
            DownloadState.Failed, 0, 0, Message: "The song is not in the downloaded post.", BlocklistCandidate: true);

        await host.Tracker.RunCycleAsync(CancellationToken.None);

        (await host.BlocklistAsync()).Should().ContainSingle();
        (await host.SearchRunAsync(seed.SearchRunId)).Outcome.Should().Be(SearchOutcome.Grabbed);
    }

    private static VerificationResult RejectedByVerifier() =>
        new(
            VerificationOutcome.Failed,
            DifferentRecording,
            FakeDownloadVerifier.Flac(),
            36,
            null,
            null,
            "someone-else",
            null,
            false);

    private static async Task SetGrabbedAsync(SqliteTestDatabase database, FakeTimeProvider time, long searchRunId)
    {
        await using var context = database.CreateContext(time);
        var run = await context.SearchRuns.SingleAsync(entry => entry.Id == searchRunId);
        run.Outcome = SearchOutcome.Grabbed;
        run.Message = "Grabbed candidate.";
        await context.SaveChangesAsync();
    }

    private static async Task<SearchRun> RunAsync(SqliteTestDatabase database, FakeTimeProvider time, long searchRunId)
    {
        await using var context = database.CreateContext(time);

        return await context.SearchRuns.AsNoTracking().SingleAsync(entry => entry.Id == searchRunId);
    }

    private static async Task<List<SearchRun>> RunsOfAsync(SqliteTestDatabase database, FakeTimeProvider time, long songId)
    {
        await using var context = database.CreateContext(time);

        return await context.SearchRuns.AsNoTracking().Where(entry => entry.SongId == songId).ToListAsync();
    }

    private static async Task AddEarlierFailedGrabsAsync(
        SqliteTestDatabase database,
        FakeTimeProvider time,
        long songId,
        long searchRunId,
        long candidateId,
        int count)
    {
        for (var attempt = 1; attempt <= count; attempt++)
        {
            await AddGrabAsync(database, time, songId, searchRunId, candidateId, attempt, QueueItemState.Failed);
        }
    }

    private static async Task AddGrabAsync(
        SqliteTestDatabase database,
        FakeTimeProvider time,
        long songId,
        long searchRunId,
        long candidateId,
        int attempt,
        QueueItemState state)
    {
        await using var context = database.CreateContext(time);

        context.QueueItems.Add(new QueueItem
        {
            SongId = songId,
            CandidateId = candidateId,
            SearchRunId = searchRunId,
            SourceType = SourceTypes.Soulseek,
            Destination = "wondarr/earlier-" + attempt,
            State = state,
            Attempt = attempt,
            SizeBytes = 30_000_000,
        });

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Starts the song's next search run at <paramref name="startedAt"/> and puts the seeded item back
    /// in it as a finished download whose three earlier grabs failed, as the first run looked.
    /// </summary>
    private static async Task<long> RestartAsync(ImportTestHost host, ImportSeed seed, DateTime startedAt)
    {
        long runId;

        await using (var context = host.Database.CreateContext(host.Time))
        {
            var run = new SearchRun
            {
                SongId = seed.SongId,
                Trigger = SearchTrigger.Automatic,
                StartedAt = startedAt,
                Outcome = SearchOutcome.Grabbed,
                Message = "Grabbed candidate.",
            };

            context.SearchRuns.Add(run);
            await context.SaveChangesAsync();
            runId = run.Id;

            var item = await context.QueueItems.SingleAsync(entry => entry.Id == seed.QueueItemId);
            item.SearchRunId = runId;
            item.State = QueueItemState.Completed;
            item.Message = null;
            item.FinishedAt = null;

            context.Blocklist.RemoveRange(await context.Blocklist.ToListAsync());
            await context.SaveChangesAsync();
        }

        Directory.CreateDirectory(seed.DownloadDirectory);
        await File.WriteAllBytesAsync(seed.DownloadPath, new byte[64]);
        await AddEarlierFailedGrabsAsync(host.Database, host.Time, seed.SongId, runId, seed.CandidateId, 3);

        return runId;
    }
}
