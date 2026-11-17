using System.Net;
using System.Text;
using FluentAssertions;
using NSubstitute;
using Wondarr.Core.Discover;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Tests.Lyrics;
using Xunit;

namespace Wondarr.Core.Tests.Discover;

/// <summary>Deezer's artist radio: the client call, and the source that turns it into suggestions.</summary>
public sealed class DeezerRecommendationTests
{
    /// <summary>The shape of <c>artist/{id}/radio</c>, cut down: no ISRC, a medium cover, a duration in seconds.</summary>
    private const string RadioBody =
        """{"data":[{"id":111,"title":"Roads","duration":304,"preview":"https://cdn.example/p.mp3","artist":{"id":5,"name":"Portishead"},"album":{"id":9,"title":"Dummy","cover_medium":"https://cdn.example/dummy-250.jpg"}},{"id":112,"title":"Glory Box","duration":300,"artist":{"id":5,"name":"Portishead"},"album":{"id":9,"title":"Dummy"}}],"total":2}""";

    [Fact]
    public async Task The_client_reads_the_artist_radio_by_artist_id()
    {
        var handler = StubHttpMessageHandler.Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(RadioBody, Encoding.UTF8, "application/json"),
        });

        var client = new DeezerClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/") },
            new DictionaryCache());

        var page = await client.GetArtistRadioAsync(27);

        handler.Requests.Should().ContainSingle().Which.AbsolutePath.Should().Be("/artist/27/radio");
        page!.Data.Select(track => track.Title).Should().Equal("Roads", "Glory Box");
        page.Data[0].Id.Should().Be(111);
        page.Data[0].Duration.Should().Be(304);
        page.Data[0].Artist.Name.Should().Be("Portishead");
        page.Data[0].Album.CoverMedium.Should().Be("https://cdn.example/dummy-250.jpg");
    }

    [Fact]
    public async Task The_source_reads_the_seed_artist_from_its_track_then_asks_for_the_radio()
    {
        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetTrackAsync(42, Arg.Any<CancellationToken>())
            .Returns(new DeezerTrack { Id = 42, Title = "Teardrop", Artist = new DeezerArtist { Id = 5, Name = "Massive Attack" } });
        deezer.GetArtistRadioAsync(5, Arg.Any<CancellationToken>())
            .Returns(new DeezerTrackPage
            {
                Data =
                [
                    new DeezerTrack
                    {
                        Id = 111,
                        Title = "Roads",
                        Duration = 304,
                        Artist = new DeezerArtist { Id = 6, Name = "Portishead" },
                        Album = new DeezerAlbumRef { Id = 9, Title = "Dummy", CoverMedium = "https://cdn.example/dummy-250.jpg" },
                    },
                ],
            });

        var source = new DeezerRecommendationSource(deezer);
        var result = await source.GetAsync(new RecommendationSeed(1, "Teardrop", "Massive Attack", null, 42), CancellationToken.None);

        source.Weight.Should().Be(0.6);
        result.Status.Should().Be(RecommendationSourceStatus.Answered);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.Title.Should().Be("Roads");
        candidate.Artist.Should().Be("Portishead");
        candidate.Album.Should().Be("Dummy");
        candidate.DeezerId.Should().Be(111);
        candidate.CoverUrl.Should().Be("https://cdn.example/dummy-250.jpg");
        candidate.DurationMs.Should().Be(304_000);
    }

    [Fact]
    public async Task The_source_does_nothing_for_a_seed_without_a_deezer_id()
    {
        var deezer = Substitute.For<IDeezerClient>();

        var result = await new DeezerRecommendationSource(deezer)
            .GetAsync(new RecommendationSeed(1, "Teardrop", "Massive Attack", "f3bba4cd-8018-468b-902e-bc8f029593e5", null), CancellationToken.None);

        result.Status.Should().Be(RecommendationSourceStatus.Skipped);
        await deezer.DidNotReceiveWithAnyArgs().GetTrackAsync(0, CancellationToken.None);
        await deezer.DidNotReceiveWithAnyArgs().GetArtistRadioAsync(0, CancellationToken.None);
    }

    [Fact]
    public async Task A_deezer_failure_is_reported_failed_not_thrown()
    {
        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetTrackAsync(42, Arg.Any<CancellationToken>())
            .Returns<DeezerTrack?>(_ => throw new MetadataProviderException(
                "deezer",
                HttpStatusCode.TooManyRequests,
                "Deezer answered 429."));

        var result = await new DeezerRecommendationSource(deezer)
            .GetAsync(new RecommendationSeed(1, "Teardrop", "Massive Attack", null, 42), CancellationToken.None);

        result.Status.Should().Be(RecommendationSourceStatus.Failed);
    }

    private sealed class DictionaryCache : IMetadataCache
    {
        private readonly Dictionary<string, string> _entries = [];

        public Task<string?> GetAsync(string provider, string key, CancellationToken cancellationToken) =>
            Task.FromResult(_entries.TryGetValue(provider + "|" + key, out var value) ? value : null);

        public Task SetAsync(string provider, string key, string payload, TimeSpan ttl, CancellationToken cancellationToken)
        {
            _entries[provider + "|" + key] = payload;

            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
