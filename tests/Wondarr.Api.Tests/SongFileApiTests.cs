using System.Net;
using FluentAssertions;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary><c>DELETE /api/v1/song/{id}/file</c>: the route and its refusals. The removal itself is covered in Core.</summary>
public sealed class SongFileApiTests
{
    [Fact]
    public async Task Removing_the_file_of_an_unknown_song_is_not_found()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        using var response = await client.DeleteAsync(new Uri("/api/v1/song/987654/file?blocklist=true", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Removing_a_file_without_a_key_is_unauthorized()
    {
        using var factory = new WondarrAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync(new Uri("/api/v1/song/1/file", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
