using System.Net;
using Compilarr.Core.Metadata;
using Compilarr.Core.Metadata.CoverArt;
using Compilarr.Core.Metadata.Deezer;
using Compilarr.Core.Metadata.ITunes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Compilarr.Sources.Tests.Metadata;

/// <summary>
/// The cover-art chain, against fakes of the three clients: what matters here is the order, the
/// artist check and what happens when a provider fails.
/// </summary>
public sealed class CoverArtResolverTests
{
    private const string ReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";
    private const string ReleaseId = "6defd963-fe91-4550-b18e-82c685603c2b";
    private const string DeezerCover = "https://cdn-images.dzcdn.net/images/cover/abc/1000x1000-000000-80-0-0.jpg";
    private const string ITunesArtwork = "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/100x100bb.jpg";

    [Fact]
    public async Task Release_group_art_wins_and_later_providers_are_not_asked()
    {
        var (resolver, caa, deezer, itunes) = CreateResolver();

        caa.GetReleaseGroupFrontUrlAsync(ReleaseGroupId, Arg.Any<CancellationToken>())
            .Returns($"https://coverartarchive.org/release-group/{ReleaseGroupId}/front-500");

        var cover = await resolver.ResolveAsync(Request());

        cover.Should().Be(new CoverArt(
            $"https://coverartarchive.org/release-group/{ReleaseGroupId}/front-500",
            "coverartarchive"));

        await deezer.DidNotReceiveWithAnyArgs().SearchTracksAsync(default!, default, default);
        await itunes.DidNotReceiveWithAnyArgs().SearchSongsAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Release_art_is_used_when_the_release_group_has_none()
    {
        var (resolver, caa, _, _) = CreateResolver();

        caa.GetReleaseGroupFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        caa.GetReleaseFrontUrlAsync(ReleaseId, Arg.Any<CancellationToken>())
            .Returns($"https://coverartarchive.org/release/{ReleaseId}/front-500");

        var cover = await resolver.ResolveAsync(Request());

        cover!.Source.Should().Be("coverartarchive");
        cover.Url.Should().Be($"https://coverartarchive.org/release/{ReleaseId}/front-500");
    }

    [Fact]
    public async Task A_deezer_album_id_supplies_the_cover_when_the_archive_has_none()
    {
        var (resolver, caa, deezer, _) = CreateResolver();

        caa.GetReleaseGroupFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        caa.GetReleaseFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        deezer.GetAlbumAsync(816377711, Arg.Any<CancellationToken>())
            .Returns(new DeezerAlbum { Id = 816377711, Title = "Songs You Need", CoverXl = DeezerCover });

        var cover = await resolver.ResolveAsync(Request() with { DeezerAlbumId = 816377711 });

        cover.Should().Be(new CoverArt(DeezerCover, "deezer"));
    }

    [Fact]
    public async Task A_deezer_search_takes_the_first_hit_by_the_right_artist()
    {
        var (resolver, _, deezer, itunes) = CreateResolver();

        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult
            {
                Data =
                [
                    // The same-named single by a tribute act: right title, wrong artist.
                    Track("Get Lucky - Tribute to Daft Punk", "Get Lucky", "https://cdn.example.test/tribute.jpg"),
                    Track("Get Lucky", "Daft Punk", DeezerCover),
                ],
                Total = 2,
            });

        var cover = await resolver.ResolveAsync(Request() with { MbReleaseGroupId = null });

        cover.Should().Be(new CoverArt(DeezerCover, "deezer"));

        // The query is plain text and built from the album when there is one.
        await deezer.Received(1).SearchTracksAsync("Daft Punk Random Access Memories", 25, Arg.Any<CancellationToken>());
        await itunes.DidNotReceiveWithAnyArgs().SearchSongsAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task A_deezer_failure_hands_over_to_itunes()
    {
        var (resolver, _, deezer, itunes) = CreateResolver();

        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DeezerSearchResult>(new MetadataProviderException(
                "deezer",
                HttpStatusCode.BadGateway,
                "Deezer answered 502 for a GET request.")));

        itunes.SearchSongsAsync("Daft Punk Get Lucky", Arg.Any<int>(), "US", Arg.Any<CancellationToken>())
            .Returns([new ITunesTrack { TrackName = "Get Lucky", ArtistName = "Daft Punk", ArtworkUrl100 = ITunesArtwork }]);

        var cover = await resolver.ResolveAsync(Request() with { MbReleaseGroupId = null, Album = null });

        cover!.Source.Should().Be("itunes");
        cover.Url.Should().Be("https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/600x600bb.jpg");
    }

    [Fact]
    public async Task An_itunes_hit_by_another_artist_is_skipped()
    {
        var (resolver, _, deezer, itunes) = CreateResolver();

        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult());

        itunes.SearchSongsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ITunesTrack { TrackName = "Get Lucky", ArtistName = "Get Lucky Tribute Band", ArtworkUrl100 = "https://example.test/wrong/100x100bb.jpg" },
                new ITunesTrack { TrackName = "Get Lucky", ArtistName = "DAFT PUNK", ArtworkUrl100 = ITunesArtwork },
            ]);

        var cover = await resolver.ResolveAsync(Request() with { MbReleaseGroupId = null });

        cover.Should().Be(new CoverArt(
            "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/600x600bb.jpg",
            "itunes"));
    }

    [Fact]
    public async Task Nothing_anywhere_is_null()
    {
        var (resolver, _, _, _) = CreateResolver();

        var cover = await resolver.ResolveAsync(Request() with { MbReleaseGroupId = null, MbReleaseId = null });

        cover.Should().BeNull();
    }

    private static CoverArtRequest Request() => new()
    {
        MbReleaseGroupId = ReleaseGroupId,
        MbReleaseId = ReleaseId,
        Artist = "Daft Punk",
        Album = "Random Access Memories",
        Title = "Get Lucky",
    };

    private static DeezerTrack Track(string title, string artist, string coverXl) => new()
    {
        Id = 1,
        Title = title,
        Artist = new DeezerArtist { Id = 2, Name = artist },
        Album = new DeezerAlbumRef { Id = 3, Title = title, CoverXl = coverXl },
    };

    private static (CoverArtResolver Resolver, ICoverArtArchiveClient CoverArtArchive, IDeezerClient Deezer, IITunesClient ITunes)
        CreateResolver()
    {
        var coverArtArchive = Substitute.For<ICoverArtArchiveClient>();
        var deezer = Substitute.For<IDeezerClient>();
        var itunes = Substitute.For<IITunesClient>();

        // Fakes answer "nothing" unless a test says otherwise.
        coverArtArchive.GetReleaseGroupFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        coverArtArchive.GetReleaseFrontUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        deezer.GetAlbumAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((DeezerAlbum?)null);
        deezer.SearchTracksAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new DeezerSearchResult());
        itunes.SearchSongsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        itunes.LookupAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((ITunesTrack?)null);

        return (new CoverArtResolver(coverArtArchive, deezer, itunes, NullLogger<CoverArtResolver>.Instance),
            coverArtArchive,
            deezer,
            itunes);
    }
}
