using Wondarr.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

public sealed class MusicBrainzIntervalTests
{
    [Theory]
    [InlineData("https://musicbrainz.org/ws/2/", 0, 1000)]
    [InlineData("https://beta.musicbrainz.org/ws/2/", 10, 1000)]
    [InlineData("http://mb-mirror.lan:5000/ws/2/", 50, 50)]
    [InlineData("http://localhost:18099/mb/ws/2/", 0, 0)]
    [InlineData("http://localhost:18099/mb/ws/2/", null, 1000)]
    public void The_public_service_always_gets_one_request_per_second(string baseUrl, int? mirrorMs, int expectedMs)
    {
        var options = new MetadataOptions { MusicBrainzBaseUrl = baseUrl, MusicBrainzMirrorIntervalMs = mirrorMs };

        options.MusicBrainzRequestInterval.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }
}
