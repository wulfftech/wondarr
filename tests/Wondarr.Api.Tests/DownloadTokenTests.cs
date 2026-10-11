using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wondarr.Api.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Downloads without the API key in the URL: a token good for 60 seconds and one path, accepted only
/// by the backup and log file downloads.
/// </summary>
public sealed class DownloadTokenTests
{
    private const string BackupPath = "/api/v1/system/backup/wondarr_backup_a.zip/download";
    private const string LogPath = "/api/v1/log/file/wondarr-1.json";

    [Fact]
    public void A_fresh_token_is_valid_for_its_own_path()
    {
        var service = new DownloadTokenService(new ManualTimeProvider());

        service.IsValid(service.Mint(BackupPath), BackupPath).Should().BeTrue();
    }

    [Fact]
    public void A_token_expires_after_sixty_seconds()
    {
        var time = new ManualTimeProvider();
        var service = new DownloadTokenService(time);
        var token = service.Mint(BackupPath);

        time.Advance(TimeSpan.FromSeconds(59));
        service.IsValid(token, BackupPath).Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(2));
        service.IsValid(token, BackupPath).Should().BeFalse();
    }

    [Fact]
    public void A_token_is_refused_on_another_path()
    {
        var service = new DownloadTokenService(new ManualTimeProvider());
        var token = service.Mint(BackupPath);

        service.IsValid(token, "/api/v1/system/backup/other.zip/download").Should().BeFalse();
        service.IsValid(token, LogPath).Should().BeFalse();
    }

    [Fact]
    public void A_tampered_token_is_refused()
    {
        var service = new DownloadTokenService(new ManualTimeProvider());
        var token = service.Mint(BackupPath);
        var dot = token.IndexOf('.', StringComparison.Ordinal);

        // A later expiry with the old signature, a flipped signature, and garbage.
        service.IsValid($"{long.Parse(token[..dot], System.Globalization.CultureInfo.InvariantCulture) + 600}{token[dot..]}", BackupPath).Should().BeFalse();
        service.IsValid(token[..^2] + (token[^1] == 'A' ? "BB" : "AA"), BackupPath).Should().BeFalse();
        service.IsValid("not-a-token", BackupPath).Should().BeFalse();
        service.IsValid(string.Empty, BackupPath).Should().BeFalse();
        service.IsValid(null, BackupPath).Should().BeFalse();
    }

    [Fact]
    public void A_token_from_another_process_is_refused()
    {
        var time = new ManualTimeProvider();
        var token = new DownloadTokenService(time).Mint(BackupPath);

        new DownloadTokenService(time).IsValid(token, BackupPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/system/backup/x.zip/download", true)]
    [InlineData("/api/v1/log/file/x.json", true)]
    [InlineData("/api/v1/system/backup", false)]
    [InlineData("/api/v1/system/backup/restore/x.zip", false)]
    [InlineData("/api/v1/system/status", false)]
    [InlineData("/api/v1/log", false)]
    [InlineData("/initialize.json", false)]
    [InlineData("", false)]
    public void Only_the_two_download_paths_can_be_minted(string path, bool expected) =>
        DownloadTokenService.IsDownloadPath(path).Should().Be(expected);

    [Fact]
    public async Task A_valid_token_downloads_a_backup_without_the_key()
    {
        using var factory = new WondarrAppFactory();
        var id = await MakeBackupAsync(factory);
        var path = $"/api/v1/system/backup/{id}/download";
        var token = await MintAsync(factory, path);

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(new Uri($"{path}?token={Uri.EscapeDataString(token)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
    }

    [Fact]
    public async Task A_token_for_one_backup_does_not_download_another_path()
    {
        using var factory = new WondarrAppFactory();
        var id = await MakeBackupAsync(factory);
        var token = await MintAsync(factory, $"/api/v1/system/backup/{id}/download");

        using var anonymous = factory.CreateClient();

        foreach (var other in new[] { "/api/v1/system/backup/other.zip/download", "/api/v1/system/backup", "/api/v1/system/status", "/api/v1/log" })
        {
            using var response = await anonymous.GetAsync(new Uri($"{other}?token={Uri.EscapeDataString(token)}", UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, other);
        }
    }

    [Fact]
    public async Task A_tampered_token_does_not_download()
    {
        using var factory = new WondarrAppFactory();
        var id = await MakeBackupAsync(factory);
        var path = $"/api/v1/system/backup/{id}/download";
        var token = await MintAsync(factory, path);

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(new Uri($"{path}?token={Uri.EscapeDataString(token + "x")}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_does_not_authorise_a_post_to_the_same_path()
    {
        using var factory = new WondarrAppFactory();
        var id = await MakeBackupAsync(factory);
        var path = $"/api/v1/system/backup/{id}/download";
        var token = await MintAsync(factory, path);

        // No redirects: a refused POST may bounce to the login page, and that page's 200 would hide it.
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await anonymous.PostAsync(new Uri($"{path}?token={Uri.EscapeDataString(token)}", UriKind.Relative), content: null);

        response.IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_downloads_a_log_file()
    {
        using var factory = new WondarrAppFactory();
        var directory = Path.Combine(factory.ConfigDir, "logs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "wondarr-test-token.json"), "{}\n");

        var token = await MintAsync(factory, "/api/v1/log/file/wondarr-test-token.json");

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(
            new Uri($"/api/v1/log/file/wondarr-test-token.json?token={Uri.EscapeDataString(token)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Minting_needs_the_key_and_refuses_other_paths()
    {
        using var factory = new WondarrAppFactory();
        using var anonymous = factory.CreateClient();
        using var unauthorised = await anonymous.PostAsJsonAsync(new Uri("/api/v1/system/download-token", UriKind.Relative), new { path = BackupPath });

        unauthorised.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        using var refused = await client.PostAsJsonAsync(new Uri("/api/v1/system/download-token", UriKind.Relative), new { path = "/api/v1/system/status" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_api_key_query_parameter_still_works_for_api_clients()
    {
        using var factory = new WondarrAppFactory();
        var id = await MakeBackupAsync(factory);

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(
            new Uri($"/api/v1/system/backup/{id}/download?apikey={factory.ApiKey}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string> MakeBackupAsync(WondarrAppFactory factory)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        using var created = await client.PostAsync(new Uri("/api/v1/system/backup", UriKind.Relative), content: null);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> MintAsync(WondarrAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/system/download-token", UriKind.Relative), new { path });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("expiresInSeconds").GetInt32().Should().Be(60);

        return body.GetProperty("token").GetString()!;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
