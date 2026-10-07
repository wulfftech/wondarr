using System.Net;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The album add over HTTP: lookup, the release list of a group, a tracklist with the library's copy
/// beside it, the 202 an add answers with, and the album a song is filed under. The providers are
/// fakes, so nothing here reaches the network.
/// </summary>
public sealed class AlbumApiTests
{
    /// <summary>The release group of <c>A Night at the Opera</c>, as the recorded search names it.</summary>
    private const string ReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";

    /// <summary>The 1975 UK vinyl the tracklist fixture was recorded from.</summary>
    private const string ReleaseId = "6defd963-fe91-4550-b18e-82c685603c2b";

    /// <summary>Bohemian Rhapsody, track 11 of that release.</summary>
    private const string BohemianRecording = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";

    private const string LookupEndpoint = "/api/v1/album/lookup";

    [Fact]
    public async Task A_lookup_answers_with_the_release_groups_musicbrainz_found()
    {
        using var factory = SongApiTests.FakeProviders();
        StubGroupSearch(factory);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{LookupEndpoint}?term=queen%20night%20opera", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hits = await SongApiTests.ReadJsonAsync(response);
        hits.GetArrayLength().Should().Be(1);

        var hit = hits[0];
        hit.GetProperty("source").GetString().Should().Be("musicbrainz");
        hit.GetProperty("id").GetString().Should().Be(ReleaseGroupId);
        hit.GetProperty("title").GetString().Should().Be("A Night at the Opera");
        hit.GetProperty("artist").GetString().Should().Be("Queen");
        hit.GetProperty("year").GetString().Should().Be("1975");
        hit.GetProperty("type").GetString().Should().Be("Album");
    }

    [Fact]
    public async Task A_lookup_without_a_term_is_rejected()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{LookupEndpoint}?term=%20%20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_release_list_of_a_group_marks_one_default()
    {
        using var factory = SongApiTests.FakeProviders();
        StubGroupReleases(factory);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(
            new Uri($"/api/v1/album/releasegroup/{ReleaseGroupId}/releases", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var releases = await SongApiTests.ReadJsonAsync(response);
        releases.GetArrayLength().Should().Be(71);

        var defaults = releases.EnumerateArray().Where(release => release.GetProperty("isDefault").GetBoolean());
        defaults.Should().ContainSingle();

        var chosen = defaults.Single();
        chosen.GetProperty("id").GetString().Should().Be("18677378-dfb9-40f5-bbb0-a492e4dc9906");
        chosen.GetProperty("formats").GetString().Should().Be("CD");
        chosen.GetProperty("trackCount").GetInt32().Should().Be(12);

        var vinyl = releases.EnumerateArray().Single(release => release.GetProperty("id").GetString() == ReleaseId);
        vinyl.GetProperty("date").GetString().Should().Be("1975-11-21");
        vinyl.GetProperty("country").GetString().Should().Be("GB");
        vinyl.GetProperty("isDefault").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_tracklist_of_a_release_carries_the_song_the_library_already_holds()
    {
        using var factory = SongApiTests.FakeProviders(musicBrainz: ReleaseStub());
        using var client = SongApiTests.Authenticated(factory);

        var songId = await SeedOwnedSongAsync(factory);

        using var response = await client.GetAsync(
            new Uri($"/api/v1/album/musicbrainz/{ReleaseId}/tracks", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tracks = await SongApiTests.ReadJsonAsync(response);
        tracks.GetArrayLength().Should().Be(12);

        var bohemian = tracks.EnumerateArray().Single(track => track.GetProperty("position").GetInt32() == 11);
        bohemian.GetProperty("title").GetString().Should().Be("Bohemian Rhapsody");
        bohemian.GetProperty("lengthMs").GetInt32().Should().Be(355_106);
        bohemian.GetProperty("mbRecordingId").GetString().Should().Be(BohemianRecording);
        bohemian.GetProperty("isrcs").GetArrayLength().Should().Be(7);
        bohemian.GetProperty("owned").GetBoolean().Should().BeTrue();
        bohemian.GetProperty("songId").GetInt64().Should().Be(songId);

        var first = tracks[0];
        first.GetProperty("owned").GetBoolean().Should().BeFalse();
        first.GetProperty("disc").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_release_and_an_unknown_source_are_not_answered()
    {
        using var factory = SongApiTests.FakeProviders(musicBrainz: ReleaseStub());
        using var client = SongApiTests.Authenticated(factory);

        using var missing = await client.GetAsync(
            new Uri("/api/v1/album/musicbrainz/00000000-0000-0000-0000-000000000000/tracks", UriKind.Relative));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var unknownSource = await client.GetAsync(
            new Uri($"/api/v1/album/beatport/{ReleaseId}/tracks", UriKind.Relative));
        unknownSource.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_add_is_accepted_with_the_command_that_will_run_it()
    {
        using var factory = SongApiTests.FakeProviders(musicBrainz: ReleaseStub());
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.PostAsync(
            new Uri("/api/v1/album/add", UriKind.Relative),
            SongApiTests.Json(
                $$"""
                {
                  "source": "musicbrainz",
                  "id": "{{ReleaseId}}",
                  "trackKeys": ["{{BohemianRecording}}"],
                  "monitored": true
                }
                """));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var accepted = await SongApiTests.ReadJsonAsync(response);
        var commandId = accepted.GetProperty("commandId").GetInt64();
        commandId.Should().BeGreaterThan(0);

        using var command = await client.GetAsync(new Uri($"/api/v1/command/{commandId}", UriKind.Relative));
        command.StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await SongApiTests.ReadJsonAsync(command);
        record.GetProperty("name").GetString().Should().Be("AddAlbum");
        record.GetProperty("trigger").GetString().Should().Be("manual");
        record.GetProperty("body").GetString().Should().Contain(ReleaseId);
    }

    [Fact]
    public async Task An_add_without_an_album_or_with_an_unknown_library_is_rejected()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        using var noAlbum = await client.PostAsync(
            new Uri("/api/v1/album/add", UriKind.Relative),
            SongApiTests.Json("""{"source": "musicbrainz"}"""));
        noAlbum.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var unknownLibrary = await client.PostAsync(
            new Uri("/api/v1/album/add", UriKind.Relative),
            SongApiTests.Json(
                $$"""
                {
                  "source": "musicbrainz",
                  "id": "{{ReleaseId}}",
                  "libraryId": 4242
                }
                """));
        unknownLibrary.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_album_of_a_song_is_the_release_it_is_pinned_to()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        var pinned = await SeedOwnedSongAsync(factory);
        var loose = await SongApiTests.SeedSongAsync(
            factory,
            "I Go Crazy",
            null,
            monitored: true,
            albumKind: AlbumContextKind.PseudoSingles);

        using var found = await client.GetAsync(new Uri($"/api/v1/song/{pinned}/album", UriKind.Relative));
        found.StatusCode.Should().Be(HttpStatusCode.OK);

        var album = await SongApiTests.ReadJsonAsync(found);
        album.GetProperty("source").GetString().Should().Be("musicbrainz");
        album.GetProperty("id").GetString().Should().Be(ReleaseId);

        using var none = await client.GetAsync(new Uri($"/api/v1/song/{loose}/album", UriKind.Relative));
        none.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var unknown = await client.GetAsync(new Uri("/api/v1/song/424242/album", UriKind.Relative));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_album_endpoints_need_the_api_key()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = factory.CreateClient();

        using var lookup = await client.GetAsync(new Uri($"{LookupEndpoint}?term=queen", UriKind.Relative));
        lookup.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var releases = await client.GetAsync(
            new Uri($"/api/v1/album/releasegroup/{ReleaseGroupId}/releases", UriKind.Relative));
        releases.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var tracks = await client.GetAsync(
            new Uri($"/api/v1/album/musicbrainz/{ReleaseId}/tracks", UriKind.Relative));
        tracks.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var add = await client.PostAsync(
            new Uri("/api/v1/album/add", UriKind.Relative),
            SongApiTests.Json("""{"source": "musicbrainz", "id": "r1"}"""));
        add.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var album = await client.GetAsync(new Uri("/api/v1/song/1/album", UriKind.Relative));
        album.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>Stubs the release-group search the lookup runs.</summary>
    private static void StubGroupSearch(WondarrAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var musicBrainz = scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>();

        musicBrainz
            .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MbReleaseGroupSearchResult
            {
                ReleaseGroups =
                [
                    new MbReleaseGroup
                    {
                        Id = ReleaseGroupId,
                        Title = "A Night at the Opera",
                        PrimaryType = "album",
                        FirstReleaseDate = "1975-11-21",
                        ArtistCredit =
                        [
                            new MbArtistCredit { Name = "Queen", Artist = new MbArtist { Name = "Queen" } },
                        ],
                    },
                ],
            });
    }

    /// <summary>Stubs the release browse the release list runs, with the recorded 71 official releases.</summary>
    private static void StubGroupReleases(WondarrAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var musicBrainz = scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>();

        musicBrainz
            .GetReleasesForReleaseGroupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AlbumFixtures.BrowseReleases());
    }

    /// <summary>
    /// A factory whose MusicBrainz fake answers the fixture's release and nothing else, so an unknown
    /// release is a 404 and the tracklist is the recorded one.
    /// </summary>
    private static WondarrAppFactory ReleaseFactory()
    {
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        musicBrainz
            .GetReleaseAsync(ReleaseId, Arg.Any<CancellationToken>())
            .Returns(AlbumFixtures.ReleaseWithIsrcs());

        return new WondarrAppFactory(configureServices: services =>
        {
            services.AddSingleton(Substitute.For<IIdentityResolver>());
            services.AddSingleton(Substitute.For<IDeezerClient>());
            services.AddSingleton(musicBrainz);
            services.AddSingleton(Substitute.For<ICoverArtResolver>());
        });
    }

    /// <summary>Seeds one song filed under the fixture's release, as an album add would leave it.</summary>
    private static async Task<long> SeedOwnedSongAsync(WondarrAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var artist = new Artist { Name = "Queen", SortName = "Queen" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "Bohemian Rhapsody",
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            MbRecordingId = BohemianRecording,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "album",
            AlbumContext = new AlbumContext
            {
                Kind = AlbumContextKind.Album,
                AlbumTitle = "A Night at the Opera",
                AlbumArtist = artist.Name,
                AlbumKey = ReleaseId,
                MbReleaseId = ReleaseId,
            },
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        return song.Id;
    }
}

/// <summary>
/// The recorded MusicBrainz answers this task's fixtures hold, deserialised the way the client
/// deserialises them. They are shared with <c>Wondarr.Core.Tests</c> and read from the repository's
/// <c>tests/fixtures</c>, so they are not copied next to the test binaries.
/// </summary>
internal static class AlbumFixtures
{
    /// <summary>The MusicBrainz client's own JSON: kebab-case keys, case-sensitive.</summary>
    private static readonly JsonSerializerOptions MusicBrainzJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
    };

    /// <summary>The 71 official releases of the fixture, as the client would deserialise them.</summary>
    internal static List<MbRelease> BrowseReleases()
    {
        using var document = JsonDocument.Parse(
            ReadFixture("browse-releases-by-group-a-night-at-the-opera.json"));

        return document.RootElement
            .GetProperty("releases")
            .EnumerateArray()
            .Select(element => element.Deserialize<MbRelease>(MusicBrainzJson)!)
            .ToList();
    }

    /// <summary>The release lookup with <c>inc=isrcs</c>: twelve tracks, each with its ISRCs.</summary>
    internal static MbRelease ReleaseWithIsrcs() =>
        JsonSerializer.Deserialize<MbRelease>(
            ReadFixture("release-a-night-at-the-opera-isrcs.json"),
            MusicBrainzJson)!;

    /// <summary>Reads one recorded fixture, walking up to the repository's <c>tests/fixtures</c>.</summary>
    private static string ReadFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        var path = directory is null
            ? null
            : Path.Combine(directory.FullName, "tests", "fixtures", "musicbrainz", name);

        return File.Exists(path)
            ? File.ReadAllText(path!)
            : throw new FileNotFoundException($"Could not find tests/fixtures/musicbrainz/{name} above {AppContext.BaseDirectory}.");
    }
}
