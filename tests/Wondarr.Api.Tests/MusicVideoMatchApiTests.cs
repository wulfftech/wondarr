using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The Match page's Music videos tab (ADR-0014, the 2026-11-19 amendment): the queue of videos already in the video
/// folder, the choices that settle them, and the 409 while the feature is off. The real <see cref="LibraryRoots"/> runs
/// over temporary folders.
/// </summary>
public sealed class MusicVideoMatchApiTests : IDisposable
{
    private const string Settings = "/api/v1/musicvideo/settings";
    private const string Match = "/api/v1/musicvideo/match";

    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;

    public MusicVideoMatchApiTests()
    {
        _factory = new WondarrAppFactory(
            configureServices: services =>
            {
                services.RemoveAll<ILibraryRoots>();
                services.AddSingleton<ILibraryRoots, LibraryRoots>();

                // Saving the root queues a scan in the real app; here the tests seed the table by hand, and a scan running
                // beside them over the same folder would change what they seeded.
                services.RemoveAll<IMusicVideoScanTrigger>();
                services.AddSingleton<IMusicVideoScanTrigger, NoScanTrigger>();
            });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
    }

    private string VideoRoot => Path.Combine(_factory.DataDir, "videos");

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task While_the_feature_is_off_every_endpoint_answers_409()
    {
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync("Massive Attack - Teardrop.mkv", MusicVideoFileState.Ambiguous);

        var calls = new (HttpMethod Method, string Path, string? Body)[]
        {
            (HttpMethod.Get, Match, null),
            (HttpMethod.Get, $"{Match}?state=unmatched", null),
            (HttpMethod.Get, $"{Match}/summary", null),
            (HttpMethod.Get, $"{Match}/songs?term=tear", null),
            (HttpMethod.Get, $"{Match}/{fileId}/thumbnail", null),
            (HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}}}"""),
            (HttpMethod.Post, $"{Match}/bulk", $$"""{"ids":[{{fileId}}],"action":"ignore"}"""),
            (HttpMethod.Post, "/api/v1/musicvideo/scan", null),
        };

        foreach (var (method, path, body) in calls)
        {
            var (status, problem) = await SendAsync(method, path, body);

            status.Should().Be(HttpStatusCode.Conflict, $"{method} {path}");
            problem!["detail"]!.GetValue<string>().Should().Be("Music videos are turned off");
        }
    }

    [Fact]
    public async Task The_endpoints_need_the_api_key()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync(new Uri($"{Match}/summary", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_scan_can_be_queued_on_demand()
    {
        await EnableAsync();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/musicvideo/scan");

        status.Should().Be(HttpStatusCode.Accepted);
        body!["commandId"]!.GetValue<long>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_summary_counts_the_files_per_state_and_the_badge_is_what_needs_a_person()
    {
        await EnableAsync();
        await SeedFileAsync("a - 1.mkv", MusicVideoFileState.Ambiguous);
        await SeedFileAsync("a - 2.mkv", MusicVideoFileState.Ambiguous);
        await SeedFileAsync("b - 1.mkv", MusicVideoFileState.Unmatched);
        await SeedFileAsync("c - 1.mkv", MusicVideoFileState.Ignored);
        await SeedFileAsync("d - 1.mkv", MusicVideoFileState.Adopted);

        var (status, body) = await SendAsync(HttpMethod.Get, $"{Match}/summary");

        status.Should().Be(HttpStatusCode.OK);
        body!["ambiguous"]!.GetValue<int>().Should().Be(2);
        body["unmatched"]!.GetValue<int>().Should().Be(1);
        body["ignored"]!.GetValue<int>().Should().Be(1);
        body["adopted"]!.GetValue<int>().Should().Be(1);
        body["needsAttention"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task The_list_shows_the_names_the_candidates_and_a_thumbnail_when_one_lies_beside_the_file()
    {
        await EnableAsync();
        var first = await SeedSongAsync("Teardrop", durationMs: 330_000, album: "Mezzanine");
        var second = await SeedSongAsync("Teardrop", durationMs: 250_000, album: "Collected");
        await SeedVideoAsync(second, path: null, MusicVideoStatus.Present);
        var fileId = await SeedFileAsync(
            "Massive Attack/Massive Attack - Teardrop.mkv",
            MusicVideoFileState.Ambiguous,
            candidates: [new MusicVideoFileCandidate(first, 1.05, "the .nfo; title 1.00"), new MusicVideoFileCandidate(second, 1.0, "the song already has a video")],
            parsedArtist: "Massive Attack",
            parsedTitle: "Teardrop",
            nfoTitle: "Teardrop",
            nfoArtist: "Massive Attack");
        await File.WriteAllTextAsync(Path.Combine(VideoRoot, "Massive Attack", "Massive Attack - Teardrop-thumb.jpg"), "jpeg");
        await SeedFileAsync("Other/Other - Song.mkv", MusicVideoFileState.Unmatched);

        var (status, body) = await SendAsync(HttpMethod.Get, Match);

        status.Should().Be(HttpStatusCode.OK);
        body!["totalRecords"]!.GetValue<int>().Should().Be(1, "the default state is ambiguous");
        var row = body["records"]!.AsArray().Single()!;
        row["id"]!.GetValue<long>().Should().Be(fileId);
        row["state"]!.GetValue<string>().Should().Be("ambiguous");
        row["path"]!.GetValue<string>().Should().Be("Massive Attack/Massive Attack - Teardrop.mkv", "relative to the root, with slashes");
        row["parsedArtist"]!.GetValue<string>().Should().Be("Massive Attack");
        row["nfoTitle"]!.GetValue<string>().Should().Be("Teardrop");
        row["container"]!.GetValue<string>().Should().Be("matroska,webm");
        row["durationMs"]!.GetValue<int>().Should().Be(284_000);
        row["size"]!.GetValue<long>().Should().Be(11);
        row["thumbnailUrl"]!.GetValue<string>().Should().Be($"/api/v1/musicvideo/match/{fileId}/thumbnail");

        var candidates = row["candidates"]!.AsArray();
        candidates.Should().HaveCount(2);
        candidates[0]!["songId"]!.GetValue<long>().Should().Be(first);
        candidates[0]!["title"]!.GetValue<string>().Should().Be("Teardrop");
        candidates[0]!["artistCredit"]!.GetValue<string>().Should().Be("Massive Attack");
        candidates[0]!["album"]!.GetValue<string>().Should().Be("Mezzanine");
        candidates[0]!["durationMs"]!.GetValue<int>().Should().Be(330_000);
        candidates[0]!["score"]!.GetValue<double>().Should().BeApproximately(1.05, 0.0001);
        candidates[0]!["songHasVideo"]!.GetValue<bool>().Should().BeFalse();
        candidates[1]!["songHasVideo"]!.GetValue<bool>().Should().BeTrue();
        candidates[1]!["reason"]!.GetValue<string>().Should().Be("the song already has a video");

        var (_, unmatched) = await SendAsync(HttpMethod.Get, $"{Match}?state=unmatched");
        unmatched!["records"]!.AsArray().Single()!["thumbnailUrl"].Should().BeNull();
    }

    [Fact]
    public async Task The_list_pages_and_refuses_an_unknown_state()
    {
        await EnableAsync();

        for (var index = 0; index < 5; index++)
        {
            await SeedFileAsync($"x - {index}.mkv", MusicVideoFileState.Ambiguous);
        }

        var (_, page) = await SendAsync(HttpMethod.Get, $"{Match}?state=ambiguous&page=2&pageSize=2");

        page!["totalRecords"]!.GetValue<int>().Should().Be(5);
        page["records"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).Should().Equal("x - 2.mkv", "x - 3.mkv");

        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Match}?state=adopted");

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["state"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_thumbnail_is_streamed_from_beside_the_files_own_video()
    {
        await EnableAsync();
        var fileId = await SeedFileAsync("Massive Attack - Teardrop.mkv", MusicVideoFileState.Unmatched);
        await File.WriteAllBytesAsync(Path.Combine(VideoRoot, "Massive Attack - Teardrop.png"), [1, 2, 3]);

        using var response = await _client.GetAsync(new Uri($"{Match}/{fileId}/thumbnail", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task The_thumbnail_is_a_404_when_nothing_lies_beside_the_file_or_the_file_is_unknown()
    {
        await EnableAsync();
        var fileId = await SeedFileAsync("Massive Attack - Teardrop.mkv", MusicVideoFileState.Unmatched);

        (await SendAsync(HttpMethod.Get, $"{Match}/{fileId}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"{Match}/9999/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_thumbnail_is_confined_to_the_root_and_to_the_files_own_name()
    {
        await EnableAsync();

        // An image of another file's name, and a row whose path lies outside the root with an image beside it.
        await File.WriteAllBytesAsync(Path.Combine(VideoRoot, "Someone Else - Other.jpg"), [9]);
        var unrelated = await SeedFileAsync("Massive Attack - Teardrop.mkv", MusicVideoFileState.Unmatched);

        var outside = Path.Combine(_factory.DataDir, "elsewhere", "Secret.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllBytesAsync(Path.ChangeExtension(outside, ".jpg"), [7]);
        var escaped = await SeedFileAbsoluteAsync(outside, MusicVideoFileState.Unmatched);

        (await SendAsync(HttpMethod.Get, $"{Match}/{unrelated}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound, "only an image named after the file's own video counts");
        (await SendAsync(HttpMethod.Get, $"{Match}/{escaped}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound, "outside the root");

        // No path from the client is ever read: the id is all the route takes, and a path-looking id is not a route.
        using var traversal = await _client.GetAsync(new Uri($"{Match}/..%2F..%2Fsecret/thumbnail", UriKind.Relative));
        traversal.Content.Headers.ContentType?.MediaType.Should().NotStartWith("image/");
    }

    [Fact]
    public async Task A_thumbnail_that_is_a_link_to_somewhere_else_is_not_followed()
    {
        await EnableAsync();
        var secret = Path.Combine(_factory.DataDir, "secret.jpg");
        await File.WriteAllBytesAsync(secret, [7]);
        var fileId = await SeedFileAsync("Massive Attack - Teardrop.mkv", MusicVideoFileState.Unmatched);

        try
        {
            File.CreateSymbolicLink(Path.Combine(VideoRoot, "Massive Attack - Teardrop.jpg"), secret);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // This machine may not make links; the rule is the same wherever it can.
            return;
        }

        (await SendAsync(HttpMethod.Get, $"{Match}/{fileId}/thumbnail")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolving_to_a_song_adopts_the_file_where_it_lies()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Unmatched);

        var (status, body) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}}}""");

        status.Should().Be(HttpStatusCode.OK);
        body!["state"]!.GetValue<string>().Should().Be("adopted");
        body["songId"]!.GetValue<long>().Should().Be(songId);

        var (_, video) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");
        video!["status"]!.GetValue<string>().Should().Be("present");
        video["foundBy"]!.GetValue<string>().Should().Be("existing");
        video["path"]!.GetValue<string>().Should().Be(Path.Combine(VideoRoot, "clip.mkv"));
        video["youTubeVideoId"].Should().BeNull("a found video has no YouTube id");
        File.Exists(Path.Combine(VideoRoot, "clip.mkv")).Should().BeTrue("the file stays where it is, as it is");
    }

    [Fact]
    public async Task Resolving_to_a_song_that_already_has_a_video_is_a_409_unless_replace_is_true()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var downloaded = Path.Combine(VideoRoot, "Massive Attack", "Massive Attack - Teardrop.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        await File.WriteAllTextAsync(downloaded, "downloaded");
        await File.WriteAllTextAsync(Path.ChangeExtension(downloaded, ".nfo"), "<musicvideo/>");
        await SeedVideoAsync(songId, downloaded, MusicVideoStatus.Present);
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Ambiguous);

        var (refused, problem) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}}}""");

        refused.Should().Be(HttpStatusCode.Conflict);
        problem!["detail"]!.GetValue<string>().Should().Be("The song already has a video.");
        File.Exists(downloaded).Should().BeTrue();

        var (replaced, _) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}},"replace":true}""");

        replaced.Should().Be(HttpStatusCode.OK);
        File.Exists(downloaded).Should().BeFalse("a downloaded video that is replaced goes through the recycle bin");
        File.Exists(Path.ChangeExtension(downloaded, ".nfo")).Should().BeFalse();

        var (_, video) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");
        video!["foundBy"]!.GetValue<string>().Should().Be("existing");
    }

    [Fact]
    public async Task A_replace_whose_old_video_cannot_be_recycled_still_succeeds_and_the_row_is_never_half_done()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var outside = Path.Combine(_factory.DataDir, "elsewhere", "Teardrop.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "x");
        await SeedVideoAsync(songId, outside, MusicVideoStatus.Present);
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Ambiguous);

        var (status, _) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}},"replace":true}""");

        status.Should().Be(HttpStatusCode.OK, "the song has its new video; the old file is left where it is");
        File.Exists(outside).Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var video = await context.MusicVideos.SingleAsync();
        video.FoundBy.Should().Be(MusicVideoFoundBy.Existing);
        (await context.MusicVideoFiles.SingleAsync()).SongId.Should().Be(songId);
    }

    [Fact]
    public async Task Replacing_an_adopted_video_leaves_its_file_where_it_is_and_sends_it_back_to_the_queue()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var firstFile = await SeedFileAsync("first.mkv", MusicVideoFileState.Unmatched);
        var secondFile = await SeedFileAsync("second.mkv", MusicVideoFileState.Unmatched);
        (await SendAsync(HttpMethod.Post, $"{Match}/{firstFile}/resolve", $$"""{"songId":{{songId}}}""")).Status.Should().Be(HttpStatusCode.OK);

        var (status, _) = await SendAsync(HttpMethod.Post, $"{Match}/{secondFile}/resolve", $$"""{"songId":{{songId}},"replace":true}""");

        status.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(VideoRoot, "first.mkv")).Should().BeTrue("an adopted file is the user's, and is only ever recycled when they remove it");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        (await context.MusicVideos.SingleAsync()).Path.Should().Be(Path.Combine(VideoRoot, "second.mkv"));
        var first = await context.MusicVideoFiles.SingleAsync(file => file.Id == firstFile);
        first.State.Should().Be(MusicVideoFileState.Ambiguous);
        first.SongId.Should().BeNull();
    }

    [Fact]
    public async Task Resolving_the_same_file_to_the_same_song_again_is_fine()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Unmatched);
        var body = $$"""{"songId":{{songId}}}""";

        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", body)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", body)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_download_in_progress_for_the_song_refuses_a_resolve()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Unmatched);

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            context.Commands.Add(new Wondarr.Core.Jobs.CommandRecord
            {
                Name = MusicVideoDownloadCommandHandler.CommandName,
                Body = MusicVideoCommandBodies.Write(new MusicVideoCommandBodies.Download(songId, "u7K72X4eo_s", MusicVideoFoundBy.Manual)),
                Status = Wondarr.Core.Jobs.CommandStatus.Started,
            });
            await context.SaveChangesAsync();
        }

        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}}}""");

        status.Should().Be(HttpStatusCode.Conflict);
        problem!["title"]!.GetValue<string>().Should().Be("Download in progress");
    }

    [Fact]
    public async Task A_resolve_that_names_nothing_or_too_much_is_a_400_and_an_unknown_file_or_song_is_a_404()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync("clip.mkv", MusicVideoFileState.Unmatched);

        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", "{}")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", $$"""{"songId":{{songId}},"ignore":true}""")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"{Match}/9999/resolve", $$"""{"songId":{{songId}}}""")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", """{"songId":9999}""")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ignore_marks_the_file_and_unignore_puts_it_back_where_it_came_from()
    {
        await EnableAsync();
        var songId = await SeedSongAsync("Teardrop");
        var fileId = await SeedFileAsync(
            "clip.mkv",
            MusicVideoFileState.Ambiguous,
            candidates: [new MusicVideoFileCandidate(songId, 0.8, "the file name")]);

        var (ignored, ignoredBody) = await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", """{"ignore":true}""");

        ignored.Should().Be(HttpStatusCode.OK);
        ignoredBody!["state"]!.GetValue<string>().Should().Be("ignored");
        (await SendAsync(HttpMethod.Get, $"{Match}?state=ignored")).Body!["totalRecords"]!.GetValue<int>().Should().Be(1);
        (await SendAsync(HttpMethod.Get, $"{Match}?state=ambiguous")).Body!["totalRecords"]!.GetValue<int>().Should().Be(0);

        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", """{"unignore":true}""")).Status.Should().Be(HttpStatusCode.OK);

        (await SendAsync(HttpMethod.Get, $"{Match}?state=ambiguous")).Body!["totalRecords"]!.GetValue<int>().Should().Be(1, "its candidates were kept");
        (await SendAsync(HttpMethod.Post, $"{Match}/{fileId}/resolve", """{"unignore":true}""")).Status.Should().Be(HttpStatusCode.Conflict, "it is not ignored now");
    }

    [Fact]
    public async Task Bulk_accept_top_links_each_files_top_candidate_when_its_song_has_no_video()
    {
        await EnableAsync();
        var free = await SeedSongAsync("Free Song");
        var taken = await SeedSongAsync("Taken Song");
        await SeedVideoAsync(taken, path: null, MusicVideoStatus.Present);
        var withFreeTop = await SeedFileAsync("a.mkv", MusicVideoFileState.Ambiguous, candidates: [new MusicVideoFileCandidate(free, 0.97, "x")]);
        var withTakenTop = await SeedFileAsync("b.mkv", MusicVideoFileState.Ambiguous, candidates: [new MusicVideoFileCandidate(taken, 0.97, "x")]);
        var withNone = await SeedFileAsync("c.mkv", MusicVideoFileState.Unmatched);
        var tooWeak = await SeedFileAsync("d.mkv", MusicVideoFileState.Ambiguous, candidates: [new MusicVideoFileCandidate(free, 0.9, "x")]);
        var tooClose = await SeedFileAsync("e.mkv", MusicVideoFileState.Ambiguous, candidates: [new MusicVideoFileCandidate(free, 0.97, "x"), new MusicVideoFileCandidate(taken, 0.95, "x")]);

        var (status, body) = await SendAsync(
            HttpMethod.Post,
            $"{Match}/bulk",
            $$"""{"ids":[{{withFreeTop}},{{withTakenTop}},{{withNone}},{{tooWeak}},{{tooClose}}],"action":"acceptTop"}""");

        status.Should().Be(HttpStatusCode.OK);
        body!["requested"]!.GetValue<int>().Should().Be(5);
        body["adopted"]!.GetValue<int>().Should().Be(1);
        body["skipped"]!.GetValue<int>().Should().Be(4, "no candidate, a taken song, a weak title and a close second are all left for a person");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        (await context.MusicVideoFiles.SingleAsync(file => file.Id == withFreeTop)).State.Should().Be(MusicVideoFileState.Adopted);
        (await context.MusicVideoFiles.SingleAsync(file => file.Id == withTakenTop)).State.Should().Be(MusicVideoFileState.Ambiguous);
        (await context.MusicVideoFiles.SingleAsync(file => file.Id == withNone)).State.Should().Be(MusicVideoFileState.Unmatched);
    }

    [Fact]
    public async Task Bulk_ignore_ignores_each_file_and_bad_bodies_are_a_400()
    {
        await EnableAsync();
        var first = await SeedFileAsync("a.mkv", MusicVideoFileState.Unmatched);
        var second = await SeedFileAsync("b.mkv", MusicVideoFileState.Ambiguous);

        var (status, body) = await SendAsync(HttpMethod.Post, $"{Match}/bulk", $$"""{"ids":[{{first}},{{second}},9999],"action":"ignore"}""");

        status.Should().Be(HttpStatusCode.OK);
        body!["ignored"]!.GetValue<int>().Should().Be(2);
        body["skipped"]!.GetValue<int>().Should().Be(1, "9999 is not a file");

        (await SendAsync(HttpMethod.Post, $"{Match}/bulk", """{"ids":[],"action":"ignore"}""")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"{Match}/bulk", $$"""{"ids":[{{first}}],"action":"delete"}""")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_song_search_matches_artist_and_title_words_and_returns_at_most_25()
    {
        await EnableAsync();
        await SeedSongAsync("Teardrop", artist: "Massive Attack");
        await SeedSongAsync("Teardrop", artist: "Someone Else");
        await SeedSongAsync("100% Pure_Love", artist: "Crystal Waters");

        var (_, both) = await SendAsync(HttpMethod.Get, $"{Match}/songs?term=massive%20teardrop");

        var songs = both!.AsArray();
        songs.Should().ContainSingle();
        songs[0]!["artistCredit"]!.GetValue<string>().Should().Be("Massive Attack");
        songs[0]!["title"]!.GetValue<string>().Should().Be("Teardrop");
        songs[0]!["hasVideo"]!.GetValue<bool>().Should().BeFalse();

        (await SendAsync(HttpMethod.Get, $"{Match}/songs?term=100%25%20pure_")).Body!.AsArray().Should().ContainSingle("LIKE wildcards are plain text");

        for (var index = 0; index < 30; index++)
        {
            await SeedSongAsync($"Bulk Song {index}", artist: "Many Songs");
        }

        (await SendAsync(HttpMethod.Get, $"{Match}/songs?term=bulk")).Body!.AsArray().Should().HaveCount(25);
        (await SendAsync(HttpMethod.Get, $"{Match}/songs?term=")).Body!.AsArray().Should().BeEmpty();
    }

    private static string Json(IReadOnlyList<MusicVideoFileCandidate> candidates) => MusicVideoFileRules.Write(candidates);

    private sealed class NoScanTrigger : IMusicVideoScanTrigger
    {
        public Task EnqueueAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private async Task EnableAsync()
    {
        Directory.CreateDirectory(VideoRoot);
        var body = new JsonObject { ["enabled"] = true, ["rootPath"] = VideoRoot }.ToJsonString();
        var (status, response) = await SendAsync(HttpMethod.Put, Settings, body);

        status.Should().Be(HttpStatusCode.OK, response?.ToJsonString());
    }

    private async Task<long> SeedSongAsync(string title, string artist = "Massive Attack", int? durationMs = 284_000, string album = "Singles")
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var artistRow = await context.Artists.FirstOrDefaultAsync(row => row.Name == artist);

        if (artistRow is null)
        {
            artistRow = new Artist { Name = artist, SortName = artist };
            context.Artists.Add(artistRow);
            await context.SaveChangesAsync();
        }

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist,
            PrimaryArtistId = artistRow.Id,
            DurationMs = durationMs,
            Monitored = true,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        song.Artists.Add(new SongArtist { Artist = artistRow, Role = ArtistRole.Main, Position = 1 });
        song.AlbumContext = new AlbumContext
        {
            Kind = AlbumContextKind.Album,
            AlbumTitle = album,
            AlbumArtist = artist,
            AlbumKey = Guid.NewGuid().ToString("D"),
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    private Task<long> SeedFileAsync(
        string relative,
        MusicVideoFileState state,
        IReadOnlyList<MusicVideoFileCandidate>? candidates = null,
        string? parsedArtist = null,
        string? parsedTitle = null,
        string? nfoTitle = null,
        string? nfoArtist = null)
    {
        var path = Path.Combine(VideoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "video bytes");

        return SeedFileAbsoluteAsync(path, state, candidates, parsedArtist, parsedTitle, nfoTitle, nfoArtist);
    }

    private async Task<long> SeedFileAbsoluteAsync(
        string path,
        MusicVideoFileState state,
        IReadOnlyList<MusicVideoFileCandidate>? candidates = null,
        string? parsedArtist = null,
        string? parsedTitle = null,
        string? nfoTitle = null,
        string? nfoArtist = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var file = new MusicVideoFile
        {
            Path = path,
            Size = 11,
            ModifiedAt = DateTime.UtcNow,
            Container = "matroska,webm",
            DurationMs = 284_000,
            Width = 1920,
            Height = 1080,
            State = state,
            Candidates = Json(candidates ?? []),
            ParsedArtist = parsedArtist,
            ParsedTitle = parsedTitle,
            NfoTitle = nfoTitle,
            NfoArtist = nfoArtist,
        };
        context.MusicVideoFiles.Add(file);
        await context.SaveChangesAsync();

        return file.Id;
    }

    private async Task SeedVideoAsync(long songId, string? path, MusicVideoStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        context.MusicVideos.Add(new MusicVideo
        {
            SongId = songId,
            Status = status,
            YouTubeVideoId = "u7K72X4eo_s",
            FoundBy = MusicVideoFoundBy.MusicBrainz,
            Path = path,
        });
        await context.SaveChangesAsync();
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
}
