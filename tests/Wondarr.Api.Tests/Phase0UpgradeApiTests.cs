using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Starts the real host on a config directory that holds only a database written by the Phase 0
/// image, named <c>compilarr.db</c> as it was then: the host adopts it, migrates it and serves it.
/// </summary>
public sealed class Phase0UpgradeApiTests
{
    [Fact]
    public async Task The_host_adopts_and_serves_a_phase_0_database()
    {
        using var factory = new WondarrAppFactory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "migrations", "phase0-compilarr.db");
        File.Copy(fixture, Path.Combine(factory.ConfigDir, "compilarr.db"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var status = await client.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));
        status.StatusCode.Should().Be(HttpStatusCode.OK);

        File.Exists(Path.Combine(factory.ConfigDir, "wondarr.db")).Should().BeTrue();
        File.Exists(Path.Combine(factory.ConfigDir, "compilarr.db")).Should().BeFalse();

        using var tasks = await client.GetAsync(new Uri("/api/v1/system/task", UriKind.Relative));
        tasks.StatusCode.Should().Be(HttpStatusCode.OK);
        using var taskDocument = JsonDocument.Parse(await tasks.Content.ReadAsStringAsync());
        var taskNames = taskDocument.RootElement.EnumerateArray()
            .Select(task => task.GetProperty("taskName").GetString())
            .ToList();
        taskNames.Should().ContainSingle(name => name == "Heartbeat");
        taskNames.Should().ContainSingle(name => name == "CheckHealth");
        taskNames.Should().OnlyHaveUniqueItems("the job sync must not duplicate the Phase 0 rows");

        using var profiles = await client.GetAsync(new Uri("/api/v1/qualityprofile", UriKind.Relative));
        profiles.StatusCode.Should().Be(HttpStatusCode.OK);
        using var profileDocument = JsonDocument.Parse(await profiles.Content.ReadAsStringAsync());
        profileDocument.RootElement.EnumerateArray()
            .Select(profile => profile.GetProperty("name").GetString())
            .Should().BeEquivalentTo("Standard 320", "Lossless");
    }
}
