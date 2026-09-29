using System.Text.Json;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The provider's contract: it refuses to search when the source is not usable, it walks the query
/// sequence without ever bypassing the runner's budget, and it translates slskd's transfer states into
/// the source-independent ones.
/// </summary>
public class SoulseekSourceProviderTests
{
    private const string FirstQuery = "daft punk get lucky";
    private const string SecondQuery = "get lucky";
    private const string ThirdQuery = "daft punk random access memories";

    private const string AudioPath = @"@@peer\Music\Daft Punk\Random Access Memories\08 - Get Lucky.flac";

    private static readonly SlskdStatusSnapshot Running =
        new(SlskdState.Running, IsReachable: true, IsLoggedIn: true);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task External_mode_is_unavailable()
    {
        var (available, reason) = await Provider(snapshot: Running, options: Options(SoulseekMode.External))
            .GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
        reason.Should().Be("External slskd mode arrives in Phase 5");
    }

    [Fact]
    public async Task Missing_credentials_are_unavailable()
    {
        var options = Options();
        options.Username = null;
        options.Password = null;

        var (available, reason) = await Provider(snapshot: Running, options: options)
            .GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
        reason.Should().Be("Soulseek is not configured");
    }

    [Theory]
    [InlineData(SlskdState.Starting, true, true)]
    [InlineData(SlskdState.Crashed, false, true)]
    [InlineData(SlskdState.Running, false, true)]
    public async Task A_slskd_that_is_not_running_is_unavailable(SlskdState state, bool reachable, bool loggedIn)
    {
        var (available, reason) = await Provider(snapshot: new SlskdStatusSnapshot(state, IsReachable: reachable, IsLoggedIn: loggedIn))
            .GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
        reason.Should().Be("slskd is not running");
    }

    [Fact]
    public async Task A_logged_out_slskd_is_unavailable()
    {
        var (available, reason) = await Provider(snapshot: new SlskdStatusSnapshot(SlskdState.Running, IsReachable: true))
            .GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeFalse();
        reason.Should().Be("Soulseek: not logged in");
    }

    [Fact]
    public async Task A_running_logged_in_slskd_is_available()
    {
        var (available, reason) = await Provider(snapshot: Running).GetAvailabilityAsync(CancellationToken.None);

        available.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task An_unavailable_source_submits_no_search()
    {
        var runner = new FakeRunner();

        var result = await Provider(runner, snapshot: SlskdStatusSnapshot.NotConfigured)
            .SearchAsync(Request(), CancellationToken.None);

        runner.Queries.Should().BeEmpty();
        result.Queries.Should().BeEmpty();
        result.Candidates.Should().BeEmpty();
        result.Message.Should().Be("slskd is not running");
    }

    [Fact]
    public async Task Runs_every_query_in_order()
    {
        var runner = new FakeRunner(_ => []);

        var result = await Provider(runner).SearchAsync(Request(), CancellationToken.None);

        runner.Queries.Should().Equal(FirstQuery, SecondQuery, ThirdQuery);
        result.Queries.Should().Equal(FirstQuery, SecondQuery, ThirdQuery);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Stops_the_sequence_when_the_pool_is_good_enough()
    {
        var runner = new FakeRunner(_ => [Response("peer1", AudioPath)]);

        var result = await Provider(runner)
            .SearchAsync(Request(pool => pool.Count > 0), CancellationToken.None);

        runner.Queries.Should().Equal(FirstQuery);
        result.Queries.Should().Equal(FirstQuery);
        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Merges_the_candidates_of_every_query_by_blocklist_key()
    {
        var runner = new FakeRunner(_ => [Response("peer1", AudioPath), Response("peer2", AudioPath)]);

        var result = await Provider(runner).SearchAsync(Request(), CancellationToken.None);

        runner.Queries.Should().HaveCount(3);
        result.Candidates.Should().HaveCount(2);
        result.Candidates.Select(candidate => candidate.Provider).Should().Equal("peer1", "peer2");
    }

    [Fact]
    public async Task A_failing_query_does_not_stop_the_next_one()
    {
        var runner = new FakeRunner(query => query == FirstQuery
            ? throw new InvalidOperationException("slskd blew up")
            : [Response("peer1", AudioPath)]);

        var result = await Provider(runner).SearchAsync(Request(), CancellationToken.None);

        runner.Queries.Should().Equal(FirstQuery, SecondQuery, ThirdQuery);
        result.Queries.Should().Equal(SecondQuery, ThirdQuery);
        result.Candidates.Should().NotBeEmpty();
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Reports_the_first_error_when_every_query_fails()
    {
        var runner = new FakeRunner(query => throw new InvalidOperationException($"no answer to {query}"));

        var result = await Provider(runner).SearchAsync(Request(), CancellationToken.None);

        result.Queries.Should().BeEmpty();
        result.Message.Should().Be($"Soulseek search failed: no answer to {FirstQuery}");
    }

    [Fact]
    public async Task A_slskd_rejection_stops_the_sequence_and_keeps_what_was_found()
    {
        var runner = new FakeRunner(query => query == ThirdQuery
            ? throw new SlskdSearchRejectedException()
            : [Response("peer1", AudioPath)]);

        var result = await Provider(runner).SearchAsync(Request(), CancellationToken.None);

        result.Queries.Should().Equal(FirstQuery, SecondQuery);
        result.Candidates.Should().ContainSingle();
        result.Message.Should().Be("slskd refused a search (too many in flight)");
    }

    [Fact]
    public async Task Propagates_a_cancelled_search()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var act = async () => await Provider(runner).SearchAsync(Request(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Grabs_a_candidate_into_a_handle_that_reads_back()
    {
        var downloads = new FakeDownloads
        {
            Status = new SlskdDownloadStatus(SlskdTransferState.Completed | SlskdTransferState.Succeeded, 100, 1234, 1234, null, null, "/data/downloads/wondarr/42/x.flac"),
        };

        var provider = Provider(downloads: downloads);
        var handle = await provider.GrabAsync(Candidate(AudioPath), "wondarr/42", CancellationToken.None);

        handle.SourceType.Should().Be(SourceTypes.Soulseek);
        handle.Value.Should().Contain("\"remoteFilename\"");

        downloads.Enqueues.Should().ContainSingle();
        downloads.Enqueues[0].ExternalId.Should().Be("wondarr/42");

        var grab = JsonSerializer.Deserialize<SlskdGrab>(handle.Value, SerializerOptions);
        grab.Should().NotBeNull();
        grab!.Username.Should().Be("peer1");
        grab.RemoteFilename.Should().Be(AudioPath);
        grab.Destination.Should().Be("wondarr/42");

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(DownloadState.Completed);
        status.CompletedPath.Should().Be("/data/downloads/wondarr/42/x.flac");
        status.Progress.Should().Be(1);
    }

    [Fact]
    public async Task Swallows_a_cancel_that_slskd_has_already_forgotten()
    {
        var downloads = new FakeDownloads { CancelFailure = new SlskdEnqueueException(System.Net.HttpStatusCode.OK, "gone") };
        var provider = Provider(downloads: downloads);
        var handle = await provider.GrabAsync(Candidate(AudioPath), "wondarr/42", CancellationToken.None);

        var act = async () => await provider.CancelAsync(handle, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    public static TheoryData<SlskdTransferState, string?, string?, DownloadState, string?, int?> States => new()
    {
        { SlskdTransferState.Requested, null, null, DownloadState.Queued, null, null },
        { SlskdTransferState.Initializing, null, null, DownloadState.Queued, null, null },
        { SlskdTransferState.Queued | SlskdTransferState.Locally, null, null, DownloadState.Queued, null, null },
        { SlskdTransferState.Queued | SlskdTransferState.Remotely, null, null, DownloadState.RemotelyQueued, null, 7 },
        { SlskdTransferState.InProgress, null, null, DownloadState.Downloading, null, null },
        { SlskdTransferState.Completed | SlskdTransferState.Succeeded, "/data/downloads/x.flac", null, DownloadState.Completed, null, null },
        { SlskdTransferState.Completed | SlskdTransferState.Succeeded, null, "Completed file not found in /data/downloads", DownloadState.Failed, "Completed file not found in /data/downloads", null },
        { SlskdTransferState.Completed | SlskdTransferState.Cancelled, null, null, DownloadState.Cancelled, null, null },
        { SlskdTransferState.Completed | SlskdTransferState.Errored, null, "the peer went away", DownloadState.Failed, "Soulseek transfer errored: the peer went away", null },
        { SlskdTransferState.Completed | SlskdTransferState.Rejected, null, "denied", DownloadState.Failed, "Soulseek transfer rejected: denied", null },
        { SlskdTransferState.Completed | SlskdTransferState.TimedOut, null, null, DownloadState.Failed, "Soulseek transfer timed out", null },
        { SlskdTransferState.Completed | SlskdTransferState.Aborted, null, "aborted by the peer", DownloadState.Failed, "Soulseek transfer aborted: aborted by the peer", null },
    };

    [Theory]
    [MemberData(nameof(States))]
    public async Task Maps_every_transfer_state(
        SlskdTransferState state,
        string? localPath,
        string? error,
        DownloadState expected,
        string? message,
        int? placeInQueue)
    {
        var downloads = new FakeDownloads
        {
            // The queue position is always there: only a remotely queued transfer may report it.
            Status = new SlskdDownloadStatus(state, 42.5, 1024, 4096, 7, error, localPath),
        };

        var provider = Provider(downloads: downloads);
        var handle = await provider.GrabAsync(Candidate(AudioPath), "wondarr/42", CancellationToken.None);

        var status = await provider.GetStatusAsync(handle, CancellationToken.None);

        status.State.Should().Be(expected);
        status.Message.Should().Be(message);
        status.PlaceInQueue.Should().Be(placeInQueue);
        status.BytesTransferred.Should().Be(1024);
        status.SizeBytes.Should().Be(4096);
        status.CompletedPath.Should().Be(localPath);
        status.Progress.Should().Be(expected == DownloadState.Completed ? 1 : 0.425);
    }

    private static SoulseekSourceProvider Provider(
        FakeRunner? runner = null,
        FakeDownloads? downloads = null,
        SlskdStatusSnapshot? snapshot = null,
        SoulseekOptions? options = null)
    {
        var status = new SlskdStatus();
        status.Set(snapshot ?? Running);

        return new SoulseekSourceProvider(
            runner ?? new FakeRunner(),
            downloads ?? new FakeDownloads(),
            status,
            SlskdTestData.Monitor(options ?? Options()),
            NullLogger<SoulseekSourceProvider>.Instance);
    }

    private static SoulseekOptions Options(SoulseekMode mode = SoulseekMode.Bundled) => new()
    {
        Mode = mode,
        Username = "wondarr",
        Password = "test-password",
    };

    private static SongSearchRequest Request(Func<IReadOnlyList<Candidate>, bool>? pool = null) =>
        new(1, "Get Lucky", "Daft Punk", ["Daft Punk"], "Random Access Memories", 249_000, VersionFlags.None)
        {
            IsPoolGoodEnough = pool,
        };

    private static Candidate Candidate(string remotePath) => new()
    {
        SourceType = SourceTypes.Soulseek,
        BlocklistKey = BlocklistKeys.Soulseek("peer1", remotePath),
        DisplayName = "08 - Get Lucky.flac",
        RemotePath = remotePath,
        Provider = "peer1",
        SizeBytes = 1234,
    };

    private static SlskdSearchResponse Response(string username, string filename) => new()
    {
        Username = username,
        HasFreeUploadSlot = true,
        Files = [new SlskdSearchFile { Filename = filename, Size = 1234, Length = 249 }],
    };

    private sealed class FakeRunner : ISlskdSearchRunner
    {
        private readonly Func<string, IReadOnlyList<SlskdSearchResponse>> _responses;

        public FakeRunner(Func<string, IReadOnlyList<SlskdSearchResponse>>? responses = null) =>
            _responses = responses ?? (_ => []);

        public List<string> Queries { get; } = [];

        public Task<SlskdSearchResult> RunAsync(string searchText, CancellationToken cancellationToken)
        {
            Queries.Add(searchText);

            return Task.FromResult(new SlskdSearchResult(
                searchText,
                _responses(searchText),
                "Completed",
                StoppedByWallClock: false,
                TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeDownloads : ISlskdDownloads
    {
        public SlskdDownloadStatus Status { get; set; } =
            new(SlskdTransferState.Requested, 0, 0, 0, null, null, null);

        public List<(string Destination, string ExternalId)> Enqueues { get; } = [];

        public Exception? CancelFailure { get; set; }

        public Task<SlskdGrab> EnqueueAsync(
            string username,
            string remoteFilename,
            long size,
            string destination,
            string externalId,
            CancellationToken cancellationToken)
        {
            Enqueues.Add((destination, externalId));

            return Task.FromResult(new SlskdGrab(username, Guid.NewGuid(), remoteFilename, destination));
        }

        public Task<SlskdDownloadStatus> GetStatusAsync(SlskdGrab grab, CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task CancelAsync(SlskdGrab grab, CancellationToken cancellationToken)
        {
            if (CancelFailure is not null)
            {
                throw CancelFailure;
            }

            return Task.CompletedTask;
        }
    }
}