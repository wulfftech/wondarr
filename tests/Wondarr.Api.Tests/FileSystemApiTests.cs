using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wondarr.Core.MusicVideos;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>The folder picker's endpoints: the roots, a listing, a new folder, and what they refuse.</summary>
public sealed class FileSystemApiTests : IDisposable
{
    private const string Browse = "/api/v1/filesystem";

    private readonly string _mount = Path.Combine(Path.GetTempPath(), "wondarr-fs-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _release = new();
    private readonly WondarrAppFactory _factory;
    private readonly HttpClient _client;
    private bool _hang;

    public FileSystemApiTests()
    {
        Directory.CreateDirectory(Path.Combine(_mount, "Music"));
        File.WriteAllText(Path.Combine(_mount, "file.txt"), "x");
        _factory = new WondarrAppFactory(
            configureServices: services =>
            {
                services.RemoveAll<ContainerMounts>();
                services.AddSingleton(new ContainerMounts(
                    _ => true,
                    () => ["1 0 0:50 / / rw - overlay overlay rw", $"2 1 0:60 / {_mount} rw - cifs //nas/x rw"],
                    directoryExists: _ =>
                    {
                        if (_hang)
                        {
                            _release.Wait(TimeSpan.FromSeconds(30));
                        }

                        return true;
                    }));
            });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
    }

    public void Dispose()
    {
        _release.Set();
        _client.Dispose();
        _factory.Dispose();
        _release.Dispose();
        Directory.Delete(_mount, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_endpoints_need_the_api_key()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync(new Uri(Browse, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_no_path_only_the_roots_come_back()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (status, body) = await SendAsync(HttpMethod.Get, Browse);

        status.Should().Be(HttpStatusCode.OK);
        body!["path"].Should().BeNull();
        body["folders"]!.AsArray().Should().BeEmpty();
        body["roots"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal(_mount);
    }

    [Fact]
    public async Task A_folder_lists_its_sub_folders_and_not_its_files()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (status, body) = await SendAsync(HttpMethod.Get, $"{Browse}?path={Uri.EscapeDataString(_mount)}");

        status.Should().Be(HttpStatusCode.OK);
        body!["folders"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).Should().Equal("Music");
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/etc")]
    [InlineData("MOUNT/../../etc")]
    public async Task A_path_that_is_relative_outside_a_root_or_climbs_is_a_400(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (status, _) = await SendAsync(HttpMethod.Get, $"{Browse}?path={Uri.EscapeDataString(path.Replace("MOUNT", _mount, StringComparison.Ordinal))}");

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_hung_folder_answers_504_and_not_a_hang()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _hang = true;

        var (status, _) = await SendAsync(HttpMethod.Get, $"{Browse}?path={Uri.EscapeDataString(_mount)}");

        status.Should().Be(HttpStatusCode.GatewayTimeout);
    }

    [Fact]
    public async Task A_new_folder_is_made_then_refused_as_a_duplicate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var body = new JsonObject { ["parent"] = _mount, ["name"] = "Videos" }.ToJsonString();

        var (created, folder) = await SendAsync(HttpMethod.Post, $"{Browse}/folder", body);
        var (again, _) = await SendAsync(HttpMethod.Post, $"{Browse}/folder", body);

        created.Should().Be(HttpStatusCode.OK);
        folder!["path"]!.GetValue<string>().Should().Be(Path.Combine(_mount, "Videos"));
        Directory.Exists(Path.Combine(_mount, "Videos")).Should().BeTrue();
        again.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public async Task A_name_that_is_not_one_plain_segment_is_a_400(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var body = new JsonObject { ["parent"] = _mount, ["name"] = name }.ToJsonString();

        var (status, _) = await SendAsync(HttpMethod.Post, $"{Browse}/folder", body);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_new_folder_outside_a_root_is_a_400()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var body = new JsonObject { ["parent"] = "/tmp", ["name"] = "Videos" }.ToJsonString();

        var (status, _) = await SendAsync(HttpMethod.Post, $"{Browse}/folder", body);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }
}
