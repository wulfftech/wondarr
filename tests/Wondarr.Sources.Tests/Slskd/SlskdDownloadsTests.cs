using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Sources.Slskd;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Wondarr.Sources.Tests.Slskd;

/// <summary>
/// The service Wondarr grabs downloads through: one file per batch, one folder per grab, and the
/// finished file found on disk rather than inferred from slskd's layout.
/// <para>
/// The transfer API itself is substituted here; <see cref="SlskdTransferApiTests"/> covers the HTTP
/// it performs and the parsing of slskd's state strings.
/// </para>
/// </summary>
public class SlskdDownloadsTests : IDisposable
{
    private const string RemoteFilename = @"Music\Singles\Get Lucky.mp3";
    private const string Destination = "wondarr/17";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid BatchId = Guid.Parse("9d0f5b6a-3c21-4d8e-9f10-2b3c4d5e6f70");
    private static readonly Guid TransferId = Guid.Parse("2f1c4a44-1de0-4a54-8a2a-6f1d2f4a6b0c");

    private readonly string _downloadsDir =
        Path.Combine(Path.GetTempPath(), "wondarr-downloads", Guid.NewGuid().ToString("N"));

    public SlskdDownloadsTests() => Directory.CreateDirectory(Path.Combine(_downloadsDir, Destination));

    public void Dispose()
    {
        if (Directory.Exists(_downloadsDir))
        {
            Directory.Delete(_downloadsDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Enqueues_one_file_into_the_grabs_own_folder()
    {
        var sent = new List<SlskdEnqueueBatchRequest>();

        var api = Substitute.For<ISlskdTransferApi>();
        api.EnqueueAsync(Arg.Do<SlskdEnqueueBatchRequest>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(new SlskdEnqueueBatchResponse { Batch = new SlskdBatch { Id = BatchId } });
        api.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SlskdUserTransfers> { Transfers(RemoteFilename, older: 0, BatchId) });

        var grab = await Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 8_345_678, Destination, "17", CancellationToken.None);

        var request = sent.Should().ContainSingle().Subject;

        request.Username.Should().Be("DJ Snake");
        request.Files.Should().ContainSingle();
        request.Files[0].Filename.Should().Be(RemoteFilename);
        request.Files[0].Size.Should().Be(8_345_678);
        request.Options!.Destination.Should().Be(Destination);
        request.Options.ExternalId.Should().Be("17");
        request.Id.Should().NotBeNull();

        grab.Username.Should().Be("DJ Snake");
        grab.RemoteFilename.Should().Be(RemoteFilename);
        grab.Destination.Should().Be(Destination);
        grab.TransferId.Should().Be(TransferId);
    }

    [Fact]
    public async Task Finds_the_transfer_by_batch_and_falls_back_to_the_newest_file_of_that_name()
    {
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var api = Substitute.For<ISlskdTransferApi>();
        api.EnqueueAsync(Arg.Any<SlskdEnqueueBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlskdEnqueueBatchResponse { Batch = new SlskdBatch { Id = BatchId } });

        // slskd did not record the batch id, and the same file was grabbed before.
        api.ListAsync(Arg.Any<CancellationToken>()).Returns(
            new List<SlskdUserTransfers>
            {
                Transfers(RemoteFilename, older: -1, batchId: null, id: other),
                Transfers(RemoteFilename, older: 1, batchId: null, id: TransferId),
            });

        var grab = await Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 8_345_678, Destination, "17", CancellationToken.None);

        grab.TransferId.Should().Be(TransferId);
    }

    [Fact]
    public async Task Reports_an_enqueue_slskd_refused_for_this_file()
    {
        var api = Substitute.For<ISlskdTransferApi>();
        api.EnqueueAsync(Arg.Any<SlskdEnqueueBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlskdEnqueueBatchResponse
            {
                Failures = [new SlskdEnqueueFailure(RemoteFilename, "File is not shared")],
            });

        var act = () => Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 1, Destination, "17", CancellationToken.None);

        (await act.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.Message.Should().Contain("File is not shared");
    }

    [Fact]
    public async Task Reports_an_enqueue_slskd_accepted_but_never_listed()
    {
        var api = Substitute.For<ISlskdTransferApi>();
        api.EnqueueAsync(Arg.Any<SlskdEnqueueBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlskdEnqueueBatchResponse { Batch = new SlskdBatch { Id = BatchId } });
        api.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<SlskdUserTransfers>());

        var act = () => Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 1, Destination, "17", CancellationToken.None);

        (await act.Should().ThrowAsync<SlskdEnqueueException>())
            .Which.Message.Should().Contain("no transfer for it");
    }

    [Fact]
    public async Task Enqueues_into_a_subfolder_but_never_out_of_the_download_directory()
    {
        var sent = new List<SlskdEnqueueBatchRequest>();

        var api = Substitute.For<ISlskdTransferApi>();
        api.EnqueueAsync(Arg.Do<SlskdEnqueueBatchRequest>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(new SlskdEnqueueBatchResponse { Batch = new SlskdBatch { Id = BatchId } });
        api.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SlskdUserTransfers> { Transfers(RemoteFilename, older: 0, BatchId) });

        await Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 1, "wondarr/17/nested", "17", CancellationToken.None);

        sent.Should().ContainSingle().Which.Options!.Destination.Should().Be("wondarr/17/nested");
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("wondarr/../../x")]
    [InlineData("/abs")]
    [InlineData("\\abs")]
    [InlineData("C:\\x")]
    [InlineData("c:/x")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_a_destination_that_leaves_the_download_directory(string destination)
    {
        var api = Substitute.For<ISlskdTransferApi>();

        var act = () => Downloads(api).EnqueueAsync("DJ Snake", RemoteFilename, 1, destination, "17", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await api.DidNotReceive().EnqueueAsync(Arg.Any<SlskdEnqueueBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reports_progress_and_the_queue_position_for_a_remote_queue()
    {
        var api = Api(TransferJson("Queued, Remotely"));
        api.GetPlaceInQueueAsync("DJ Snake", TransferId, Arg.Any<CancellationToken>()).Returns(12);

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.State.Should().Be(SlskdTransferState.Queued | SlskdTransferState.Remotely);
        status.PlaceInQueue.Should().Be(12);
        status.LocalPath.Should().BeNull();
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task Keeps_the_reported_position_when_the_peer_will_not_answer()
    {
        var api = Api(TransferJson("Queued, Remotely"));
        api.GetPlaceInQueueAsync("DJ Snake", TransferId, Arg.Any<CancellationToken>())
            .Returns<int?>(_ => throw new HttpRequestException("the peer did not answer"));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.PlaceInQueue.Should().Be(12);
    }

    [Fact]
    public async Task Reports_a_successful_transfer_with_the_file_slskd_wrote()
    {
        var folder = Path.Combine(_downloadsDir, Destination);
        var written = Path.Combine(folder, "Get Lucky.mp3");
        await File.WriteAllTextAsync(written, "not really audio");

        var api = Api(TransferJson("Completed, Succeeded"));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.State.Should().Be(SlskdTransferState.Completed | SlskdTransferState.Succeeded);
        status.PercentComplete.Should().Be(100);
        status.BytesTransferred.Should().Be(8_345_678);
        status.LocalPath.Should().Be(written);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task Finds_the_file_slskd_renamed_rather_than_overwrote()
    {
        var folder = Path.Combine(_downloadsDir, Destination);
        var renamed = Path.Combine(folder, "Get Lucky_1638345123456.mp3");
        await File.WriteAllTextAsync(renamed, "not really audio");

        var api = Api(TransferJson("Completed, Succeeded"));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.LocalPath.Should().Be(renamed);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task Reports_a_completed_transfer_whose_file_is_not_there()
    {
        var api = Api(TransferJson("Completed, Succeeded"));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.LocalPath.Should().BeNull();
        status.Error.Should().Be($"Completed file not found in {Path.Combine(_downloadsDir, Destination)}");
    }

    [Fact]
    public async Task Never_looks_for_a_file_before_the_transfer_has_succeeded()
    {
        // A file with the right name is there, but it is not this transfer's yet.
        await File.WriteAllTextAsync(Path.Combine(_downloadsDir, Destination, "Get Lucky.mp3"), "half a file");

        var api = Api(TransferJson("InProgress"));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.LocalPath.Should().BeNull();
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task Reports_a_failed_transfer_and_slskds_reason()
    {
        var api = Api(TransferJson("Completed, Errored", "\"Timed out waiting for the peer\""));

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.State.Should().Be(SlskdTransferState.Completed | SlskdTransferState.Errored);
        status.Error.Should().Be("Timed out waiting for the peer");
        status.LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task Reports_a_transfer_slskd_no_longer_has_as_failed()
    {
        var api = Substitute.For<ISlskdTransferApi>();
        api.GetAsync("DJ Snake", TransferId, Arg.Any<CancellationToken>()).Returns((SlskdTransfer?)null);

        var status = await Downloads(api).GetStatusAsync(Grab(), CancellationToken.None);

        status.State.Should().Be(SlskdTransferState.Completed | SlskdTransferState.Errored);
        status.Error.Should().Be("Transfer no longer exists in slskd");
        status.LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task Cancels_and_removes_the_transfer()
    {
        var api = Substitute.For<ISlskdTransferApi>();

        await Downloads(api).CancelAsync(Grab(), CancellationToken.None);

        await api.Received(1).CancelAsync("DJ Snake", TransferId, true, Arg.Any<CancellationToken>());
    }

    private static SlskdGrab Grab() => new("DJ Snake", TransferId, RemoteFilename, Destination);

    private static SlskdUserTransfers Transfers(string filename, int older, Guid? batchId, Guid? id = null) =>
        new()
        {
            Username = "DJ Snake",
            Directories =
            [
                new SlskdTransferDirectory
                {
                    Directory = @"Music\Singles",
                    FileCount = 1,
                    Files =
                    [
                        new SlskdTransfer
                        {
                            Id = id ?? TransferId,
                            BatchId = batchId,
                            Username = "DJ Snake",
                            Direction = "Download",
                            Filename = filename,
                            Size = 8_345_678,
                            State = "Queued, Remotely",
                            RequestedAt = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc).AddMinutes(older),
                        },
                    ],
                },
            ],
        };

    /// <summary>A transfer API that answers <c>GET …/{id}</c> with <paramref name="json"/>.</summary>
    private static ISlskdTransferApi Api(string json)
    {
        var api = Substitute.For<ISlskdTransferApi>();

        api.GetAsync("DJ Snake", TransferId, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<SlskdTransfer>(json, SerializerOptions));

        return api;
    }

    private static string TransferJson(string state, string? exception = null) =>
        $$"""
        {
          "id": "{{TransferId:D}}",
          "username": "DJ Snake",
          "direction": "Download",
          "filename": "Music\\Singles\\Get Lucky.mp3",
          "size": 8345678,
          "state": "{{state}}",
          "bytesTransferred": 8345678,
          "percentComplete": 100,
          "placeInQueue": 12,
          "exception": {{exception ?? "null"}}
        }
        """;

    private SlskdDownloads Downloads(ISlskdTransferApi api)
    {
        var services = new ServiceCollection();
        services.AddSingleton(api);

        var options = new SoulseekOptions { DownloadsDir = _downloadsDir };

        return new SlskdDownloads(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            SlskdTestData.Monitor(options),
            NullLogger<SlskdDownloads>.Instance);
    }
}