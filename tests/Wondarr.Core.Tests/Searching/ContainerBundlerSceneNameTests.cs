using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Metadata;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;
using Xunit;

namespace Wondarr.Core.Tests.Searching;

/// <summary>
/// A scene-named post bundles its other wanted songs (M-42a): the bundle reads the post's file names
/// with the release's artist and group tag the grabbed song's request carries.
/// </summary>
public sealed class ContainerBundlerSceneNameTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task A_scene_named_post_bundles_another_wanted_song_and_leaves_the_other_files_out()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha");
        var gamma = await host.SeedSongAsync("Gamma", artistId: await ArtistOfAsync(host, alpha));
        var delta = await host.SeedSongAsync("Delta", artistId: await ArtistOfAsync(host, alpha));
        var candidate = SceneCandidate(releaseArtist: "Aphex Twin", releaseGroup: "GRP");

        await using var context = host.Database.CreateContext(host.Time);

        var found = await ContainerBundler.FindAsync(context, alpha, candidate, Token);

        var bundled = found.Should().ContainSingle("only Gamma's file is in the post").Subject;
        bundled.Song.Id.Should().Be(gamma);
        bundled.Match.File.Path.Should().Be("03-aphex_twin-gamma-grp.flac");
        found.Should().NotContain(entry => entry.Song.Id == delta, "no file in the post is Delta");

        // The partner's candidate keeps the release's artist and group for the matcher after the grab.
        var partner = ContainerBundler.CandidateFor(candidate, bundled);

        partner.Release!.FileIndex.Should().Be(2);
        partner.Release.Song!.Title.Should().Be("Gamma");
        partner.Release.Song.ReleaseArtist.Should().Be("Aphex Twin");
        partner.Release.Song.ReleaseGroup.Should().Be("GRP");
    }

    [Fact]
    public async Task A_post_whose_request_names_no_artist_or_group_bundles_nothing_by_a_scene_name()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha");
        await host.SeedSongAsync("Gamma", artistId: await ArtistOfAsync(host, alpha));
        var candidate = SceneCandidate(releaseArtist: null, releaseGroup: null);

        await using var context = host.Database.CreateContext(host.Time);

        (await ContainerBundler.FindAsync(context, alpha, candidate, Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_release_bundles_its_live_songs_and_leaves_the_studio_ones_out()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha (Live)", versionFlags: ["live"]);
        var artist = await ArtistOfAsync(host, alpha);
        await host.SeedSongAsync("Gamma", artistId: artist);
        var delta = await host.SeedSongAsync("Delta (Live)", versionFlags: ["live"], artistId: artist);
        ContainerFile[] files =
        [
            new(0, "01-aphex_twin-alpha-grp.flac", SceneSize),
            new(1, "03-aphex_twin-gamma-grp.flac", SceneSize),
            new(2, "04-aphex_twin-delta-grp.flac", SceneSize),
        ];
        var candidate = SceneCandidate("Aphex Twin", "GRP", files, VersionFlags.Live, VersionFlags.Live);

        await using var context = host.Database.CreateContext(host.Time);

        var found = await ContainerBundler.FindAsync(context, alpha, candidate, Token);

        found.Should().ContainSingle("the live release holds Delta live; Gamma is a studio song").Which.Song.Id.Should().Be(delta);
    }

    [Fact]
    public async Task A_hyphenated_artist_the_release_name_got_wrong_bundles_through_the_partner_songs_own_artist()
    {
        await using var host = await SearchTestHost.CreateAsync();
        var alpha = await host.SeedSongAsync("Alpha");
        var gamma = await host.SeedSongAsync("Gamma", artistId: await ArtistOfAsync(host, alpha));
        ContainerFile[] files =
        [
            new(0, "01-aphex-twin-alpha-grp.flac", SceneSize),
            new(1, "03-aphex-twin-gamma-grp.flac", SceneSize),
        ];

        // The release name parsed the artist as "Aphex"; the song's credit is "Aphex Twin".
        var candidate = SceneCandidate("Aphex", "GRP", files);

        await using var context = host.Database.CreateContext(host.Time);

        var found = await ContainerBundler.FindAsync(context, alpha, candidate, Token);

        found.Should().ContainSingle().Which.Song.Id.Should().Be(gamma);
        var partner = ContainerBundler.CandidateFor(candidate, found[0]).Release!.Song!;
        partner.SongArtist.Should().Be("Aphex Twin");
        partner.SongAlbum.Should().Be("Selected Ambient Works", "the partner song's own album, read with the song");
    }

    /// <summary>200 s at about 900 kbps.</summary>
    private const long SceneSize = 200 * 900 * 1000L / 8;

    private static async Task<long> ArtistOfAsync(SearchTestHost host, long songId)
    {
        await using var context = host.Database.CreateContext(host.Time);

        return (await context.Songs.SingleAsync(song => song.Id == songId, Token)).PrimaryArtistId;
    }

    /// <summary>A clean FLAC post named the scene way, grabbed for track one.</summary>
    private static Candidate SceneCandidate(string? releaseArtist, string? releaseGroup) =>
        SceneCandidate(
            releaseArtist,
            releaseGroup,
            [
                new(0, "01-aphex_twin-alpha-grp.flac", SceneSize),
                new(1, "02-aphex_twin-beta-grp.flac", SceneSize),
                new(2, "03-aphex_twin-gamma-grp.flac", SceneSize),
            ]);

    private static Candidate SceneCandidate(
        string? releaseArtist,
        string? releaseGroup,
        ContainerFile[] files,
        VersionFlags songFlags = VersionFlags.None,
        VersionFlags releaseFlags = VersionFlags.None)
    {
        return SearchTestHost.Candidate(files[0].Path) with
        {
            Container = CandidateContainer.AlbumContainer,
            BlocklistKey = BlocklistKeys.Torrent(Hash, files[0].Path),
            Release = new ContainerRelease
            {
                IndexerId = 1,
                IndexerName = "Indexer",
                Title = "Aphex.Twin-Selected.Ambient.Works-CD-FLAC-1992-GRP",
                ReleaseId = "guid-scene",
                InfoHash = Hash,
                FileIndex = 0,
                Files = files,
                Song = new ContainerMatchRequest("Alpha", songFlags, null, 200_000, 36, true, releaseArtist, releaseGroup, releaseFlags),
            },
        };
    }
}
