using System.Net;
using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Lyrics;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Wondarr.Api.Tests;

/// <summary>
/// The data behind a song's own page: the full file, <c>/details</c>, <c>/lyrics</c> and the per-song
/// queue and blocklist filters. Every outside source is faked, so no test reaches the network.
/// </summary>
public sealed class SongDetailsApiTests
{
    private const string RecordingId = "833f00e1-781f-4edd-90e4-e52712618862";
    private const string ReleaseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ReleaseGroupId = "0f0f0f0f-1e1e-2d2d-3c3c-4b4b4b4b4b4b";
    private const long DeezerId = 3135556;

    /// <summary>A seeded quality (FLAC); the file row's quality is a real foreign key.</summary>
    private const long FlacQualityId = 36;

    [Fact]
    public async Task A_downloaded_file_is_shown_in_full_with_only_the_safe_part_of_its_source()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);
        await SeedFileAsync(
            factory,
            songId,
            "/music/Daft Punk/Get Lucky.flac",
            SourceTypes.Soulseek,
            """{"provider":"someuser","remotePath":"\\\\someuser\\private share\\Daft Punk\\Get Lucky.flac","candidateId":4,"queueItemId":7,"searchRunId":2}""");

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var song = await SongApiTests.ReadJsonAsync(response);
        var file = song.GetProperty("file");
        file.GetProperty("sampleRate").GetInt32().Should().Be(44_100);
        file.GetProperty("bitDepth").GetInt32().Should().Be(16);
        file.GetProperty("channels").GetInt32().Should().Be(2);
        file.GetProperty("durationMs").GetInt32().Should().Be(248_100);
        file.GetProperty("qualityId").GetInt64().Should().Be(song.GetProperty("qualityId").GetInt64());
        file.GetProperty("acoustId").GetString().Should().Be("acoust-1");
        file.GetProperty("fingerprintVerified").GetBoolean().Should().BeTrue();
        file.GetProperty("importedAt").GetDateTime().Should().Be(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        file.GetProperty("tagsWritten").GetProperty("title").GetString().Should().Be("Get Lucky");
        file.GetProperty("replayGainDb").GetDouble().Should().BeApproximately(-7.5, 0.001);
        file.GetProperty("replayGainPeak").GetDouble().Should().BeApproximately(0.98, 0.001);

        var source = file.GetProperty("source");
        source.GetProperty("kind").GetString().Should().Be("download");
        source.GetProperty("provider").GetString().Should().Be("someuser");
        source.GetProperty("name").GetString().Should().Be("Get Lucky.flac");
        source.GetProperty("queueItemId").GetInt64().Should().Be(7);

        // Never the stored blob, nor the peer's folder.
        var raw = file.GetRawText();
        raw.Should().NotContain("private share");
        raw.Should().NotContain("remotePath");
        raw.Should().NotContain("searchRunId");
    }

    [Fact]
    public async Task A_reference_file_names_its_library_and_relative_path_and_the_details_repeat_the_row()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);
        var (libraryId, fileId) = await SeedReferenceFileAsync(factory, songId);
        await SeedFileAsync(
            factory,
            songId,
            "/refs/My Music/Daft Punk/Get Lucky.flac",
            SourceTypes.Reference,
            $$"""{"referenceLibraryId":{{libraryId}},"referenceFileId":{{fileId}}}""");

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}", UriKind.Relative));
        var source = (await SongApiTests.ReadJsonAsync(response)).GetProperty("file").GetProperty("source");

        source.GetProperty("kind").GetString().Should().Be("reference");
        source.GetProperty("referenceLibraryId").GetInt64().Should().Be(libraryId);
        source.GetProperty("referenceLibraryName").GetString().Should().Be("My Music");
        source.GetProperty("relativePath").GetString().Should().Be("Daft Punk/Get Lucky.flac");
        source.GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);

        using var details = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));
        var reference = (await SongApiTests.ReadJsonAsync(details)).GetProperty("referenceFile");

        reference.GetProperty("libraryId").GetInt64().Should().Be(libraryId);
        reference.GetProperty("libraryName").GetString().Should().Be("My Music");
        reference.GetProperty("relativePath").GetString().Should().Be("Daft Punk/Get Lucky.flac");
        reference.GetProperty("identifiedBy").GetString().Should().Be("tag_mbid");
        reference.GetProperty("confidence").GetDouble().Should().BeApproximately(0.95, 0.001);
        reference.GetProperty("state").GetString().Should().Be("identified");
    }

    [Fact]
    public async Task Details_assembles_every_source()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, DeezerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(Identity()));

        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetTrackAsync(DeezerId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeezerTrack?>(Track()));

        using var factory = SongApiTests.FakeProviders(resolver: resolver, deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, DeezerId, albumKey: ReleaseId);

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await SongApiTests.ReadJsonAsync(response);

        var releases = details.GetProperty("releases");
        releases.GetArrayLength().Should().Be(2);
        releases[0].GetProperty("title").GetString().Should().Be("Random Access Memories");
        releases[0].GetProperty("albumArtist").GetString().Should().Be("Daft Punk");
        releases[0].GetProperty("primaryType").GetString().Should().Be("Album");
        releases[0].GetProperty("date").GetString().Should().Be("2013-05-17");
        releases[0].GetProperty("coverUrl").GetString()
            .Should().Be($"https://coverartarchive.org/release/{ReleaseId}/front-250");
        releases[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();

        // Only a release group is known: its front cover; no ids at all: none.
        releases[1].GetProperty("coverUrl").GetString()
            .Should().Be($"https://coverartarchive.org/release-group/{ReleaseGroupId}/front-250");
        releases[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();

        var musicBrainz = details.GetProperty("musicBrainz");
        musicBrainz.GetProperty("recordingId").GetString().Should().Be(RecordingId);
        musicBrainz.GetProperty("firstReleaseDate").GetString().Should().Be("2013-04-19");
        musicBrainz.GetProperty("disambiguation").GetString().Should().Be("radio edit");
        musicBrainz.GetProperty("isrcs")[0].GetString().Should().Be("GBDUW1300040");
        musicBrainz.GetProperty("artistCredit").GetString().Should().Be("Daft Punk");
        musicBrainz.GetProperty("url").GetString().Should().Be($"https://musicbrainz.org/recording/{RecordingId}");

        var deezerSection = details.GetProperty("deezer");
        deezerSection.GetProperty("url").GetString().Should().Be($"https://www.deezer.com/track/{DeezerId}");
        deezerSection.GetProperty("rank").GetInt64().Should().Be(880_000);
        deezerSection.GetProperty("explicitLyrics").GetBoolean().Should().BeTrue();
        deezerSection.GetProperty("bpm").GetDouble().Should().BeApproximately(116.0, 0.001);
        deezerSection.GetProperty("gain").GetDouble().Should().BeApproximately(-9.4, 0.001);
        deezerSection.GetProperty("releaseDate").GetString().Should().Be("2013-05-17");
        deezerSection.GetProperty("albumCoverUrl").GetString().Should().Be("https://cdn.example/ram-xl.jpg");

        details.GetProperty("referenceFile").ValueKind.Should().Be(JsonValueKind.Null);
        details.GetProperty("lyrics").GetProperty("source").GetString().Should().Be("none");
    }

    [Fact]
    public async Task A_failing_deezer_leaves_its_section_null_and_the_rest_intact()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, DeezerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(Identity()));

        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetTrackAsync(DeezerId, Arg.Any<CancellationToken>())
            .Returns<Task<DeezerTrack?>>(_ => throw new HttpRequestException("Deezer is down"));

        using var factory = SongApiTests.FakeProviders(resolver: resolver, deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, DeezerId);

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await SongApiTests.ReadJsonAsync(response);
        details.GetProperty("deezer").ValueKind.Should().Be(JsonValueKind.Null);
        details.GetProperty("musicBrainz").GetProperty("recordingId").GetString().Should().Be(RecordingId);
        details.GetProperty("releases").GetArrayLength().Should().Be(2);

        // A failure is not remembered: the next view asks again.
        using var again = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        await deezer.Received(2).GetTrackAsync(DeezerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_identity_leaves_the_releases_empty_and_the_musicbrainz_link_from_the_song()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns<Task<SongIdentity?>>(_ => throw new HttpRequestException("MusicBrainz is down"));

        using var factory = SongApiTests.FakeProviders(resolver: resolver);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var details = await SongApiTests.ReadJsonAsync(response);
        details.GetProperty("releases").GetArrayLength().Should().Be(0);
        details.GetProperty("deezer").ValueKind.Should().Be(JsonValueKind.Null);
        details.GetProperty("musicBrainz").GetProperty("url").GetString()
            .Should().Be($"https://musicbrainz.org/recording/{RecordingId}");
        details.GetProperty("musicBrainz").GetProperty("firstReleaseDate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Deezer_is_asked_once_for_two_requests_within_the_hour()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.GetIdentityAsync(RecordingId, DeezerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SongIdentity?>(Identity()));

        var deezer = Substitute.For<IDeezerClient>();
        deezer.GetTrackAsync(DeezerId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeezerTrack?>(Track()));

        using var factory = SongApiTests.FakeProviders(resolver: resolver, deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, DeezerId);

        for (var request = 0; request < 2; request++)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));

            (await SongApiTests.ReadJsonAsync(response)).GetProperty("deezer").GetProperty("bpm").GetDouble()
                .Should().BeApproximately(116.0, 0.001);
        }

        await deezer.Received(1).GetTrackAsync(DeezerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lyrics_come_from_the_sidecar_next_to_the_file_and_details_say_so()
    {
        var lrclib = Substitute.For<ILrclibClient>();

        using var factory = Factory(lrclib);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);
        var audio = Path.Combine(factory.ConfigDir, "library", "Get Lucky.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        await File.WriteAllTextAsync(audio, "not really audio");
        await File.WriteAllTextAsync(Path.ChangeExtension(audio, ".lrc"), "[00:01.00]We've come too far\n[00:04.00]To give up who we are\n");
        await SeedFileAsync(factory, songId, audio, SourceTypes.Soulseek, "{}");

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/lyrics", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lyrics = await SongApiTests.ReadJsonAsync(response);
        lyrics.GetProperty("source").GetString().Should().Be("sidecar");
        lyrics.GetProperty("synced").GetString().Should().Contain("[00:01.00]We've come too far");
        lyrics.GetProperty("plain").GetString().Should().Be("We've come too far\nTo give up who we are");

        await lrclib.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default, default);

        using var details = await client.GetAsync(new Uri($"/api/v1/song/{songId}/details", UriKind.Relative));
        var availability = (await SongApiTests.ReadJsonAsync(details)).GetProperty("lyrics");
        availability.GetProperty("source").GetString().Should().Be("sidecar");
        availability.GetProperty("synced").GetBoolean().Should().BeTrue();
        availability.GetProperty("plain").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Lyrics_without_a_sidecar_are_looked_up_once_for_two_requests_and_never_written()
    {
        var lrclib = Substitute.For<ILrclibClient>();
        lrclib.FindAsync("Get Lucky", "Daft Punk", 248, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LyricsLookup(LyricsLookupStatus.Found, "plain text", "[00:01.00]plain text", 12)));

        using var factory = Factory(lrclib);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);
        var audio = Path.Combine(factory.ConfigDir, "library", "Get Lucky.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        await File.WriteAllTextAsync(audio, "not really audio");
        await SeedFileAsync(factory, songId, audio, SourceTypes.Soulseek, "{}");

        for (var request = 0; request < 2; request++)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/lyrics", UriKind.Relative));
            var lyrics = await SongApiTests.ReadJsonAsync(response);

            lyrics.GetProperty("source").GetString().Should().Be("lrclib");
            lyrics.GetProperty("plain").GetString().Should().Be("plain text");
            lyrics.GetProperty("synced").GetString().Should().Be("[00:01.00]plain text");
        }

        await lrclib.Received(1).FindAsync("Get Lucky", "Daft Punk", 248, Arg.Any<CancellationToken>());
        Directory.GetFiles(Path.GetDirectoryName(audio)!).Should().ContainSingle().Which.Should().Be(audio);
    }

    [Fact]
    public async Task A_lookup_that_found_nothing_is_not_an_error_and_an_unavailable_one_is_not_remembered()
    {
        var lrclib = Substitute.For<ILrclibClient>();
        lrclib.FindAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new LyricsLookup(LyricsLookupStatus.Unavailable, null, null, null)),
                Task.FromResult(new LyricsLookup(LyricsLookupStatus.NotFound, null, null, null)));

        using var factory = Factory(lrclib);
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);

        for (var request = 0; request < 3; request++)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/lyrics", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await SongApiTests.ReadJsonAsync(response)).GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // Unavailable is asked again, NotFound is remembered: two calls for three views.
        await lrclib.Received(2).FindAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lyrics_switched_off_answer_a_null_source_without_a_lookup()
    {
        var lrclib = Substitute.For<ILrclibClient>();

        using var factory = Factory(lrclib, new Dictionary<string, string?> { ["Lyrics:Enabled"] = "false" });
        using var client = SongApiTests.Authenticated(factory);
        var songId = await SeedSongAsync(factory, deezerId: null);

        using var response = await client.GetAsync(new Uri($"/api/v1/song/{songId}/lyrics", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lyrics = await SongApiTests.ReadJsonAsync(response);
        lyrics.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        lyrics.GetProperty("synced").ValueKind.Should().Be(JsonValueKind.Null);
        lyrics.GetProperty("plain").ValueKind.Should().Be(JsonValueKind.Null);

        await lrclib.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task An_unknown_song_is_a_404_on_every_new_endpoint()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        foreach (var path in new[] { "/api/v1/song/4242", "/api/v1/song/4242/details", "/api/v1/song/4242/lyrics" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task The_new_endpoints_require_the_api_key()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/v1/song/1/details", "/api/v1/song/1/lyrics" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task The_queue_and_the_blocklist_can_be_filtered_to_one_song()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = SongApiTests.Authenticated(factory);

        var first = await QueueApiTests.SeedSongAsync(factory, "Get Lucky");
        var second = await QueueApiTests.SeedSongAsync(factory, "Instant Crush");
        var firstCandidate = await QueueApiTests.SeedCandidateAsync(factory, first, "Get Lucky.flac");
        var secondCandidate = await QueueApiTests.SeedCandidateAsync(factory, second, "Instant Crush.flac");
        await QueueApiTests.SeedQueueItemAsync(factory, first, firstCandidate, QueueItemState.Downloading);
        await QueueApiTests.SeedQueueItemAsync(factory, second, secondCandidate, QueueItemState.Downloading);
        await SeedBlocklistAsync(factory, first, "peer:one");
        await SeedBlocklistAsync(factory, second, "peer:two");
        await SeedBlocklistAsync(factory, null, "peer:three");

        using var allQueue = await client.GetAsync(new Uri("/api/v1/queue", UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(allQueue)).GetProperty("totalRecords").GetInt32().Should().Be(2);

        using var queue = await client.GetAsync(new Uri($"/api/v1/queue?songId={second}", UriKind.Relative));
        var queued = await SongApiTests.ReadJsonAsync(queue);
        queued.GetProperty("totalRecords").GetInt32().Should().Be(1);
        queued.GetProperty("records")[0].GetProperty("songId").GetInt64().Should().Be(second);

        using var allBlocked = await client.GetAsync(new Uri("/api/v1/blocklist", UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(allBlocked)).GetProperty("totalRecords").GetInt32().Should().Be(3);

        using var blocklist = await client.GetAsync(new Uri($"/api/v1/blocklist?songId={first}", UriKind.Relative));
        var blocked = await SongApiTests.ReadJsonAsync(blocklist);
        blocked.GetProperty("totalRecords").GetInt32().Should().Be(1);
        blocked.GetProperty("records")[0].GetProperty("blocklistKey").GetString().Should().Be("peer:one");

        using var none = await client.GetAsync(new Uri("/api/v1/blocklist?songId=4242", UriKind.Relative));
        (await SongApiTests.ReadJsonAsync(none)).GetProperty("totalRecords").GetInt32().Should().Be(0);
    }

    /// <summary>The default fakes plus a spy LRCLIB client, optionally with extra settings.</summary>
    private static WondarrAppFactory Factory(ILrclibClient lrclib, IReadOnlyDictionary<string, string?>? settings = null) =>
        new(
            settings,
            services =>
            {
                // Nothing may reach MusicBrainz or Deezer: the details endpoint asks for the identity.
                services.AddSingleton(Substitute.For<IIdentityResolver>());
                services.AddSingleton(Substitute.For<IDeezerClient>());
                services.AddSingleton(lrclib);
            });

    /// <summary>The identity the fakes resolve the recording to: two releases, only the first with a release id.</summary>
    private static SongIdentity Identity() => new()
    {
        Source = "musicbrainz",
        MbRecordingId = RecordingId,
        DeezerId = DeezerId,
        Title = "Get Lucky",
        ArtistCredit = "Daft Punk",
        Isrcs = ["GBDUW1300040"],
        Disambiguation = "radio edit",
        OriginalDate = "2013-04-19",
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
                Date = "2013-05-17",
                TrackNo = 8,
                TotalTracks = 13,
            },
            new ReleaseOption
            {
                Key = "group-only",
                MbReleaseGroupId = ReleaseGroupId,
                Title = "Get Lucky",
                AlbumArtist = "Daft Punk",
                PrimaryType = "Single",
            },
        ],
    };

    private static DeezerTrack Track() => new()
    {
        Id = DeezerId,
        Title = "Get Lucky",
        Rank = 880_000,
        ExplicitLyrics = true,
        Bpm = 116.0,
        Gain = -9.4,
        ReleaseDate = "2013-05-17",
        Album = new DeezerAlbumRef { Id = 1, Title = "Random Access Memories", CoverXl = "https://cdn.example/ram-xl.jpg" },
    };

    private static async Task<long> SeedSongAsync(WondarrAppFactory factory, long? deezerId, string? albumKey = null)
    {
        var id = await SongApiTests.SeedSongAsync(factory, "Get Lucky", RecordingId, monitored: true, albumKey);

        if (deezerId is not null)
        {
            using var scope = factory.Services.CreateScope();
            await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();
            var song = await context.Songs.FindAsync(id);
            song!.DeezerId = deezerId;
            await context.SaveChangesAsync();
        }

        return id;
    }

    private static async Task SeedFileAsync(
        WondarrAppFactory factory,
        long songId,
        string path,
        string sourceType,
        string sourceRef)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        context.SongFiles.Add(new SongFile
        {
            SongId = songId,
            Path = path,
            Size = 30_000_000,
            Codec = "flac",
            Container = "flac",
            BitrateKbps = 900,
            SampleRate = 44_100,
            BitDepth = 16,
            Channels = 2,
            DurationMs = 248_100,
            QualityId = FlacQualityId,
            AcoustId = "acoust-1",
            FingerprintVerified = true,
            SourceType = sourceType,
            SourceRef = sourceRef,
            ImportedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            TagsWritten = """{"title":"Get Lucky","artist":"Daft Punk"}""",
            ReplayGainDb = -7.5,
            ReplayGainPeak = 0.98,
        });

        await context.SaveChangesAsync();
    }

    private static async Task<(long LibraryId, long FileId)> SeedReferenceFileAsync(WondarrAppFactory factory, long songId)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        var library = new ReferenceLibrary { Name = "My Music", RootPath = "/refs/My Music" };
        context.ReferenceLibraries.Add(library);
        await context.SaveChangesAsync();

        var file = new ReferenceFile
        {
            ReferenceLibraryId = library.Id,
            RelativePath = "Daft Punk/Get Lucky.flac",
            Size = 30_000_000,
            SongId = songId,
            Confidence = 0.95,
            State = ReferenceFileState.Identified,
            IdentifiedBy = "tag_mbid",
        };
        context.ReferenceFiles.Add(file);
        await context.SaveChangesAsync();

        return (library.Id, file.Id);
    }

    private static async Task SeedBlocklistAsync(WondarrAppFactory factory, long? songId, string key)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<WondarrDbContext>();

        context.Blocklist.Add(new BlocklistItem
        {
            SongId = songId,
            SourceType = "slskd",
            BlocklistKey = key,
            Reason = "Verification failed",
        });

        await context.SaveChangesAsync();
    }
}
