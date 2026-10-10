using System.Text.Json;
using Wondarr.Core.Albums;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.MusicBrainz;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Albums;

/// <summary>
/// The album add against a real migrated SQLite database and faked providers: the default release of
/// a release group, the tracklist with the library's copy beside it, and the one batch an add runs.
/// The fixtures are the real MusicBrainz answers recorded for this task.
/// </summary>
public sealed class AlbumServiceTests : IDisposable
{
    /// <summary>The release group of <c>A Night at the Opera</c>, as the recorded search names it.</summary>
    private const string ReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";

    /// <summary>The 1975 UK vinyl the tracklist fixture was recorded from.</summary>
    private const string ReleaseId = "6defd963-fe91-4550-b18e-82c685603c2b";

    /// <summary>Bohemian Rhapsody, track 11 of that release.</summary>
    private const string BohemianRecording = "b1a9c0e9-d987-4042-ae91-78d6a3267d69";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IMusicBrainzClient _musicBrainz = Substitute.For<IMusicBrainzClient>();
    private readonly IDeezerClient _deezer = Substitute.For<IDeezerClient>();
    private readonly IIdentityResolver _resolver = Substitute.For<IIdentityResolver>();
    private readonly ISongService _songs = Substitute.For<ISongService>();
    private readonly List<SongIdentity> _addedIdentities = [];
    private SongAddOptions? _addedOptions;

    /// <summary>The MusicBrainz client's own JSON: kebab-case keys, case-sensitive.</summary>
    private static readonly JsonSerializerOptions MusicBrainzJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
    };

    [Fact]
    public void The_default_release_is_the_earliest_CD_or_digital_release_of_the_group()
    {
        var releases = BrowseReleases();

        // 71 official releases. The 1975 originals are all 12" Vinyl, so they are out; the earliest
        // release whose every medium is CD or Digital Media is the 1986 Italian CD (date "1986", 12
        // tracks) — "1986" sorts before "1986-12-01", the two GB CDs of that day, and every later CD.
        var expected = "18677378-dfb9-40f5-bbb0-a492e4dc9906";

        AlbumService.DefaultRelease(releases)!.Id.Should().Be(expected);
    }

    [Fact]
    public async Task The_release_list_marks_one_default_and_carries_formats_and_track_counts()
    {
        var releases = BrowseReleases();

        _musicBrainz
            .GetReleasesForReleaseGroupAsync(ReleaseGroupId, Arg.Any<CancellationToken>())
            .Returns(releases);

        var listed = await NewService().GetReleasesAsync(ReleaseGroupId, CancellationToken.None);

        listed.Should().HaveCount(71);
        listed.Count(release => release.IsDefault).Should().Be(1);
        listed.Single(release => release.IsDefault).Id.Should().Be("18677378-dfb9-40f5-bbb0-a492e4dc9906");
        listed.Single(release => release.IsDefault).TrackCount.Should().Be(12);
        listed.Single(release => release.IsDefault).Formats.Should().Be("CD");

        var vinyl = listed.Single(release => release.Id == ReleaseId);
        vinyl.Formats.Should().Be("12\" Vinyl");
        vinyl.Date.Should().Be("1975-11-21");
        vinyl.Country.Should().Be("GB");
    }

    [Fact]
    public async Task The_tracklist_carries_the_isrcs_and_the_song_the_library_already_holds()
    {
        await using var context = await ContextAsync();
        _musicBrainz.GetReleaseAsync(ReleaseId, Arg.Any<CancellationToken>()).Returns(ReleaseWithIsrcs());

        var artist = new Artist { Name = "Queen", SortName = "Queen" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var owned = new Song
        {
            Title = "Bohemian Rhapsody",
            ArtistCredit = "Queen",
            PrimaryArtistId = artist.Id,
            MbRecordingId = BohemianRecording,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.Add(owned);
        await context.SaveChangesAsync();

        var tracks = await NewService()
            .GetTracklistAsync(new AlbumRef(AlbumRef.MusicBrainzSource, ReleaseId), CancellationToken.None);

        tracks.Should().NotBeNull();
        tracks!.Should().HaveCount(12);

        var bohemian = tracks.Single(track => track.Position == 11);
        bohemian.Title.Should().Be("Bohemian Rhapsody");
        bohemian.ArtistCredit.Should().Be("Queen");
        bohemian.LengthMs.Should().Be(355_106);
        bohemian.MbRecordingId.Should().Be(BohemianRecording);
        bohemian.Isrcs.Should().HaveCount(7);
        bohemian.Owned.Should().BeTrue();
        bohemian.SongId.Should().Be(owned.Id);
        bohemian.LibraryId.Should().Be(SeedData.DefaultLibraryId);

        tracks[0].Title.Should().StartWith("Death on Two Legs");
        tracks[0].Owned.Should().BeFalse();
        tracks[0].Disc.Should().Be(1);
    }

    [Fact]
    public async Task An_add_skips_owned_and_unselected_tracks_and_pins_the_batch_to_the_release()
    {
        await using var context = await ContextAsync();
        StubSongService();
        _musicBrainz.GetReleaseAsync(ReleaseId, Arg.Any<CancellationToken>()).Returns(ReleaseWithIsrcs());

        var artist = new Artist { Name = "Queen", SortName = "Queen" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        context.Songs.Add(new Song
        {
            Title = "Bohemian Rhapsody",
            ArtistCredit = "Queen",
            PrimaryArtistId = artist.Id,
            MbRecordingId = BohemianRecording,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        });
        await context.SaveChangesAsync();

        _resolver
            .GetIdentityAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new SongIdentity
            {
                Source = "musicbrainz",
                MbRecordingId = callInfo.ArgAt<string?>(0),
                Title = "Track",
                ArtistCredit = "Queen",
                Artists = [new IdentityArtist("Queen", "Queen", "q1", null, ArtistRole.Main, 0)],
                ReleaseOptions = [ReleaseOption(ReleaseId)],
            });

        // Twelve tracks, one already owned and one not selected: ten identities, one batch. The keys
        // name every track but the unselected one, the owned one included.
        var keys = ReleaseWithIsrcs().Media[0].Tracks
            .Select(track => track.Recording!.Id)
            .ToList();
        keys.RemoveAt(3);

        var progress = new List<string>();

        var result = await NewService().AddAsync(
            new AlbumAddRequest
            {
                Album = new AlbumRef(AlbumRef.MusicBrainzSource, ReleaseId),
                TrackKeys = keys,
            },
            message =>
            {
                progress.Add(message);

                return Task.CompletedTask;
            },
            CancellationToken.None);

        result.Added.Should().Be(10);
        result.Failed.Should().BeEmpty();
        result.AlreadyInLibrary.Should().Be(1);
        result.FiledByPolicy.Should().Be(0);
        result.Message.Should().Be("Added 10 of 11 tracks, 1 already in the library");

        _addedIdentities.Should().HaveCount(10);
        _addedIdentities.Should().NotContain(identity => identity.MbRecordingId == BohemianRecording);
        _addedOptions.Should().NotBeNull();
        _addedOptions!.AlbumReleaseId.Should().Be(ReleaseId);
        _addedOptions.AddedBy.Should().Be("album");
        _addedOptions.Monitored.Should().BeTrue();

        await _songs.Received(1).AddIdentitiesAsync(
            Arg.Any<IReadOnlyList<SongIdentity>>(),
            Arg.Any<SongAddOptions>(),
            Arg.Any<CancellationToken>());

        progress.Should().HaveCount(10);
        progress[0].Should().Be("Resolved 1 of 10 tracks");
        progress[9].Should().Be("Resolved 10 of 10 tracks");
    }

    [Fact]
    public async Task An_add_reports_a_track_neither_provider_knows_and_adds_the_rest()
    {
        await using var context = await ContextAsync();
        StubSongService();
        _musicBrainz.GetReleaseAsync(ReleaseId, Arg.Any<CancellationToken>()).Returns(ReleaseWithIsrcs());

        _resolver
            .GetIdentityAsync(Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string?>(0) == BohemianRecording
                ? null
                : new SongIdentity
                {
                    Source = "musicbrainz",
                    MbRecordingId = callInfo.ArgAt<string?>(0),
                    Title = "Track",
                    ArtistCredit = "Queen",
                    Artists = [new IdentityArtist("Queen", "Queen", "q1", null, ArtistRole.Main, 0)],
                    ReleaseOptions = [ReleaseOption(ReleaseId)],
                });

        var result = await NewService().AddAsync(
            new AlbumAddRequest { Album = new AlbumRef(AlbumRef.MusicBrainzSource, ReleaseId) },
            cancellationToken: CancellationToken.None);

        result.Added.Should().Be(11);
        result.Failed.Should().HaveCount(1);
        result.Failed[0].TrackKey.Should().Be(BohemianRecording);
        result.Failed[0].Title.Should().Be("Bohemian Rhapsody");
        result.Failed[0].Reason.Should().Be("Neither MusicBrainz nor Deezer knows this track");
        result.Message.Should().Contain("1 failed to resolve");
        _addedIdentities.Should().HaveCount(11);
    }

    [Fact]
    public async Task A_search_answers_from_musicbrainz_and_falls_back_to_deezer()
    {
        _musicBrainz
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

        var hits = await NewService().SearchAsync("Queen - A Night at the Opera", 20, CancellationToken.None);

        hits.Should().HaveCount(1);
        hits[0].Source.Should().Be(AlbumRef.MusicBrainzSource);
        hits[0].ReleaseGroupId.Should().Be(ReleaseGroupId);
        hits[0].Title.Should().Be("A Night at the Opera");
        hits[0].Artist.Should().Be("Queen");
        hits[0].Year.Should().Be("1975");
        hits[0].Type.Should().Be("Album");

        await _deezer.DidNotReceive().SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        _musicBrainz
            .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MbReleaseGroupSearchResult());
        _deezer
            .SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerAlbumSearchResult
            {
                Data =
                [
                    new DeezerAlbum
                    {
                        Id = 1007321681,
                        Title = "A Night at the Opera",
                        RecordType = "album",
                        ReleaseDate = "1975-10-31",
                        NbTracks = 12,
                        CoverMedium = "https://api.deezer.com/album/1007321681/image",
                        Artist = new DeezerArtist { Id = 412, Name = "Queen" },
                    },
                ],
            });

        var fallback = await NewService().SearchAsync("A Night at the Opera", 20, CancellationToken.None);

        fallback.Should().HaveCount(1);
        fallback[0].Source.Should().Be(AlbumRef.DeezerSource);
        fallback[0].DeezerAlbumId.Should().Be(1007321681);
        fallback[0].TrackCount.Should().Be(12);
        fallback[0].CoverUrl.Should().Be("https://api.deezer.com/album/1007321681/image");
    }

    [Fact]
    public async Task A_search_falls_back_to_deezer_and_names_musicbrainz_when_it_is_busy()
    {
        _musicBrainz
            .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbReleaseGroupSearchResult>(_ => throw BusyMusicBrainz());
        _deezer
            .SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerAlbumSearchResult
            {
                Data = [new DeezerAlbum { Id = 1007321681, Title = "A Night at the Opera", RecordType = "album" }],
            });

        var found = await NewService().SearchPartialAsync("A Night at the Opera", 20, CancellationToken.None);

        found.Items.Should().ContainSingle().Which.Source.Should().Be(AlbumRef.DeezerSource);
        found.FailedProviders.Should().Equal("musicbrainz");
        found.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task A_search_is_unavailable_when_both_providers_fail()
    {
        _musicBrainz
            .SearchReleaseGroupsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<MbReleaseGroupSearchResult>(_ => throw BusyMusicBrainz());
        _deezer
            .SearchAlbumsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<DeezerAlbumSearchResult>(_ => throw new HttpRequestException("connection refused"));

        var search = () => NewService().SearchPartialAsync("A Night at the Opera", 20, CancellationToken.None);

        var thrown = await search.Should().ThrowAsync<ProvidersUnavailableException>();
        thrown.Which.Providers.Should().Equal("musicbrainz", "deezer");
    }

    private static MetadataProviderException BusyMusicBrainz() =>
        new("musicbrainz", System.Net.HttpStatusCode.ServiceUnavailable, "MusicBrainz answered 503 for a GET request.");

    [Fact]
    public async Task The_album_of_a_song_is_its_pinned_release_and_nothing_else()
    {
        await using var context = await ContextAsync();
        var artist = new Artist { Name = "Queen", SortName = "Queen" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var pinned = new Song
        {
            Title = "Bohemian Rhapsody",
            ArtistCredit = "Queen",
            PrimaryArtistId = artist.Id,
            MbRecordingId = BohemianRecording,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "album",
            AlbumContext = new AlbumContext
            {
                Kind = AlbumContextKind.Album,
                AlbumTitle = "A Night at the Opera",
                AlbumArtist = "Queen",
                AlbumKey = ReleaseId,
                MbReleaseId = ReleaseId,
            },
        };
        var loose = new Song
        {
            Title = "I Go Crazy",
            ArtistCredit = "Queen",
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.AddRange(pinned, loose);
        await context.SaveChangesAsync();

        var service = NewService();

        (await service.GetAlbumForSongAsync(pinned.Id, CancellationToken.None))
            .Should().Be(new AlbumRef(AlbumRef.MusicBrainzSource, ReleaseId));
        (await service.GetAlbumForSongAsync(loose.Id, CancellationToken.None)).Should().BeNull();
        (await service.GetAlbumForSongAsync(0, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_add_rejects_an_unknown_library_before_anything_is_looked_up()
    {
        await using var context = await ContextAsync();

        var act = async () => await NewService().AddAsync(
            new AlbumAddRequest
            {
                Album = new AlbumRef(AlbumRef.MusicBrainzSource, ReleaseId),
                LibraryId = 4242,
            },
            cancellationToken: CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ArgumentException>();
        exception.Which.ParamName.Should().Be("libraryId");
        await _musicBrainz.DidNotReceive().GetReleaseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private AlbumService NewService() =>
        new(
            Context(),
            _musicBrainz,
            _deezer,
            _resolver,
            _songs,
            NullLogger<AlbumService>.Instance);

    private WondarrDbContext Context() => _database.CreateContext(_timeProvider);

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        return _database.CreateContext(_timeProvider);
    }

    /// <summary>The fake song service answers "added" for every identity it is handed, and remembers the batch.</summary>
    private void StubSongService()
    {
        _songs
            .AddIdentitiesAsync(
                Arg.Do<IReadOnlyList<SongIdentity>>(identities => _addedIdentities.AddRange(identities)),
                Arg.Do<SongAddOptions>(options => _addedOptions = options),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo
                .ArgAt<IReadOnlyList<SongIdentity>>(0)
                .Select(identity => new SongAddResult(identity, SongAddOutcome.Added, new Song()))
                .ToList());
    }

    private static ReleaseOption ReleaseOption(string releaseId) => new()
    {
        Key = releaseId,
        MbReleaseId = releaseId,
        MbReleaseGroupId = ReleaseGroupId,
        Title = "A Night at the Opera",
        AlbumArtist = "Queen",
        PrimaryType = "Album",
        Status = "Official",
        Date = "1975-11-21",
    };

    /// <summary>The 71 official releases of the fixture, as the client would deserialise them.</summary>
    private static List<MbRelease> BrowseReleases()
    {
        using var document = JsonDocument.Parse(ReadFixture("musicbrainz", "browse-releases-by-group-a-night-at-the-opera.json"));

        return document.RootElement
            .GetProperty("releases")
            .EnumerateArray()
            .Select(element => element.Deserialize<MbRelease>(MusicBrainzJson)!)
            .ToList();
    }

    /// <summary>The release lookup with <c>inc=isrcs</c>: twelve tracks, each with its ISRCs.</summary>
    private static MbRelease ReleaseWithIsrcs() =>
        JsonSerializer.Deserialize<MbRelease>(
            ReadFixture("musicbrainz", "release-a-night-at-the-opera-isrcs.json"),
            MusicBrainzJson)!;

    /// <summary>Reads one recorded fixture, walking up to the repository's <c>tests/fixtures</c>.</summary>
    private static string ReadFixture(string provider, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        var path = directory is null
            ? null
            : Path.Combine(directory.FullName, "tests", "fixtures", provider, name);

        return File.Exists(path)
            ? File.ReadAllText(path!)
            : throw new FileNotFoundException($"Could not find tests/fixtures/{provider}/{name} above {AppContext.BaseDirectory}.");
    }
}
