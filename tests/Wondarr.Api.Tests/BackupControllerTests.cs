using System.Net;
using System.Text.Json;
using Wondarr.Core.Backup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The System → Backup endpoints against a real host: a backup made through the API can be listed,
/// downloaded and deleted, and a restore — stored or uploaded — is staged and answers
/// <c>restartRequired</c> without stopping the test host (the shutdown is stubbed out).
/// </summary>
public sealed class BackupControllerTests
{
    private const string BackupEndpoint = "/api/v1/system/backup";

    [Fact]
    public async Task Backups_without_a_key_are_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(BackupEndpoint, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_manual_backup_can_be_listed_downloaded_and_deleted()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var created = await client.PostAsync(new Uri(BackupEndpoint, UriKind.Relative), content: null);
        var backup = await ReadResourceAsync(created);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        backup.Id.Should().StartWith("wondarr_backup_v");
        backup.Type.Should().Be("manual");
        backup.Path.Should().Be($"/backup/manual/{backup.Name}");
        backup.Size.Should().BeGreaterThan(0);

        using (var list = await client.GetAsync(new Uri(BackupEndpoint, UriKind.Relative)))
        {
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            var ids = await ReadIdsAsync(list);
            ids.Should().Contain(backup.Id);
        }

        using (var download = await client.GetAsync(new Uri($"{BackupEndpoint}/{backup.Id}/download", UriKind.Relative)))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            download.Content.Headers.ContentDisposition!.FileName.Should().Be(backup.Name);
            (await download.Content.ReadAsByteArrayAsync()).Should().HaveCount((int)backup.Size);
        }

        using (var deleted = await client.DeleteAsync(new Uri($"{BackupEndpoint}/{backup.Id}", UriKind.Relative)))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var list = await client.GetAsync(new Uri(BackupEndpoint, UriKind.Relative)))
        {
            (await ReadIdsAsync(list)).Should().NotContain(backup.Id);
        }
    }

    [Fact]
    public async Task An_unknown_backup_is_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var deleted = await client.DeleteAsync(new Uri($"{BackupEndpoint}/../secrets.zip", UriKind.Relative));
        using var downloaded = await client.GetAsync(new Uri($"{BackupEndpoint}/notes.txt/download", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        downloaded.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restoring_a_stored_backup_stages_it_and_answers_restart_required()
    {
        var shutdown = Substitute.For<IApplicationShutdown>();
        using var factory = new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<IApplicationShutdown>();
            services.AddSingleton(shutdown);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var created = await client.PostAsync(new Uri(BackupEndpoint, UriKind.Relative), content: null);
        var backup = await ReadResourceAsync(created);

        using var restored = await client.PostAsync(
            new Uri($"{BackupEndpoint}/restore/{backup.Id}", UriKind.Relative), content: null);
        var body = await ReadRestoreAsync(restored);

        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().BeTrue();
        TheStagedFilesShouldExist(factory.ConfigDir);
        shutdown.Received(1).StopAfterResponse();
    }

    [Fact]
    public async Task Uploading_an_invalid_backup_answers_400_and_stages_nothing()
    {
        var shutdown = Substitute.For<IApplicationShutdown>();
        using var factory = new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<IApplicationShutdown>();
            services.AddSingleton(shutdown);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var restored = await client.PostAsync(
            new Uri($"{BackupEndpoint}/restore/upload", UriKind.Relative),
            ZipUpload("not a zip at all"));

        var failureBody = await restored.Content.ReadAsStringAsync();
        restored.StatusCode.Should().Be(HttpStatusCode.BadRequest, failureBody);
        (await restored.Content.ReadAsStringAsync()).Should().Contain("backup");
        Directory.Exists(Path.Combine(factory.ConfigDir, "restore")).Should().BeFalse();
        shutdown.DidNotReceive().StopAfterResponse();
    }

    [Fact]
    public async Task Uploading_a_valid_backup_stages_it_and_answers_restart_required()
    {
        var shutdown = Substitute.For<IApplicationShutdown>();
        using var factory = new WondarrAppFactory(configureServices: services =>
        {
            services.RemoveAll<IApplicationShutdown>();
            services.AddSingleton(shutdown);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        // A backup of this very instance, downloaded and uploaded back: the round trip the
        // "restore into a fresh instance" flow uses.
        using var created = await client.PostAsync(new Uri(BackupEndpoint, UriKind.Relative), content: null);
        var backup = await ReadResourceAsync(created);

        using var download = await client.GetAsync(new Uri($"{BackupEndpoint}/{backup.Id}/download", UriKind.Relative));
        using var zip = new MemoryStream(await download.Content.ReadAsByteArrayAsync());

        using var restored = await client.PostAsync(
            new Uri($"{BackupEndpoint}/restore/upload", UriKind.Relative),
            ZipUpload(zip));
        var body = await ReadRestoreAsync(restored);

        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().BeTrue();
        TheStagedFilesShouldExist(factory.ConfigDir);
        shutdown.Received(1).StopAfterResponse();
    }

    private static void TheStagedFilesShouldExist(string configDir)
    {
        var restore = Path.Combine(configDir, "restore");

        File.Exists(Path.Combine(restore, "wondarr.db")).Should().BeTrue();
        File.Exists(Path.Combine(restore, "config.yml")).Should().BeTrue();
    }

    private static MultipartFormDataContent ZipUpload(Stream content)
    {
        var form = new MultipartFormDataContent
        {
            { new StreamContent(content), "file", "backup.zip" },
        };

        return form;
    }

    private static MultipartFormDataContent ZipUpload(string content) =>
        ZipUpload(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

    private static async Task<BackupResourceShape> ReadResourceAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new BackupResourceShape(
            document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("name").GetString()!,
            document.RootElement.GetProperty("path").GetString()!,
            document.RootElement.GetProperty("type").GetString()!,
            document.RootElement.GetProperty("size").GetInt64(),
            document.RootElement.GetProperty("time").GetDateTime());
    }

    private static async Task<List<string>> ReadIdsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.EnumerateArray()
            .Select(backup => backup.GetProperty("id").GetString()!)
            .ToList();
    }

    private static async Task<bool> ReadRestoreAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("restartRequired").GetBoolean();
    }

    private sealed record BackupResourceShape(
        string Id,
        string Name,
        string Path,
        string Type,
        long Size,
        DateTime Time);
}
