using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.Organizer;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The marker file at the top of a library root: <c>rootStatus</c> on every read, the confirm endpoint
/// and the marker a created library gets. These run against the real <see cref="LibraryRoots"/> over a
/// temporary media root, so nothing is written to <c>/data</c>.
/// </summary>
public sealed class LibraryRootApiTests : IDisposable
{
    private readonly string _media = Path.Combine(Path.GetTempPath(), "wondarr-api-media-" + Guid.NewGuid().ToString("N"));
    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;

    public LibraryRootApiTests()
    {
        Directory.CreateDirectory(_media);

        _factory = new WondarrAppFactory(
            new Dictionary<string, string?> { ["MediaRoot"] = _media },
            services =>
            {
                services.RemoveAll<ILibraryRoots>();
                services.AddSingleton<ILibraryRoots, LibraryRoots>();
            });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
    }

    private string Root => Path.Combine(_media, "music");

    private string Marker => Path.Combine(Root, LibraryRoots.MarkerFileName);

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();

        try
        {
            Directory.Delete(_media, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> RootStatusAsync()
    {
        using var response = await _client.GetAsync(new Uri("/api/v1/library/1", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["rootStatus"]!.GetValue<string>();
    }

    [Fact]
    public async Task The_seeded_library_is_marked_at_startup_and_reads_as_ready()
    {
        File.Exists(Marker).Should().BeTrue("a library with no files is adopted when the host starts");

        (await RootStatusAsync()).Should().Be("ready");
    }

    [Fact]
    public async Task The_status_is_checked_on_every_read_and_the_list_carries_it_too()
    {
        File.Delete(Marker);

        (await RootStatusAsync()).Should().Be("unmarked");

        using var list = await _client.GetAsync(new Uri("/api/v1/library", UriKind.Relative));
        ((JsonArray)JsonNode.Parse(await list.Content.ReadAsStringAsync())!)[0]!["rootStatus"]!.GetValue<string>().Should().Be("unmarked");

        Directory.Delete(Root, recursive: true);

        (await RootStatusAsync()).Should().Be("missing");
    }

    [Fact]
    public async Task Confirming_an_unmarked_root_writes_the_marker_and_returns_the_library()
    {
        File.Delete(Marker);

        using var response = await _client.PostAsync(new Uri("/api/v1/library/1/root/confirm", UriKind.Relative), content: null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body["id"]!.GetValue<long>().Should().Be(1);
        body["rootStatus"]!.GetValue<string>().Should().Be("ready");
        File.Exists(Marker).Should().BeTrue();
    }

    [Fact]
    public async Task Confirming_a_root_that_does_not_exist_is_a_bad_request_naming_the_root_and_creates_nothing()
    {
        Directory.Delete(Root, recursive: true);

        using var response = await _client.PostAsync(new Uri("/api/v1/library/1/root/confirm", UriKind.Relative), content: null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body["errors"]!["rootPath"]!.AsArray().Should().NotBeEmpty();
        Directory.Exists(Root).Should().BeFalse();
    }

    [Fact]
    public async Task Confirming_an_unknown_library_is_not_found()
    {
        using var response = await _client.PostAsync(new Uri("/api/v1/library/987654/root/confirm", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_library_writes_the_marker_into_its_root()
    {
        var root = Path.Combine(_media, "archive");
        var library = new JsonObject
        {
            ["name"] = "Archive",
            ["rootPath"] = root,
            ["layout"] = "artistAlbum",
            ["namingTemplate"] = "{Album Artist Name}/{Album Title}/{track:00} - {Track Title}",
            ["sidecarOptions"] = new JsonObject(),
            ["outputPolicy"] = null,
            ["albumPolicy"] = "originalAlbum",
            ["minTracksPerRealAlbum"] = 2,
            ["plexSectionId"] = null,
            ["plexLibraryPath"] = null,
            ["isDefault"] = false,
        };

        using var response = await _client.PostAsync(
            new Uri("/api/v1/library", UriKind.Relative),
            new StringContent(library.ToJsonString(), Encoding.UTF8, "application/json"));
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        created["rootStatus"]!.GetValue<string>().Should().Be("ready");
        File.Exists(Path.Combine(root, LibraryRoots.MarkerFileName)).Should().BeTrue();
    }
}
