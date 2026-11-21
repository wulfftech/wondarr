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
using Wondarr.Sources.YouTube;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Finding and picking a music video through the API (ADR-0014): the search, the manual grab, the bulk search and
/// the refusal to remove a video whose download is in flight. The finder and the command queue are faked, so nothing
/// goes to MusicBrainz, YouTube or yt-dlp, and no command runs.
/// </summary>
public sealed class MusicVideoFindApiTests : IDisposable
{
    private const string Settings = "/api/v1/musicvideo/settings";
    private const string VideoId = "u7K72X4eo_s";

    private readonly ICommandQueue _commands = Substitute.For<ICommandQueue>();
    private readonly FindingFinder _finder = new();
    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;
    private int _commandIds;

    public MusicVideoFindApiTests()
    {
        _commands
            .EnqueueAsync(default!, default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(new CommandRecord { Id = ++_commandIds, Name = call.ArgAt<string>(0) }));

        _factory = new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<IMusicVideoFinder>();
            services.AddSingleton<IMusicVideoFinder>(_finder);
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

    [Fact]
    public async Task A_search_with_a_confident_candidate_queues_the_download_and_returns_the_candidates()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        _finder.Candidates =
        [
            Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz, MusicVideoCandidateKind.Unknown, score: 10_000),
            Candidate("BBBBBBBBBBB", confident: false, MusicVideoFoundBy.YouTubeSearch, MusicVideoCandidateKind.UserUpload, score: 100),
        ];

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");

        status.Should().Be(HttpStatusCode.OK);
        body!["queued"]!.GetValue<bool>().Should().BeTrue();
        var candidates = body["candidates"]!.AsArray();
        candidates.Should().HaveCount(2);
        var first = candidates[0]!;
        first["videoId"]!.GetValue<string>().Should().Be(VideoId);
        first["title"]!.GetValue<string>().Should().Be("Teardrop");
        first["artists"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("Massive Attack");
        first["kind"]!.GetValue<string>().Should().Be("unknown");
        first["durationMs"]!.GetValue<int>().Should().Be(284_000);
        first["thumbnailUrl"]!.GetValue<string>().Should().Be($"https://i.ytimg.com/vi/{VideoId}/hqdefault.jpg");
        first["confident"]!.GetValue<bool>().Should().BeTrue();
        first["foundBy"]!.GetValue<string>().Should().Be("musicBrainz");
        first["score"]!.GetValue<int>().Should().Be(10_000);
        candidates[1]!["kind"]!.GetValue<string>().Should().Be("userUpload");
        candidates[1]!["confident"]!.GetValue<bool>().Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().MusicVideos.SingleAsync();
        row.SongId.Should().Be(songId);
        row.Status.Should().Be(MusicVideoStatus.Queued);
        row.YouTubeVideoId.Should().Be(VideoId);
        row.FoundBy.Should().Be(MusicVideoFoundBy.MusicBrainz);

        await _commands.Received(1).EnqueueAsync(
            MusicVideoDownloadCommandHandler.CommandName,
            Arg.Is<string?>(json => json!.Contains(VideoId) && json.Contains($"\"songId\":{songId}")),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_search_without_a_confident_candidate_returns_the_candidates_and_queues_nothing()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        _finder.Candidates = [Candidate(VideoId, confident: false, MusicVideoFoundBy.YouTubeSearch, MusicVideoCandidateKind.OfficialVideo, score: 1_800)];

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");

        status.Should().Be(HttpStatusCode.OK);
        body!["queued"]!.GetValue<bool>().Should().BeFalse();
        body["candidates"]!.AsArray().Should().ContainSingle();
        await _commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().MusicVideos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_search_that_may_not_download_lists_a_confident_candidate_and_queues_nothing()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        _finder.Candidates = [Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz, MusicVideoCandidateKind.Unknown, score: 10_000)];

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search?autoDownload=false");

        status.Should().Be(HttpStatusCode.OK);
        body!["queued"]!.GetValue<bool>().Should().BeFalse();
        body["candidates"]!.AsArray().Should().ContainSingle();
        await _commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_confident_search_keeps_the_chosen_videos_title_on_the_row_and_the_resource_shows_it()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        _finder.Candidates = [Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz, MusicVideoCandidateKind.Unknown, score: 10_000)];

        await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");
        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        status.Should().Be(HttpStatusCode.OK);
        body!["videoTitle"]!.GetValue<string>().Should().Be("Teardrop");
        body["status"]!.GetValue<string>().Should().Be("queued");
    }

    [Fact]
    public async Task A_search_that_finds_nothing_returns_an_empty_list()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");

        status.Should().Be(HttpStatusCode.OK);
        body!["queued"]!.GetValue<bool>().Should().BeFalse();
        body["candidates"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_search_for_a_song_that_is_not_there_is_a_404()
    {
        await EnableAsync();

        (await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/song/9999/search")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_search_while_youtube_music_cannot_be_searched_is_a_502()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        _finder.Failure = new InnertubeException(HttpStatusCode.TooManyRequests, "slow down");

        var (status, problem) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");

        status.Should().Be(HttpStatusCode.BadGateway);
        problem!["title"]!.GetValue<string>().Should().Contain("YouTube Music");
    }

    [Fact]
    public async Task A_grab_queues_a_manual_download_and_returns_the_command()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"{{VideoId}}"}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body!["commandId"]!.GetValue<long>().Should().Be(1);

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().MusicVideos.SingleAsync();
        row.Status.Should().Be(MusicVideoStatus.Queued);
        row.FoundBy.Should().Be(MusicVideoFoundBy.Manual);
        row.YouTubeVideoId.Should().Be(VideoId);

        await _commands.Received(1).EnqueueAsync(
            MusicVideoDownloadCommandHandler.CommandName,
            Arg.Is<string?>(json => json!.Contains("\"foundBy\":\"manual\"")),
            CommandTrigger.Manual,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_grab_accepts_a_link_to_the_video()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();

        var (status, _) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"https://www.youtube.com/watch?v={{VideoId}}"}""");

        status.Should().Be(HttpStatusCode.Accepted);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<WondarrDbContext>().MusicVideos.SingleAsync()).YouTubeVideoId.Should().Be(VideoId);
    }

    [Theory]
    [InlineData("""{"youTubeVideoId":"not a video"}""")]
    [InlineData("""{"youTubeVideoId":"https://vimeo.com/12345"}""")]
    [InlineData("""{"youTubeVideoId":""}""")]
    [InlineData("""{}""")]
    public async Task A_grab_of_something_that_is_not_a_video_id_is_a_400(string json)
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();

        var (status, problem) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", json);

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["youTubeVideoId"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_grab_of_a_blocklisted_video_is_a_409()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.MusicVideoBlocklist.Add(new MusicVideoBlocklistItem { SongId = songId, YouTubeVideoId = VideoId, Reason = "wrong" });
            await context.SaveChangesAsync();
        }

        var (status, problem) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"{{VideoId}}"}""");

        status.Should().Be(HttpStatusCode.Conflict);
        problem!["title"]!.GetValue<string>().Should().Be("Blocklisted");
        await _commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_grab_for_a_song_that_is_not_there_is_a_404()
    {
        await EnableAsync();

        (await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/song/9999/grab", $$"""{"youTubeVideoId":"{{VideoId}}"}""")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_grab_while_the_feature_is_off_is_a_409()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");

        var (status, problem) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"{{VideoId}}"}""");

        status.Should().Be(HttpStatusCode.Conflict);
        problem!["detail"]!.GetValue<string>().Should().Be("Music videos are turned off");
    }

    [Fact]
    public async Task A_bulk_search_queues_a_confident_only_search_for_each_song_that_exists()
    {
        var first = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        var second = await QueueApiTests.SeedSongAsync(_factory, "Angel");
        await EnableAsync();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/search", $$"""{"songIds":[{{first}},{{second}},{{first}},9999]}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body!["queued"]!.GetValue<int>().Should().Be(2);
        body["commandIds"]!.AsArray().Should().HaveCount(2);

        foreach (var id in new[] { first, second })
        {
            await _commands.Received(1).EnqueueAsync(
                MusicVideoSearchCommandHandler.CommandName,
                Arg.Is<string?>(json => json!.Contains($"\"songId\":{id}") && json.Contains("\"autoDownload\":true")),
                CommandTrigger.Manual,
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task A_bulk_search_of_more_than_200_songs_is_a_400()
    {
        await EnableAsync();
        var ids = string.Join(',', Enumerable.Range(1, 201));

        var (status, problem) = await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/search", $$"""{"songIds":[{{ids}}]}""");

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["songIds"]!.AsArray().Should().NotBeEmpty();
        await _commands.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_bulk_search_of_exactly_200_songs_is_accepted()
    {
        await EnableAsync();
        var ids = string.Join(',', Enumerable.Range(1, 200));

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/search", $$"""{"songIds":[{{ids}}]}""");

        status.Should().Be(HttpStatusCode.Accepted);
        body!["queued"]!.GetValue<int>().Should().Be(0, "none of those songs exist");
    }

    [Theory]
    [InlineData("""{"songIds":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"songIds":[0]}""")]
    public async Task A_bulk_search_that_names_no_song_is_a_400(string json)
    {
        await EnableAsync();

        (await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/search", json)).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_bulk_search_while_the_feature_is_off_is_a_409()
    {
        (await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/search", """{"songIds":[1]}""")).Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(MusicVideoStatus.Queued)]
    [InlineData(MusicVideoStatus.Downloading)]
    public async Task Removing_a_video_whose_download_is_in_flight_is_a_409(MusicVideoStatus status)
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.MusicVideos.Add(new MusicVideo { SongId = songId, Status = status, YouTubeVideoId = VideoId, FoundBy = MusicVideoFoundBy.Manual });
            context.Commands.Add(new CommandRecord
            {
                Name = MusicVideoDownloadCommandHandler.CommandName,
                Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, VideoId, MusicVideoFoundBy.Manual)),
                Status = CommandStatus.Started,
            });
            await context.SaveChangesAsync();
        }

        var (code, problem) = await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}");

        code.Should().Be(HttpStatusCode.Conflict);
        problem!["detail"]!.GetValue<string>().Should().Be("A download is in progress");

        using var check = _factory.Services.CreateScope();
        var checkContext = check.ServiceProvider.GetRequiredService<WondarrDbContext>();
        (await checkContext.MusicVideos.CountAsync()).Should().Be(1, "the row is left alone");
        (await checkContext.MusicVideoBlocklist.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_grab_replaces_a_filed_video_but_a_search_does_not()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.MusicVideos.Add(new MusicVideo { SongId = songId, Status = MusicVideoStatus.Present, YouTubeVideoId = "AAAAAAAAAAA", FoundBy = MusicVideoFoundBy.Manual, Path = "/x/y.mkv" });
            await context.SaveChangesAsync();
        }

        _finder.Candidates = [Candidate(VideoId, confident: true, MusicVideoFoundBy.MusicBrainz, MusicVideoCandidateKind.Unknown, score: 10_000)];

        var (_, searched) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/search");
        searched!["queued"]!.GetValue<bool>().Should().BeFalse("a search never replaces a filed video");

        var (status, _) = await SendAsync(HttpMethod.Post, $"/api/v1/musicvideo/song/{songId}/grab", $$"""{"youTubeVideoId":"{{VideoId}}"}""");
        status.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_scheduled_search_settings_default_to_off_and_daily_and_can_be_changed()
    {
        var (_, defaults) = await SendAsync(HttpMethod.Get, Settings);

        defaults!["scheduledSearchEnabled"]!.GetValue<bool>().Should().BeFalse();
        defaults["scheduledSearchIntervalHours"]!.GetValue<int>().Should().Be(24);

        var (status, changed) = await SendAsync(HttpMethod.Put, Settings, """{"scheduledSearchEnabled":true,"scheduledSearchIntervalHours":6}""");

        status.Should().Be(HttpStatusCode.OK);
        changed!["scheduledSearchEnabled"]!.GetValue<bool>().Should().BeTrue();
        changed["scheduledSearchIntervalHours"]!.GetValue<int>().Should().Be(6);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_factory.ConfigDir, "config.yml"));
        yaml.Should().Contain("scheduled_search_enabled: true").And.Contain("scheduled_search_interval_hours: 6");
    }

    [Fact]
    public async Task A_scheduled_search_interval_out_of_range_is_a_400()
    {
        var (status, problem) = await SendAsync(HttpMethod.Put, Settings, """{"scheduledSearchIntervalHours":0}""");

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!.AsObject().Should().NotBeEmpty();
    }

    private static MusicVideoCandidate Candidate(
        string videoId,
        bool confident,
        MusicVideoFoundBy foundBy,
        MusicVideoCandidateKind kind,
        int score) =>
        new(
            videoId,
            "Teardrop",
            ["Massive Attack"],
            kind,
            284_000,
            MusicVideoCandidate.ThumbnailFor(videoId),
            confident,
            foundBy,
            score);

    private async Task EnableAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, Settings, """{"enabled":true}""");

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
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

    /// <summary>A finder that returns what the test says, or fails.</summary>
    private sealed class FindingFinder : IMusicVideoFinder
    {
        public List<MusicVideoCandidate> Candidates { get; set; } = [];

        public Exception? Failure { get; set; }

        public Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken) =>
            Failure is not null
                ? Task.FromException<MusicVideoFindResult>(Failure)
                : Task.FromResult(new MusicVideoFindResult(Candidates));
    }
}
