using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.Domain;
using Wondarr.Core.MusicVideos;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The music video endpoints (ADR-0014): the settings and the root's marker, the 409 while the feature is off,
/// reading and removing a song's video, and the <c>musicVideo</c> field of the song lists. The real
/// <see cref="LibraryRoots"/> runs over temporary folders, so nothing is written to <c>/data</c>.
/// </summary>
public sealed class MusicVideoApiTests : IDisposable
{
    private const string Settings = "/api/v1/musicvideo/settings";

    private readonly VideoQueryCounter _queries = new();
    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;

    public MusicVideoApiTests()
    {
        _factory = new WondarrAppFactory(
            configureServices: services =>
            {
                services.RemoveAll<ILibraryRoots>();
                services.AddSingleton<ILibraryRoots, LibraryRoots>();
                services.ConfigureDbContext<WondarrDbContext>(options => options.AddInterceptors(_queries));
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
    public async Task The_endpoints_need_the_api_key()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync(new Uri(Settings, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(new Uri("/api/v1/musicvideo/song/1", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_settings_report_the_defaults_with_the_feature_off()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, Settings);

        status.Should().Be(HttpStatusCode.OK);
        body!["enabled"]!.GetValue<bool>().Should().BeFalse();
        body["rootPath"]!.GetValue<string>().Should().Be("/data/music-videos", "the media root's default applies below config.yml");
        body["rootPathIsDefault"]!.GetValue<bool>().Should().BeTrue();
        body["defaultRootPath"]!.GetValue<string>().Should().Be("/data/music-videos");
        body["layout"]!.GetValue<string>().Should().Be("folder");
        body["namingTemplate"]!.GetValue<string>().Should().Be("{Artist}/{Artist} - {Title}");
        body["besideTrackSuffix"]!.GetValue<string>().Should().Be("-video");
        body["maxHeight"].Should().BeNull();
        body["writeNfo"]!.GetValue<bool>().Should().BeTrue();
        body["writeThumbnail"]!.GetValue<bool>().Should().BeTrue();
        body["scheduledSearchLimit"]!.GetValue<int>().Should().Be(25);
        body["readOnlyFields"]!.AsArray().Should().BeEmpty();
        body["rootStatus"].Should().NotBeNull("the default root is checked live, whatever the machine holds there");
    }

    [Theory]
    [InlineData("""{"rootPath":"videos/music"}""", "rootPath")]
    [InlineData("""{"rootPath":"/data/music/videos"}""", "rootPath")]
    [InlineData("""{"layout":"nested"}""", "layout")]
    [InlineData("""{"namingTemplate":"{Artist}/{Track}"}""", "namingTemplate")]
    public async Task A_change_that_breaks_a_rule_is_a_400_naming_the_field(string body, string field)
    {
        var (status, problem) = await SendAsync(HttpMethod.Put, Settings, body);

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]![field]!.AsArray().Should().NotBeEmpty();

        var (_, stored) = await SendAsync(HttpMethod.Get, Settings);
        stored!["rootPath"]!.GetValue<string>().Should().Be("/data/music-videos", "a refused change writes nothing");
    }

    [Fact]
    public async Task A_root_inside_the_data_directory_is_a_400()
    {
        var inside = Path.Combine(_factory.ConfigDir, "videos").Replace('\\', '/');

        var (status, problem) = await SendAsync(HttpMethod.Put, Settings, new JsonObject { ["rootPath"] = inside }.ToJsonString());

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["rootPath"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Saving_an_empty_folder_as_the_root_marks_it_and_writes_config_yml()
    {
        Directory.CreateDirectory(VideoRoot);

        var (status, body) = await SendAsync(HttpMethod.Put, Settings, RootBody(VideoRoot, enabled: true));

        status.Should().Be(HttpStatusCode.OK);
        body!["enabled"]!.GetValue<bool>().Should().BeTrue();
        body["rootStatus"]!.GetValue<string>().Should().Be("ready");
        body["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(Path.Combine(VideoRoot, LibraryRoots.MarkerFileName)).Should().BeTrue();

        var yaml = await File.ReadAllTextAsync(Path.Combine(_factory.ConfigDir, "config.yml"));
        yaml.Should().Contain("music_videos:").And.Contain("enabled: true").And.Contain("root_path:");
    }

    [Fact]
    public async Task Turning_on_with_a_folder_that_needs_confirming_saves_the_folder_but_not_the_switch()
    {
        Directory.CreateDirectory(VideoRoot);
        await File.WriteAllTextAsync(Path.Combine(VideoRoot, "old.mkv"), "x");

        var (refused, problem) = await SendAsync(HttpMethod.Put, Settings, RootBody(VideoRoot, enabled: true));

        refused.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["rootPath"]!.AsArray()[0]!.GetValue<string>().Should().Contain("Confirm the folder first");

        var (_, saved) = await SendAsync(HttpMethod.Get, Settings);

        saved!["enabled"]!.GetValue<bool>().Should().BeFalse();
        saved["rootStatus"]!.GetValue<string>().Should().Be("unmarked");
        saved["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(Path.Combine(VideoRoot, LibraryRoots.MarkerFileName)).Should().BeFalse();

        var (status, confirmed) = await SendAsync(HttpMethod.Post, $"{Settings}/root/confirm");

        status.Should().Be(HttpStatusCode.OK);
        confirmed!["rootStatus"]!.GetValue<string>().Should().Be("ready");
        confirmed["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(Path.Combine(VideoRoot, LibraryRoots.MarkerFileName)).Should().BeTrue();

        var (turnedOn, on) = await SendAsync(HttpMethod.Put, Settings, """{"enabled":true}""");

        turnedOn.Should().Be(HttpStatusCode.OK);
        on!["enabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Turning_on_with_a_folder_inside_the_container_is_a_400_naming_the_mount()
    {
        using var containerFactory = new WondarrAppFactory(
            configureServices: services =>
            {
                services.RemoveAll<ContainerMounts>();
                services.AddSingleton(new ContainerMounts(
                    path => path == "/.dockerenv",
                    () => ["1 0 0:1 / / rw - overlay overlay rw", "2 1 0:2 / /data/music rw - cifs //nas/music rw"]));
            });
        using var client = containerFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", containerFactory.ApiKey);

        using var response = await client.PutAsync(
            new Uri(Settings, UriKind.Relative),
            new StringContent("""{"enabled":true,"rootPath":"/data/music-videos"}""", System.Text.Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("is inside the container");
    }

    [Fact]
    public async Task Confirming_with_no_saved_root_is_a_400()
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Settings}/root/confirm");

        status.Should().Be(HttpStatusCode.BadRequest);
        problem!["errors"]!["rootPath"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task While_the_feature_is_off_every_endpoint_but_the_settings_answers_409()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");

        var (get, getProblem) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");
        var (delete, _) = await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}");
        var (settings, _) = await SendAsync(HttpMethod.Get, Settings);

        get.Should().Be(HttpStatusCode.Conflict);
        getProblem!["detail"]!.GetValue<string>().Should().Be("Music videos are turned off");
        delete.Should().Be(HttpStatusCode.Conflict);
        settings.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_song_without_a_video_is_a_404_while_the_feature_is_on()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();

        (await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reading_a_video_returns_the_row()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        await SeedVideoAsync(songId, path: null, MusicVideoStatus.Queued);

        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}");

        status.Should().Be(HttpStatusCode.OK);
        body!["songId"]!.GetValue<long>().Should().Be(songId);
        body["status"]!.GetValue<string>().Should().Be("queued");
        body["youTubeVideoId"]!.GetValue<string>().Should().Be("u7K72X4eo_s");
        body["foundBy"]!.GetValue<string>().Should().Be("musicBrainz");
        body["path"].Should().BeNull();
    }

    [Fact]
    public async Task Deleting_recycles_the_files_blocklists_the_video_and_forgets_it()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        Directory.CreateDirectory(VideoRoot);
        (await SendAsync(HttpMethod.Put, Settings, RootBody(VideoRoot, enabled: true))).Status.Should().Be(HttpStatusCode.OK);
        var video = Path.Combine(VideoRoot, "Daft Punk", "Daft Punk - Teardrop.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(Path.ChangeExtension(video, ".nfo"), "<musicvideo/>");
        await File.WriteAllTextAsync(Path.ChangeExtension(video, ".jpg"), "jpeg");
        await SeedVideoAsync(songId, video, MusicVideoStatus.Present);

        var (status, body) = await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}");

        status.Should().Be(HttpStatusCode.OK);
        body!["blocklisted"]!.GetValue<bool>().Should().BeTrue();
        var recycled = body["recycledPaths"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        recycled.Should().HaveCount(3);
        recycled.Should().OnlyContain(path => File.Exists(path));
        File.Exists(video).Should().BeFalse();
        (await SendAsync(HttpMethod.Get, $"/api/v1/musicvideo/song/{songId}")).Status.Should().Be(HttpStatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        context.MusicVideoBlocklist.Should().ContainSingle(item => item.SongId == songId && item.YouTubeVideoId == "u7K72X4eo_s");
        context.History.Should().ContainSingle(item => item.SongId == songId && item.EventType == HistoryEventType.MusicVideoDeleted);
    }

    [Fact]
    public async Task Deleting_with_blocklist_false_blocklists_nothing()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        await SeedVideoAsync(songId, path: null, MusicVideoStatus.Failed);

        var (status, body) = await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}?blocklist=false");

        status.Should().Be(HttpStatusCode.OK);
        body!["blocklisted"]!.GetValue<bool>().Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<WondarrDbContext>().MusicVideoBlocklist.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/song")]
    [InlineData("/api/v1/wanted/missing")]
    public async Task The_song_lists_carry_the_video_state_only_while_the_feature_is_on(string list)
    {
        var withVideo = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await QueueApiTests.SeedSongAsync(_factory, "Angel");
        await SeedVideoAsync(withVideo, path: null, MusicVideoStatus.Present, height: 1080);

        var (_, off) = await SendAsync(HttpMethod.Get, list);
        var offRecords = off!["records"]!.AsArray();

        offRecords.Should().HaveCount(2);
        offRecords.Should().OnlyContain(record => record!["musicVideo"] == null, "the feature is off");

        await EnableAsync();

        var (_, on) = await SendAsync(HttpMethod.Get, list);
        var records = on!["records"]!.AsArray();
        var teardrop = records.Single(record => record!["title"]!.GetValue<string>() == "Teardrop")!;
        var angel = records.Single(record => record!["title"]!.GetValue<string>() == "Angel")!;

        teardrop["musicVideo"]!["status"]!.GetValue<string>().Should().Be("present");
        teardrop["musicVideo"]!["height"]!.GetValue<int>().Should().Be(1080);
        angel["musicVideo"].Should().BeNull("this song has no video");
    }

    [Theory]
    [InlineData("/api/v1/song")]
    [InlineData("/api/v1/wanted/missing")]
    public async Task A_song_list_page_costs_one_video_query_through_the_controller(string list)
    {
        for (var index = 0; index < 4; index++)
        {
            var songId = await QueueApiTests.SeedSongAsync(_factory, $"Song {index}");
            await SeedVideoAsync(songId, path: null, MusicVideoStatus.Present, height: 720);
        }

        await EnableAsync();
        _queries.Reset();

        var (status, body) = await SendAsync(HttpMethod.Get, list);

        status.Should().Be(HttpStatusCode.OK);
        body!["records"]!.AsArray().Should().HaveCount(4);
        _queries.Count.Should().Be(1, "one video query for the page, not one per row");
    }

    [Fact]
    public async Task While_the_feature_is_off_a_song_list_page_costs_no_video_query()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await SeedVideoAsync(songId, path: null, MusicVideoStatus.Present, height: 720);
        _queries.Reset();

        var (status, _) = await SendAsync(HttpMethod.Get, "/api/v1/song");

        status.Should().Be(HttpStatusCode.OK);
        _queries.Count.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_video_whose_row_names_a_path_outside_the_video_folder_is_a_409_and_touches_nothing()
    {
        var songId = await QueueApiTests.SeedSongAsync(_factory, "Teardrop");
        await EnableAsync();
        Directory.CreateDirectory(VideoRoot);
        var outside = Path.Combine(_factory.DataDir, "elsewhere", "Teardrop.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "x");
        await SeedVideoAsync(songId, outside, MusicVideoStatus.Present);

        var (status, problem) = await SendAsync(HttpMethod.Delete, $"/api/v1/musicvideo/song/{songId}");

        status.Should().Be(HttpStatusCode.Conflict);
        problem!["detail"]!.GetValue<string>().Should().Be("The video's path is outside the music video folder");
        File.Exists(outside).Should().BeTrue();
    }

    private static string RootBody(string root, bool enabled) =>
        new JsonObject { ["enabled"] = enabled, ["rootPath"] = root }.ToJsonString();

    private async Task EnableAsync()
    {
        Directory.CreateDirectory(VideoRoot);

        var (status, body) = await SendAsync(HttpMethod.Put, Settings, RootBody(VideoRoot, enabled: true));

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
    }

    /// <summary>Counts the commands that read or write the music video tables.</summary>
    private sealed class VideoQueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("music_video", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("music_video", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }

            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private async Task SeedVideoAsync(long songId, string? path, MusicVideoStatus status, int? height = null)
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
            Height = height,
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
