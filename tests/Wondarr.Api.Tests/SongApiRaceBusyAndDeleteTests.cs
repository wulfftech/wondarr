using System.Net;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.CoverArt;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// Three song-API paths that used to answer wrongly: two adds of one recording racing each other, a
/// lookup while a metadata provider is busy or refusing, and a delete of a song whose transfer is running.
/// </summary>
public sealed class SongApiRaceBusyAndDeleteTests
{
    private const string SongEndpoint = "/api/v1/song";
    private const string LookupEndpoint = "/api/v1/song/lookup";
    private const string RecordingId = "833f00e1-781f-4edd-90e4-e52712618862";
    private const string LookupBody = """{"term":"Daft Punk - Get Lucky"}""";

    [Fact]
    public async Task A_racing_add_of_the_same_recording_answers_409_with_the_winners_id_like_any_known_song()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(SongApiTests.Identity() with { CoverUrl = null }));

        var covers = Substitute.For<ICoverArtResolver>();
        using var factory = SongApiTests.FakeProviders(resolver: resolver, coverArt: covers);
        using var client = SongApiTests.Authenticated(factory);

        // The racing request saves the recording after this one checked the library and before it writes:
        // with no cover of its own, the song asks the cover archive in between.
        var winnerId = 0L;
        covers.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                winnerId = await SongApiTests.SeedSongAsync(factory, "Get Lucky", RecordingId, monitored: true);

                return (CoverArt?)new CoverArt("https://cover.example/get-lucky.jpg", "coverartarchive");
            });

        using var response = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            SongApiTests.Json($$"""{"mbRecordingId":"{{RecordingId}}"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await SongApiTests.ReadJsonAsync(response);
        problem.GetProperty("title").GetString().Should().Be("Song already exists");
        problem.GetProperty("songId").GetInt64().Should().Be(winnerId);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        (await database.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_busy_musicbrainz_with_deezer_answering_returns_the_deezer_songs_and_the_partial_header()
    {
        var (musicBrainz, deezer) = Providers();
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult { Data = [GetLucky()], Total = 1 });
        musicBrainz.SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Failure("musicbrainz", HttpStatusCode.ServiceUnavailable));

        using var factory = RealResolverFactory(musicBrainz, deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(LookupEndpoint, UriKind.Relative), SongApiTests.Json(LookupBody));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Wondarr-Partial").Should().Equal("musicbrainz");

        var songs = await SongApiTests.ReadJsonAsync(response);
        songs.GetArrayLength().Should().BeGreaterThan(0);
        songs[0].GetProperty("source").GetString().Should().Be("deezer");
    }

    [Fact]
    public async Task A_lookup_while_every_provider_is_busy_is_a_503_problem_with_the_providers_retry_after()
    {
        var (musicBrainz, deezer) = Providers();
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw Failure("deezer", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(20)));
        musicBrainz.SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Failure("musicbrainz", HttpStatusCode.ServiceUnavailable));

        using var factory = RealResolverFactory(musicBrainz, deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(LookupEndpoint, UriKind.Relative), SongApiTests.Json(LookupBody));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(20));

        var problem = await SongApiTests.ReadJsonAsync(response);
        problem.GetProperty("status").GetInt32().Should().Be(503);
        problem.GetProperty("detail").GetString().Should().Contain("did not answer");
    }

    [Fact]
    public async Task A_lookup_the_providers_refuse_is_a_502_problem_and_not_a_500()
    {
        var (musicBrainz, deezer) = Providers();
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerSearchResult>(_ => throw Failure("deezer", HttpStatusCode.Forbidden));
        musicBrainz.SearchRecordingsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbRecordingSearchResult>(_ => throw Failure("musicbrainz", HttpStatusCode.Forbidden));

        using var factory = RealResolverFactory(musicBrainz, deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(new Uri(LookupEndpoint, UriKind.Relative), SongApiTests.Json(LookupBody));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Headers.RetryAfter.Should().BeNull();

        var problem = await SongApiTests.ReadJsonAsync(response);
        problem.GetProperty("title").GetString().Should().EndWith("returned an error");
        problem.GetProperty("status").GetInt32().Should().Be(502);
    }

    [Fact]
    public async Task Deleting_a_song_that_is_downloading_or_importing_is_a_409_and_the_queue_item_is_intact()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var busyId = await SongApiTests.SeedSongAsync(factory, "Get Lucky", RecordingId, monitored: true);
        var finishedId = await SongApiTests.SeedSongAsync(factory, "Instant Crush", "11111111-2222-3333-4444-555555555555", monitored: true);
        await AddQueueItemAsync(factory, busyId, QueueItemState.Importing);
        await AddQueueItemAsync(factory, finishedId, QueueItemState.Imported);

        using var refused = await client.DeleteAsync(new Uri($"{SongEndpoint}/{busyId}", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await SongApiTests.ReadJsonAsync(refused);
        problem.GetProperty("title").GetString().Should().Be("Songs in the queue");
        problem.GetProperty("detail").GetString().Should().Contain("downloading or importing");
        problem.GetProperty("songIds")[0].GetInt64().Should().Be(busyId);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            (await database.Songs.AnyAsync(song => song.Id == busyId)).Should().BeTrue();
            (await database.QueueItems.SingleAsync(item => item.SongId == busyId)).State.Should().Be(QueueItemState.Importing);
        }

        using var deleted = await client.DeleteAsync(new Uri($"{SongEndpoint}/{finishedId}", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static (IMusicBrainzClient MusicBrainz, IDeezerClient Deezer) Providers() =>
        (Substitute.For<IMusicBrainzClient>(), Substitute.For<IDeezerClient>());

    /// <summary>A factory that runs the real identity resolver over fake providers.</summary>
    private static WondarrAppFactory RealResolverFactory(IMusicBrainzClient musicBrainz, IDeezerClient deezer) =>
        SongApiTests.FakeProviders(
            deezer: deezer,
            musicBrainz: musicBrainz,
            configure: services =>
            {
                services.RemoveAll<IIdentityResolver>();
                services.AddTransient<IIdentityResolver, IdentityResolver>();
            });

    private static MetadataProviderException Failure(string provider, HttpStatusCode status, TimeSpan? retryAfter = null) =>
        new(provider, status, $"{provider} answered {(int)status} for a GET request.", retryAfter);

    private static DeezerTrack GetLucky() => new()
    {
        Id = 67238735,
        Title = "Get Lucky",
        TitleShort = "Get Lucky",
        Duration = 369,
        Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
        Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
        Contributors = [new DeezerArtist { Id = 27, Name = "Daft Punk" }],
    };

    private static async Task AddQueueItemAsync(WondarrAppFactory factory, long songId, QueueItemState state)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
        var now = DateTime.UtcNow;
        var run = new SearchRun { SongId = songId, StartedAt = now };
        var candidate = new CandidateRecord
        {
            SearchRun = run,
            SongId = songId,
            SourceType = "soulseek",
            BlocklistKey = "user/path/" + songId,
            DisplayName = "file.flac",
            RemotePath = "path/file.flac",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        context.QueueItems.Add(new QueueItem
        {
            SongId = songId,
            CandidateId = candidate.Id,
            SearchRunId = run.Id,
            SourceType = "soulseek",
            Destination = "wondarr/" + songId,
            State = state,
            StateChangedAt = now,
            LastProgressAt = now,
        });
        await context.SaveChangesAsync();
    }
}
