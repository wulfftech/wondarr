using System.Net;
using System.Text;
using System.Text.Json;
using Compilarr.Core.Domain;
using Compilarr.Core.Identity;
using Compilarr.Core.Metadata.CoverArt;
using Compilarr.Core.Metadata.Deezer;
using Compilarr.Core.Metadata.MusicBrainz;
using Compilarr.Core.Organizer;
using Compilarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// The song pipeline over HTTP: the add dialog's lookup, the id-based add, the list/edit/delete
/// round trip and the album picker. Every provider is faked, so no test reaches the network.
/// </summary>
public sealed class SongApiTests
{
    private const string SongEndpoint = "/api/v1/song";
    private const string LookupEndpoint = "/api/v1/song/lookup";

    /// <summary>The recording the fakes resolve to; the same one P1-07's acceptance criteria name.</summary>
    private const string RecordingId = "833f00e1-781f-4edd-90e4-e52712618862";

    /// <summary>A second recording, already in the library.</summary>
    private const string StoredRecordingId = "11111111-2222-3333-4444-555555555555";

    private const string ReleaseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ReleaseGroupId = "0f0f0f0f-1e1e-2d2d-3c3c-4b4b4b4b4b4b";
    private const string CoverUrl = "https://cover.example/get-lucky.jpg";

    [Fact]
    public async Task Lookup_returns_every_candidate_and_names_the_one_already_in_the_library()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SongCandidate>>([
                Candidate("22222222-3333-4444-5555-666666666666", "Get Lucky (Radio Edit)", 96.5),
                Candidate(StoredRecordingId, "Get Lucky", 99.0),
            ]));

        using var factory = FakeProviders(resolver: resolver);
        using var client = Authenticated(factory);
        var storedId = await SeedSongAsync(factory, "Get Lucky", StoredRecordingId, monitored: true);

        using var response = await client.PostAsync(
            new Uri(LookupEndpoint, UriKind.Relative),
            Json("""{"term":"Daft Punk - Get Lucky"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var candidates = await ReadJsonAsync(response);
        candidates.GetArrayLength().Should().Be(2);
        candidates[0].GetProperty("title").GetString().Should().Be("Get Lucky (Radio Edit)");
        candidates[0].GetProperty("existingSongId").ValueKind.Should().Be(JsonValueKind.Null);
        candidates[0].GetProperty("source").GetString().Should().Be("musicbrainz");
        candidates[0].GetProperty("score").GetDouble().Should().BeApproximately(96.5, 0.001);

        candidates[1].GetProperty("existingSongId").GetInt64().Should().Be(storedId);
        candidates[1].GetProperty("isrcs")[0].GetString().Should().Be("GBDUW1300040");

        // The resolver was asked for 20 candidates, as the task specifies.
        await resolver.Received(1).SearchAsync("Daft Punk - Get Lucky", 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lookup_rejects_an_empty_term_and_an_unsupported_link()
    {
        using var factory = FakeProviders();
        using var client = Authenticated(factory);

        using var empty = await client.PostAsync(new Uri(LookupEndpoint, UriKind.Relative), Json("""{"term":""}"""));
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var tooLong = await client.PostAsync(
            new Uri(LookupEndpoint, UriKind.Relative),
            Json(JsonSerializer.Serialize(new { term = new string('x', 501) })));

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var spotify = await client.PostAsync(
            new Uri(LookupEndpoint, UriKind.Relative),
            Json("""{"term":"https://open.spotify.com/track/x"}"""));

        spotify.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        spotify.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var problem = await ReadJsonAsync(spotify);
        problem.GetProperty("detail").GetString().Should().Contain("CSV");
    }

    [Fact]
    public async Task Adding_by_mb_recording_id_returns_201_then_409_then_404()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(Identity()));

        using var factory = FakeProviders(resolver: resolver);
        using var client = Authenticated(factory);

        using var created = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            Json("""{"mbRecordingId":"833f00e1-781f-4edd-90e4-e52712618862"}"""));

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var song = await ReadJsonAsync(created);
        var id = song.GetProperty("id").GetInt64();

        created.Headers.Location.Should().NotBeNull();
        created.Headers.Location!.ToString().Should().EndWith($"/api/v1/song/{id}");
        song.GetProperty("title").GetString().Should().Be("Get Lucky");
        song.GetProperty("artistCredit").GetString().Should().Be("Daft Punk feat. Pharrell Williams");
        song.GetProperty("addedBy").GetString().Should().Be("api");
        song.GetProperty("monitored").GetBoolean().Should().BeTrue();
        song.GetProperty("mbRecordingId").GetString().Should().Be(RecordingId);
        song.GetProperty("albumContext").ValueKind.Should().NotBe(JsonValueKind.Null);
        song.GetProperty("albumContext").GetProperty("albumTitle").GetString().Should().NotBeNullOrWhiteSpace();
        song.GetProperty("albumContext").GetProperty("coverUrl").GetString().Should().Be(CoverUrl);

        using var again = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            Json("""{"mbRecordingId":"833f00e1-781f-4edd-90e4-e52712618862"}"""));

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var conflict = await ReadJsonAsync(again);
        conflict.GetProperty("songId").GetInt64().Should().Be(id);

        using var neither = await client.PostAsync(new Uri(SongEndpoint, UriKind.Relative), Json("{}"));
        neither.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var both = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            Json("""{"mbRecordingId":"833f00e1-781f-4edd-90e4-e52712618862","deezerId":3135556}"""));

        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var unknown = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            Json("""{"mbRecordingId":"99999999-9999-4999-8999-999999999999"}"""));

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Listing_updating_and_deleting_a_song_round_trips()
    {
        using var factory = FakeProviders();
        using var client = Authenticated(factory);
        await SeedSongAsync(factory, "Alpha", StoredRecordingId, monitored: true);
        await SeedSongAsync(factory, "Bravo", null, monitored: false);

        using var listed = await client.GetAsync(
            new Uri($"{SongEndpoint}?monitored=true&sortKey=title&sortDirection=ascending", UriKind.Relative));
        var page = await ReadJsonAsync(listed);

        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        page.GetProperty("totalRecords").GetInt32().Should().Be(1);
        page.GetProperty("page").GetInt32().Should().Be(1);
        page.GetProperty("pageSize").GetInt32().Should().Be(20);
        page.GetProperty("sortKey").GetString().Should().Be("title");
        page.GetProperty("sortDirection").GetString().Should().Be("ascending");

        var id = page.GetProperty("records")[0].GetProperty("id").GetInt64();
        page.GetProperty("records")[0].GetProperty("title").GetString().Should().Be("Alpha");

        using var updated = await client.PutAsync(
            new Uri($"{SongEndpoint}/{id}", UriKind.Relative),
            Json("""{"monitored":false}"""));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(updated)).GetProperty("monitored").GetBoolean().Should().BeFalse();

        using var afterUpdate = await client.GetAsync(new Uri($"{SongEndpoint}?monitored=true", UriKind.Relative));
        (await ReadJsonAsync(afterUpdate)).GetProperty("totalRecords").GetInt32().Should().Be(0);

        using var deleted = await client.DeleteAsync(new Uri($"{SongEndpoint}/{id}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var gone = await client.GetAsync(new Uri($"{SongEndpoint}/{id}", UriKind.Relative));
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var missing = await client.PutAsync(
            new Uri($"{SongEndpoint}/{id}", UriKind.Relative),
            Json("""{"monitored":true}"""));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Album_contexts_end_with_the_singles_option_and_explicitly_move_the_song()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(Identity()));

        using var factory = FakeProviders(resolver: resolver);
        using var client = Authenticated(factory);
        var songId = await SeedSongAsync(factory, "Get Lucky", RecordingId, monitored: true, albumKey: ReleaseId);

        using var options = await client.GetAsync(new Uri($"{SongEndpoint}/{songId}/albumcontexts", UriKind.Relative));
        var list = await ReadJsonAsync(options);

        options.StatusCode.Should().Be(HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("key").GetString().Should().Be(ReleaseId);
        list[0].GetProperty("title").GetString().Should().Be("Random Access Memories");
        list[0].GetProperty("primaryType").GetString().Should().Be("Album");
        list[0].GetProperty("totalTracks").GetInt32().Should().Be(13);
        list[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();

        list[1].GetProperty("key").GetString().Should().Be("singles");
        list[1].GetProperty("title").GetString().Should().Be("Singles");
        list[1].GetProperty("albumArtist").GetString().Should().Be("Daft Punk");
        list[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();

        using var moved = await client.PutAsync(
            new Uri($"{SongEndpoint}/{songId}/albumcontext", UriKind.Relative),
            Json("""{"albumKey":"singles"}"""));

        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(moved))
            .GetProperty("albumContext").GetProperty("kind").GetString().Should().Be("pseudoSingles");

        // The move is what the picker now marks as current.
        using var afterMove = await client.GetAsync(new Uri($"{SongEndpoint}/{songId}/albumcontexts", UriKind.Relative));
        var afterList = await ReadJsonAsync(afterMove);

        afterList[0].GetProperty("isCurrent").GetBoolean().Should().BeFalse();
        afterList[1].GetProperty("isCurrent").GetBoolean().Should().BeTrue();

        using var back = await client.PutAsync(
            new Uri($"{SongEndpoint}/{songId}/albumcontext", UriKind.Relative),
            Json($$"""{"albumKey":"{{ReleaseId}}"}"""));

        back.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(back))
            .GetProperty("albumContext").GetProperty("kind").GetString().Should().Be("album");

        using var unknownKey = await client.PutAsync(
            new Uri($"{SongEndpoint}/{songId}/albumcontext", UriKind.Relative),
            Json("""{"albumKey":"nope"}"""));

        unknownKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_song_endpoint_requires_the_api_key()
    {
        using var factory = FakeProviders();
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(new Uri($"{SongEndpoint}?monitored=true", UriKind.Relative));
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var one = await client.GetAsync(new Uri($"{SongEndpoint}/1", UriKind.Relative));
        one.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var add = await client.PostAsync(
            new Uri(SongEndpoint, UriKind.Relative),
            Json("""{"mbRecordingId":"833f00e1-781f-4edd-90e4-e52712618862"}"""));

        add.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var lookup = await client.PostAsync(new Uri(LookupEndpoint, UriKind.Relative), Json("""{"term":"a"}"""));
        lookup.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var update = await client.PutAsync(new Uri($"{SongEndpoint}/1", UriKind.Relative), Json("{}"));
        update.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var delete = await client.DeleteAsync(new Uri($"{SongEndpoint}/1", UriKind.Relative));
        delete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var contexts = await client.GetAsync(new Uri($"{SongEndpoint}/1/albumcontexts", UriKind.Relative));
        contexts.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var move = await client.PutAsync(
            new Uri($"{SongEndpoint}/1/albumcontext", UriKind.Relative),
            Json("""{"albumKey":"singles"}"""));

        move.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A factory whose four providers are fakes, so nothing can reach the network.</summary>
    internal static CompilarrAppFactory FakeProviders(
        IIdentityResolver? resolver = null,
        IDeezerClient? deezer = null,
        IMusicBrainzClient? musicBrainz = null,
        ICoverArtResolver? coverArt = null)
    {
        var mb = musicBrainz ?? Substitute.For<IMusicBrainzClient>();
        mb.GetReleaseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MbRelease?>(new MbRelease
            {
                Id = ReleaseId,
                Title = "Random Access Memories",
                Status = "Official",
                Date = "2013-05-17",
            }));

        var covers = coverArt ?? Substitute.For<ICoverArtResolver>();
        covers.ResolveAsync(Arg.Any<CoverArtRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CoverArt?>(new CoverArt(CoverUrl, "coverartarchive")));

        return new CompilarrAppFactory(configureServices: services =>
        {
            services.AddSingleton(resolver ?? Substitute.For<IIdentityResolver>());
            services.AddSingleton(deezer ?? Substitute.For<IDeezerClient>());
            services.AddSingleton(mb);
            services.AddSingleton(covers);
        });
    }

    internal static HttpClient Authenticated(CompilarrAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", factory.ApiKey);

        return client;
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>
    /// Seeds one song by hand, so the API tests do not depend on the add path. The song is filed
    /// under <paramref name="albumKey"/>, which is what the album picker marks as current.
    /// </summary>
    internal static async Task<long> SeedSongAsync(
        CompilarrAppFactory factory,
        string title,
        string? mbRecordingId,
        bool monitored,
        string? albumKey = null,
        AlbumContextKind albumKind = AlbumContextKind.Album)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        var artist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            MbRecordingId = mbRecordingId,
            DurationMs = 248_000,
            Monitored = monitored,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };

        // The artist list counts credits, not the primary-artist column.
        song.Artists.Add(new SongArtist { Artist = artist, Role = ArtistRole.Main, Position = 1 });

        var key = albumKey ?? new Guid("0a0a0a0a-1b1b-2c2c-3d3d-4e4e4e4e4e4e").ToString("D");
        song.AlbumContext = new AlbumContext
        {
            Kind = albumKind,
            AlbumTitle = albumKind == AlbumContextKind.PseudoSingles ? "Singles" : "Random Access Memories",
            AlbumArtist = artist.Name,
            AlbumKey = key,
            MbReleaseId = albumKind == AlbumContextKind.Album ? key : null,
            CoverUrl = CoverUrl,
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }

    /// <summary>The identity the fakes resolve the recording to: one official album, one cover.</summary>
    internal static SongIdentity Identity() => new()
    {
        Source = IdentityResolver.MusicBrainzSource,
        MbRecordingId = RecordingId,
        Title = "Get Lucky",
        ArtistCredit = "Daft Punk feat. Pharrell Williams",
        Artists =
        [
            new IdentityArtist("Daft Punk", "Daft Punk", "056e4f3e-d505-4dad-8ec1-d04f521cbb56", null, ArtistRole.Main, 0),
            new IdentityArtist("Pharrell Williams", "Williams, Pharrell", null, 1057, ArtistRole.Featured, 1),
        ],
        DurationMs = 248_000,
        Isrcs = ["GBDUW1300040"],
        OriginalDate = "2013-04-19",
        CoverUrl = CoverUrl,
        ReleaseOptions =
        [
            new ReleaseOption
            {
                Key = ReleaseId,
                MbReleaseId = ReleaseId,
                MbReleaseGroupId = ReleaseGroupId,
                Title = "Random Access Memories",
                AlbumArtist = "Daft Punk",
                PrimaryType = "Album",
                Status = "Official",
                Date = "2013-05-17",
                TotalTracks = 13,
            },
        ],
    };

    /// <summary>One ranked candidate, as the resolver would return it.</summary>
    private static SongCandidate Candidate(string recordingId, string title, double score) => new()
    {
        Source = IdentityResolver.MusicBrainzSource,
        MbRecordingId = recordingId,
        Title = title,
        ArtistCredit = "Daft Punk feat. Pharrell Williams",
        DurationMs = 248_000,
        FirstReleaseDate = "2013-04-19",
        ReleaseTypes = ["Album"],
        AlbumTitle = "Random Access Memories",
        CoverUrl = CoverUrl,
        Isrcs = ["GBDUW1300040"],
        Score = score,
    };
}
