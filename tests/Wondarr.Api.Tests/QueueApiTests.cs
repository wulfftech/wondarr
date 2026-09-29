using System.Net;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The queue endpoints: the paged list, the summary badge and the removal with its blocklist and retry
/// flags. The source is faked so a removal never reaches the network.
/// </summary>
public sealed class QueueApiTests
{
    private const string QueueEndpoint = "/api/v1/queue";

    [Fact]
    public async Task Listing_the_queue_shows_only_the_active_grabs_by_default()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Imported);

        using var response = await client.GetAsync(new Uri(QueueEndpoint, UriKind.Relative));
        var page = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page.GetProperty("totalRecords").GetInt32().Should().Be(1);
        page.GetProperty("sortKey").GetString().Should().Be("createdAt");

        var records = page.GetProperty("records");
        records.GetArrayLength().Should().Be(1);
        records[0].GetProperty("state").GetString().Should().Be("downloading");
        records[0].GetProperty("songTitle").GetString().Should().Be("Get Lucky");
        records[0].GetProperty("displayName").GetString().Should().Be("Get Lucky.flac");
        records[0].GetProperty("sourceType").GetString().Should().Be(SourceTypes.Soulseek);
        records[0].GetProperty("qualityName").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Listing_the_queue_with_includeFinished_shows_the_finished_grabs_too()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Imported);

        using var response = await client.GetAsync(new Uri($"{QueueEndpoint}?includeFinished=true", UriKind.Relative));
        var page = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page.GetProperty("totalRecords").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_queue_summary_counts_the_active_grabs_and_flags_a_queued_peer()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);
        await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Imported);

        // Only one grab may be in flight per song (the queue's unique index), so the peer-queued one
        // belongs to a second song.
        var otherSongId = await SeedSongAsync(factory, "Instant Crush");
        var otherCandidateId = await SeedCandidateAsync(factory, otherSongId, "Instant Crush.flac");
        await SeedQueueItemAsync(factory, otherSongId, otherCandidateId, QueueItemState.RemotelyQueued);

        using var response = await client.GetAsync(new Uri($"{QueueEndpoint}/status", UriKind.Relative));
        var status = await ReadJsonAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        status.GetProperty("count").GetInt32().Should().Be(2);
        status.GetProperty("totalCount").GetInt32().Should().Be(2);
        status.GetProperty("unknownCount").GetInt32().Should().Be(0);
        status.GetProperty("warnings").GetBoolean().Should().BeTrue();
        status.GetProperty("errors").GetBoolean().Should().BeFalse();
        status.GetProperty("unknownErrors").GetBoolean().Should().BeFalse();
        status.GetProperty("unknownWarnings").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_download_cancels_it_at_the_source()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        var itemId = await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);

        using var response = await client.DeleteAsync(new Uri($"{QueueEndpoint}/{itemId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{}");

        await source.Received(1).CancelAsync(Arg.Any<GrabHandle>(), Arg.Any<CancellationToken>());

        var item = await ReadQueueItemAsync(factory, itemId);
        item!.State.Should().Be(QueueItemState.Cancelled);
        item.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Removing_a_download_with_blocklist_blocklists_the_candidate_and_asks_for_the_next_one()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out var search);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        var itemId = await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);

        using var response = await client.DeleteAsync(
            new Uri($"{QueueEndpoint}/{itemId}?blocklist=true", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await ReadBlocklistAsync(factory, songId);
        blocked.Should().ContainSingle();
        blocked[0].Should().EndWith(":Get Lucky.flac");

        // The retry is the next attempt, not the one the removed grab was.
        await search.Received(1).GrabBestAsync(Arg.Any<long>(), 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_download_with_blocklist_and_skipRedownload_does_not_retry()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out var search);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        var itemId = await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Downloading);

        using var response = await client.DeleteAsync(
            new Uri($"{QueueEndpoint}/{itemId}?blocklist=true&skipRedownload=true", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBlocklistAsync(factory, songId)).Should().ContainSingle();

        await search.DidNotReceive().GrabBestAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_download_that_is_importing_is_a_conflict()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        var songId = await SeedSongAsync(factory, "Get Lucky");
        var candidateId = await SeedCandidateAsync(factory, songId, "Get Lucky.flac");
        var itemId = await SeedQueueItemAsync(factory, songId, candidateId, QueueItemState.Importing);

        using var response = await client.DeleteAsync(new Uri($"{QueueEndpoint}/{itemId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("detail").GetString()
            .Should().Be("The download is being imported; try again in a moment");
    }

    [Fact]
    public async Task Removing_an_unknown_queue_item_is_a_not_found()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = Authenticated(factory);

        using var response = await client.DeleteAsync(new Uri($"{QueueEndpoint}/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_queue_endpoints_require_a_key()
    {
        var source = ScriptedSource();
        using var factory = Factory(source, out _);
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri(QueueEndpoint, UriKind.Relative));
        using var status = await client.GetAsync(new Uri($"{QueueEndpoint}/status", UriKind.Relative));
        using var remove = await client.DeleteAsync(new Uri($"{QueueEndpoint}/1", UriKind.Relative));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        status.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        remove.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A factory whose only source is the scripted one, and whose search service is a spy.</summary>
    private static WondarrAppFactory Factory(ISourceProvider source, out ISongSearchService search)
    {
        var spy = Substitute.For<ISongSearchService>();
        search = spy;

        return new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<ISourceProvider>();
            services.AddSingleton(source);

            services.RemoveAll<ISongSearchService>();
            services.AddSingleton(spy);
        });
    }

    /// <summary>A source that accepts a cancel without touching the network.</summary>
    private static ISourceProvider ScriptedSource()
    {
        var source = Substitute.For<ISourceProvider>();
        source.SourceType.Returns(SourceTypes.Soulseek);
        source.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns((true, (string?)null));
        source.CancelAsync(Arg.Any<GrabHandle>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return source;
    }

    internal static HttpClient Authenticated(WondarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    internal static async Task<long> SeedSongAsync(WondarrAppFactory factory, string title)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            DurationMs = 369_000,
            Monitored = true,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        song.Artists.Add(new SongArtist { Artist = artist, Role = ArtistRole.Main, Position = 1 });
        song.AlbumContext = new AlbumContext
        {
            Kind = AlbumContextKind.Album,
            AlbumTitle = "Random Access Memories",
            AlbumArtist = artist.Name,
            AlbumKey = Guid.NewGuid().ToString("D"),
            TrackNo = 8,
            DiscNo = 1,
            TotalTracks = 13,
            Date = "2013",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    internal static async Task<long> SeedCandidateAsync(WondarrAppFactory factory, long songId, string displayName)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Manual,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            Outcome = SearchOutcome.NoAcceptableCandidate,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = $"{Guid.NewGuid():N}:{displayName}",
            DisplayName = displayName,
            RemotePath = $"@@peer\\Music\\{displayName}",
            Provider = "peer",
            QualityId = await context.Qualities
                .Where(quality => quality.Name == "FLAC")
                .Select(quality => quality.Id)
                .FirstAsync(),
            SizeBytes = 42_000_000,
            DurationMs = 369_000,
            Normalised = "{}",
            Score = 700,
            ScoreBreakdown = "{}",
            Rejections = "[]",
            Accepted = true,
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        return candidate.Id;
    }

    internal static async Task<long> SeedQueueItemAsync(
        WondarrAppFactory factory,
        long songId,
        long candidateId,
        QueueItemState state)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        // The item points at the run its candidate came from; the column is a real foreign key.
        var searchRunId = await context.Candidates
            .Where(candidate => candidate.Id == candidateId)
            .Select(candidate => candidate.SearchRunId)
            .FirstAsync();

        var now = DateTime.UtcNow;
        var item = new QueueItem
        {
            SongId = songId,
            CandidateId = candidateId,
            SearchRunId = searchRunId,
            SourceType = SourceTypes.Soulseek,
            Handle = "{}",
            Destination = $"wondarr/{Guid.NewGuid():N}",
            State = state,
            Progress = state == QueueItemState.Downloading ? 0.4 : 0,
            BytesTransferred = 1_000,
            SizeBytes = 42_000_000,
            Attempt = 1,
            StateChangedAt = now,
            LastProgressAt = now,
            FinishedAt = state is QueueItemState.Imported or QueueItemState.Failed or QueueItemState.Cancelled
                ? now
                : null,
        };

        context.QueueItems.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    private static async Task<QueueItem?> ReadQueueItemAsync(WondarrAppFactory factory, long id)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await context.QueueItems.FindAsync(id);
    }

    private static async Task<List<string>> ReadBlocklistAsync(WondarrAppFactory factory, long songId)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        return await context.Blocklist
            .Where(entry => entry.SongId == songId)
            .Select(entry => entry.BlocklistKey)
            .ToListAsync();
    }
}
