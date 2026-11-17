using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.Domain;
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

    private readonly WondarrAppFactory _factory = new(
        configureServices: services =>
        {
            services.RemoveAll<ILibraryRoots>();
            services.AddSingleton<ILibraryRoots, LibraryRoots>();
        });

    private readonly HttpClient _client;

    public MusicVideoApiTests()
    {
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
        body["rootPath"].Should().BeNull();
        body["layout"]!.GetValue<string>().Should().Be("folder");
        body["namingTemplate"]!.GetValue<string>().Should().Be("{Artist}/{Artist} - {Title}");
        body["besideTrackSuffix"]!.GetValue<string>().Should().Be("-video");
        body["maxHeight"].Should().BeNull();
        body["writeNfo"]!.GetValue<bool>().Should().BeTrue();
        body["writeThumbnail"]!.GetValue<bool>().Should().BeTrue();
        body["scheduledSearchLimit"]!.GetValue<int>().Should().Be(25);
        body["readOnlyFields"]!.AsArray().Should().BeEmpty();
        body["rootStatus"].Should().BeNull();
        body["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeFalse();
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
        stored!["rootPath"].Should().BeNull("a refused change writes nothing");
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
    public async Task Saving_a_folder_with_files_leaves_it_to_be_confirmed_and_confirm_marks_it()
    {
        Directory.CreateDirectory(VideoRoot);
        await File.WriteAllTextAsync(Path.Combine(VideoRoot, "old.mkv"), "x");

        var (_, saved) = await SendAsync(HttpMethod.Put, Settings, RootBody(VideoRoot, enabled: true));

        saved!["rootStatus"]!.GetValue<string>().Should().Be("unmarked");
        saved["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(Path.Combine(VideoRoot, LibraryRoots.MarkerFileName)).Should().BeFalse();

        var (status, confirmed) = await SendAsync(HttpMethod.Post, $"{Settings}/root/confirm");

        status.Should().Be(HttpStatusCode.OK);
        confirmed!["rootStatus"]!.GetValue<string>().Should().Be("ready");
        confirmed["rootNeedsConfirmation"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(Path.Combine(VideoRoot, LibraryRoots.MarkerFileName)).Should().BeTrue();
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
        await EnableAsync();
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

    private static string RootBody(string root, bool enabled) =>
        new JsonObject { ["enabled"] = enabled, ["rootPath"] = root }.ToJsonString();

    private async Task EnableAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, Settings, """{"enabled":true}""");

        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
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
