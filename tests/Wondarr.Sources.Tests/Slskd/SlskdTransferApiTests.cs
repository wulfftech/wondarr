using System.Net;
using System.Text.Json;
using Wondarr.Core.Logging;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The transfer endpoints. The shapes are inline JSON following
/// <c>docs/research/research_soulseek.md</c> §"Verified 2026-09-29"; every request carries the API
/// key and nothing else identifies the caller.
/// </summary>
public class SlskdTransferApiTests
{
    private static readonly Guid TransferId = Guid.Parse("2f1c4a44-1de0-4a54-8a2a-6f1d2f4a6b0c");
    private static readonly Guid BatchId = Guid.Parse("9d0f5b6a-3c21-4d8e-9f10-2b3c4d5e6f70");

    [Fact]
    public async Task Posts_a_batch_with_the_destination_and_the_external_id()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, "{}");

        var request = new SlskdEnqueueBatchRequest(
            "DJ Snake",
            [new SlskdEnqueueFile(@"Music\Singles\Get Lucky.mp3", 8_345_678)],
            new SlskdBatchOptions("wondarr/17", "17"),
            BatchId);

        var result = await Api(handler).EnqueueAsync(request, CancellationToken.None);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be("http://127.0.0.1:5030/api/v0/transfers/downloads/batches");
        sent.Headers.GetValues(SlskdClient.ApiKeyHeader)
            .Should().ContainSingle().Which.Should().Be(SlskdTestData.Secrets.ApiKey);

        using var body = JsonDocument.Parse(handler.Bodies.Should().ContainSingle().Subject);
        var json = body.RootElement;
        json.GetProperty("username").GetString().Should().Be("DJ Snake");
        json.GetProperty("id").GetGuid().Should().Be(BatchId);

        var file = json.GetProperty("files").EnumerateArray().Should().ContainSingle().Subject;
        file.GetProperty("filename").GetString().Should().Be(@"Music\Singles\Get Lucky.mp3");
        file.GetProperty("size").GetInt64().Should().Be(8_345_678);

        var options = json.GetProperty("options");
        options.GetProperty("destination").GetString().Should().Be("wondarr/17");
        options.GetProperty("externalId").GetString().Should().Be("17");

        result.Batch.Should().BeNull();
        result.Failures.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.MultiStatus)]
    public async Task Reads_the_batch_out_of_a_201_and_a_207(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(
            status,
            $"{{\"batch\":{{\"id\":\"{BatchId:D}\",\"username\":\"DJ Snake\"}},\"failures\":[]}}");

        var result = await Api(handler).EnqueueAsync(Request(), CancellationToken.None);

        result.Batch.Should().NotBeNull();
        result.Batch!.Id.Should().Be(BatchId);

        // The rest of the batch shape was not verified, so it is kept rather than modelled.
        result.Batch.Additional.Should().ContainKey("username");
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_the_failures_out_of_a_200_where_everything_failed()
    {
        var handler = StubHttpMessageHandler.Ok(
            """
            {
              "batch": null,
              "failures": [
                { "filename": "Music\\Singles\\Get Lucky.mp3", "message": "File is not shared" }
              ]
            }
            """);

        var result = await Api(handler).EnqueueAsync(Request(), CancellationToken.None);

        result.Batch.Should().BeNull();
        result.Failures.Should().ContainSingle()
            .Which.Should().Be(new SlskdEnqueueFailure(@"Music\Singles\Get Lucky.mp3", "File is not shared"));
    }

    [Fact]
    public async Task Quotes_slskds_reason_for_a_400_and_a_409()
    {
        var badRequest = new StubHttpMessageHandler(HttpStatusCode.BadRequest, "\"One or more files are invalid\"");
        var conflict = new StubHttpMessageHandler(
            HttpStatusCode.Conflict,
            "\"Another enqueue for this user is in progress\"");

        var rejected = () => Api(badRequest).EnqueueAsync(Request(), CancellationToken.None);
        var busy = () => Api(conflict).EnqueueAsync(Request(), CancellationToken.None);

        (await rejected.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.Message.Should().Contain("One or more files are invalid");

        (await busy.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await busy.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.Message.Should().Contain("Another enqueue for this user is in progress");
    }

    [Fact]
    public async Task Reports_rate_limiting_for_a_429()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.TooManyRequests);

        var act = () => Api(handler).EnqueueAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.Message.Should().Be("slskd refused the enqueue (TooManyRequests): slskd is rate limiting enqueues");
    }

    [Fact]
    public async Task Escapes_the_username_in_the_transfer_path()
    {
        var handler = StubHttpMessageHandler.Ok(TransferJson("Queued, Remotely"));

        await Api(handler).GetAsync("DJ Snake/Mix", TransferId, CancellationToken.None);

        // The slash has to stay escaped, or a peer's name would add a path segment of its own.
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri
            .Should().Be($"http://127.0.0.1:5030/api/v0/transfers/downloads/DJ%20Snake%2FMix/{TransferId:D}");
    }

    [Fact]
    public async Task Reads_a_transfer_with_its_state_bytes_and_size()
    {
        var handler = StubHttpMessageHandler.Ok(TransferJson("Completed, Succeeded"));

        var transfer = await Api(handler).GetAsync("DJ Snake", TransferId, CancellationToken.None);

        transfer.Should().NotBeNull();
        transfer!.Id.Should().Be(TransferId);
        transfer.BatchId.Should().Be(BatchId);
        transfer.Username.Should().Be("DJ Snake");
        transfer.Direction.Should().Be("Download");
        transfer.Filename.Should().Be(@"Music\Singles\Get Lucky.mp3");
        transfer.Size.Should().Be(8_345_678);
        transfer.State.Should().Be("Completed, Succeeded");
        transfer.BytesTransferred.Should().Be(8_345_678);
        transfer.AverageSpeed.Should().Be(1048576.5);
        transfer.PercentComplete.Should().Be(100);
        transfer.PlaceInQueue.Should().Be(12);
        transfer.Exception.Should().BeNull();
        transfer.Attempts.Should().Be(0);
        transfer.RequestedAt.Should().Be(new DateTime(2026, 9, 29, 9, 1, 2, DateTimeKind.Utc));
        transfer.EnqueuedAt.Should().Be(new DateTime(2026, 9, 29, 9, 1, 3, DateTimeKind.Utc));
        transfer.StartedAt.Should().Be(new DateTime(2026, 9, 29, 9, 1, 4, DateTimeKind.Utc));
        transfer.EndedAt.Should().Be(new DateTime(2026, 9, 29, 9, 2, 4, DateTimeKind.Utc));
        transfer.Removed.Should().BeFalse();
    }

    [Fact]
    public async Task Answers_null_for_a_transfer_slskd_no_longer_has()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.NotFound);

        var transfer = await Api(handler).GetAsync("DJ Snake", TransferId, CancellationToken.None);

        transfer.Should().BeNull();
    }

    [Fact]
    public async Task Lists_the_recorded_empty_downloads_as_an_empty_list()
    {
        var handler = StubHttpMessageHandler.Ok(SlskdTestData.ReadFixture("live/downloads-empty.json"));

        var users = await Api(handler).ListAsync(CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri
            .Should().Be("http://127.0.0.1:5030/api/v0/transfers/downloads");
        users.Should().BeEmpty();
    }

    [Fact]
    public async Task Lists_a_users_transfers_grouped_by_remote_directory()
    {
        var handler = StubHttpMessageHandler.Ok(
            $$"""
            [
              {
                "username": "DJ Snake",
                "directories": [
                  {
                    "directory": "Music\\Singles",
                    "fileCount": 1,
                    "files": [ {{TransferJson("InProgress")}} ]
                  }
                ]
              }
            ]
            """);

        var users = await Api(handler).ListAsync(CancellationToken.None);

        var user = users.Should().ContainSingle().Subject;
        user.Username.Should().Be("DJ Snake");

        var directory = user.Directories.Should().ContainSingle().Subject;
        directory.Directory.Should().Be(@"Music\Singles");
        directory.FileCount.Should().Be(1);
        directory.Files.Should().ContainSingle().Which.Filename.Should().Be(@"Music\Singles\Get Lucky.mp3");
    }

    [Fact]
    public async Task Reads_the_queue_position_from_a_bare_number()
    {
        var handler = StubHttpMessageHandler.Ok("7");

        var position = await Api(handler).GetPlaceInQueueAsync("DJ Snake", TransferId, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri
            .Should().Be($"http://127.0.0.1:5030/api/v0/transfers/downloads/DJ%20Snake/{TransferId:D}/position");
        position.Should().Be(7);
    }

    [Fact]
    public async Task Reads_the_queue_position_out_of_a_whole_transfer_too()
    {
        var handler = StubHttpMessageHandler.Ok(TransferJson("Queued, Remotely"));

        var position = await Api(handler).GetPlaceInQueueAsync("DJ Snake", TransferId, CancellationToken.None);

        position.Should().Be(12);
    }

    [Fact]
    public async Task Answers_null_for_a_queue_position_slskd_does_not_have()
    {
        var notFound = StubHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var empty = StubHttpMessageHandler.Ok(string.Empty);
        var nonsense = StubHttpMessageHandler.Ok("\"later\"");

        (await Api(notFound).GetPlaceInQueueAsync("DJ Snake", TransferId, CancellationToken.None)).Should().BeNull();
        (await Api(empty).GetPlaceInQueueAsync("DJ Snake", TransferId, CancellationToken.None)).Should().BeNull();
        (await Api(nonsense).GetPlaceInQueueAsync("DJ Snake", TransferId, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task Deletes_a_transfer_with_the_remove_flag(bool remove, string expected)
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.NoContent);

        await Api(handler).CancelAsync("DJ Snake", TransferId, remove, CancellationToken.None);

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Delete);
        sent.RequestUri!.AbsoluteUri
            .Should().Be($"http://127.0.0.1:5030/api/v0/transfers/downloads/DJ%20Snake/{TransferId:D}?remove={expected}");
    }

    [Fact]
    public async Task Treats_a_404_on_cancel_as_a_transfer_that_is_already_gone()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.NotFound);

        await Api(handler).CancelAsync("DJ Snake", TransferId, remove: true, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Surfaces_any_other_failure_as_an_http_request_exception_without_the_body()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "api-key 'abc' was rejected");

        var act = () => Api(handler).GetAsync("DJ Snake", TransferId, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().NotContain("api-key");
    }

    /// <summary>Every state string slskd 0.26.0 was seen to report, and what each one means.</summary>
    [Theory]
    [InlineData("Requested", SlskdTransferState.Requested)]
    [InlineData("Queued, Locally", SlskdTransferState.Queued | SlskdTransferState.Locally)]
    [InlineData("Queued, Remotely", SlskdTransferState.Queued | SlskdTransferState.Remotely)]
    [InlineData("Initializing", SlskdTransferState.Initializing)]
    [InlineData("InProgress", SlskdTransferState.InProgress)]
    [InlineData("Completed, Succeeded", SlskdTransferState.Completed | SlskdTransferState.Succeeded)]
    [InlineData("Completed, Errored", SlskdTransferState.Completed | SlskdTransferState.Errored)]
    [InlineData("Completed, Rejected", SlskdTransferState.Completed | SlskdTransferState.Rejected)]
    [InlineData("Completed, TimedOut", SlskdTransferState.Completed | SlskdTransferState.TimedOut)]
    [InlineData("Completed, Cancelled", SlskdTransferState.Completed | SlskdTransferState.Cancelled)]
    [InlineData("Completed, Aborted", SlskdTransferState.Completed | SlskdTransferState.Aborted)]
    public void Parses_a_state_string(string state, SlskdTransferState expected) =>
        SlskdTransferStates.Parse(state).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parses_nothing_out_of_an_absent_state(string? state) =>
        SlskdTransferStates.Parse(state).Should().Be(SlskdTransferState.None);

    [Fact]
    public void Ignores_a_flag_it_does_not_know()
    {
        SlskdTransferStates.Parse("Queued, Remotely, SomethingNewOnSlskdsSide")
            .Should().Be(SlskdTransferState.Queued | SlskdTransferState.Remotely);
    }

    [Fact]
    public void Keeps_the_values_soulseek_dot_net_gives_them()
    {
        // These are the protocol's own flag values, not ours to renumber.
        ((int)SlskdTransferState.Requested).Should().Be(1);
        ((int)SlskdTransferState.Queued).Should().Be(2);
        ((int)SlskdTransferState.Initializing).Should().Be(4);
        ((int)SlskdTransferState.InProgress).Should().Be(8);
        ((int)SlskdTransferState.Completed).Should().Be(16);
        ((int)SlskdTransferState.Succeeded).Should().Be(32);
        ((int)SlskdTransferState.Cancelled).Should().Be(64);
        ((int)SlskdTransferState.TimedOut).Should().Be(128);
        ((int)SlskdTransferState.Errored).Should().Be(256);
        ((int)SlskdTransferState.Rejected).Should().Be(512);
        ((int)SlskdTransferState.Aborted).Should().Be(1024);
        ((int)SlskdTransferState.Locally).Should().Be(2048);
        ((int)SlskdTransferState.Remotely).Should().Be(4096);
    }

    private static SlskdEnqueueBatchRequest Request() =>
        new(
            "DJ Snake",
            [new SlskdEnqueueFile(@"Music\Singles\Get Lucky.mp3", 8_345_678)],
            new SlskdBatchOptions("wondarr/17", "17"),
            BatchId);

    /// <summary>One transfer, in the shape slskd 0.26.0 reports it.</summary>
    private static string TransferJson(string state) =>
        $$"""
        {
          "id": "{{TransferId:D}}",
          "batchId": "{{BatchId:D}}",
          "username": "DJ Snake",
          "direction": "Download",
          "filename": "Music\\Singles\\Get Lucky.mp3",
          "size": 8345678,
          "state": "{{state}}",
          "bytesTransferred": 8345678,
          "averageSpeed": 1048576.5,
          "percentComplete": 100,
          "placeInQueue": 12,
          "attempts": 0,
          "requestedAt": "2026-09-29T09:01:02.0000000Z",
          "enqueuedAt": "2026-09-29T09:01:03.0000000Z",
          "startedAt": "2026-09-29T09:01:04.0000000Z",
          "endedAt": "2026-09-29T09:02:04.0000000Z",
          "removed": false
        }
        """;

    private static SlskdTransferApi Api(StubHttpMessageHandler handler)
    {
        var secrets = new SlskdSecretsStore(
            SlskdTestData.RepositoryWithRuntimeSecrets(),
            Substitute.For<ISecretRegistry>(),
            SlskdTestData.Monitor(new SoulseekOptions()));

        return new SlskdTransferApi(new HttpClient(handler), SlskdTestData.Monitor(new SoulseekOptions()), secrets);
    }
}