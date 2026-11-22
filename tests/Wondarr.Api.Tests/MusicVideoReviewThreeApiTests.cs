using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// M-70: what the music video resource, the grab and the search say about a replacement in progress (item 1), how many
/// downloads are ahead of a queued one (item 6a) and the last check for a better copy (item 2). The command queue is
/// faked, and command rows are written the way the real queue writes them.
/// </summary>
public sealed class MusicVideoReviewThreeApiTests : IDisposable
{
    private const string Settings = "/api/v1/musicvideo/settings";
    private const string FatBottomedGirls = "VMnjF1O4eH0";

    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();
    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;
    private int _commandIds = 100;

    public MusicVideoReviewThreeApiTests()
    {
        _commands
            .EnqueueAsync(default!, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(new CommandRecord { Id = ++_commandIds, Name = call.ArgAt<string>(0) }));

        _factory = new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<IMusicVideoFinder>();
            services.AddSingleton<IMusicVideoFinder>(new ConfidentFinder());
            services.RemoveAll<ICommandQueue>();
            services.AddSingleton(_commands);
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<long> SeedPresentSongAsync(int height = 360, int? best = null, DateTime? bestAt = null)
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Fat Bottomed Girls");
        var (status, body) = await SendAsync(HttpMethod.Put, Settings, """{"enabled":true}""");
        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        context.MusicVideos.Add(new MusicVideo
        {
            SongId = songId,
            Status = MusicVideoStatus.Present,
            YouTubeVideoId = FatBottomedGirls,
            VideoTitle = "Queen - Fat Bottomed Girls (Official Video)",
            FoundBy = MusicVideoFoundBy.Manual,
            Path = "/x/y.mkv",
            Height = height,
            BestAvailableHeight = best,
            BestAvailableCheckedAt = bestAt,
        });
        await context.SaveChangesAsync();

        return songId;
    }

    private async Task AddActiveCommandAsync(long songId, string videoId, CommandStatus status, string? title = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        context.Commands.Add(new CommandRecord
        {
            Name = MusicVideoDownloadCommandHandler.CommandName,
            Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, videoId, MusicVideoFoundBy.Manual, title)),
            Status = status,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_filed_video_with_a_queued_download_shows_the_pending_replacement_as_queued()
    {
        var songId = await SeedPresentSongAsync();
        await AddActiveCommandAsync(songId, "AAAAAAAAAAA", CommandStatus.Queued, "Queen - Fat Bottomed Girls (Live)");

        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        status.Should().Be(HttpStatusCode.OK);
        body!["status"]!.GetValue<string>().Should().Be("present");
        var pending = body["pendingReplacement"]!;
        pending["youTubeVideoId"]!.GetValue<string>().Should().Be("AAAAAAAAAAA");
        pending["videoTitle"]!.GetValue<string>().Should().Be("Queen - Fat Bottomed Girls (Live)");
        pending["status"]!.GetValue<string>().Should().Be("queued");
    }

    [Fact]
    public async Task A_running_download_shows_the_pending_replacement_as_downloading()
    {
        var songId = await SeedPresentSongAsync();
        await AddActiveCommandAsync(songId, FatBottomedGirls, CommandStatus.Started);

        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        body!["pendingReplacement"]!["status"]!.GetValue<string>().Should().Be("downloading");
    }

    [Fact]
    public async Task A_filed_video_with_no_download_has_no_pending_replacement()
    {
        var songId = await SeedPresentSongAsync();
        await AddActiveCommandAsync(songId, FatBottomedGirls, CommandStatus.Completed);

        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        body!["pendingReplacement"].Should().BeNull();
    }

    [Fact]
    public async Task The_last_check_for_a_better_copy_is_on_the_resource()
    {
        var checkedAt = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var songId = await SeedPresentSongAsync(best: 360, bestAt: checkedAt);

        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        body!["bestAvailableHeight"]!.GetValue<int>().Should().Be(360);
        body["bestAvailableCheckedAt"]!.GetValue<DateTime>().Should().Be(checkedAt);
    }

    [Fact]
    public async Task A_grab_says_how_many_other_downloads_are_ahead_of_it()
    {
        var songId = await SeedPresentSongAsync();
        var (_, alone) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"{{FatBottomedGirls}}"}""");

        alone!["aheadInQueue"]!.GetValue<int>().Should().Be(0, "nothing else is queued, so it starts at once");

        var other = await QueueApiTests.SeedSongAsync(_factory, "Angel");
        await AddActiveCommandAsync(other, "BBBBBBBBBBB", CommandStatus.Started);
        await AddActiveCommandAsync(other, "CCCCCCCCCCC", CommandStatus.Queued);
        var third = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{third}/grab", """{"youTubeVideoId":"u7K72X4eo_s"}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body!["aheadInQueue"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task A_search_that_queues_a_download_says_how_many_other_downloads_are_ahead_of_it()
    {
        var other = await SeedPresentSongAsync();
        await AddActiveCommandAsync(other, "BBBBBBBBBBB", CommandStatus.Started);
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");

        status.Should().Be(HttpStatusCode.OK);
        body!["queued"]!.GetValue<bool>().Should().BeTrue();
        body["aheadInQueue"]!.GetValue<int>().Should().Be(1);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, raw.Length == 0 ? null : JsonNode.Parse(raw));
    }

    /// <summary>A finder that always has one confident candidate.</summary>
    private sealed class ConfidentFinder : IMusicVideoFinder
    {
        public Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new MusicVideoFindResult(
            [
                new MusicVideoCandidate(
                    "u7K72X4eo_s",
                    "Teardrop",
                    ["Massive Attack"],
                    MusicVideoCandidateKind.Unknown,
                    284_000,
                    MusicVideoCandidate.ThumbnailFor("u7K72X4eo_s"),
                    Confident: true,
                    MusicVideoFoundBy.MusicBrainz,
                    10_000),
            ]));
    }
}
