using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.ImportLists.Deezer;
using Wondarr.Core.Metadata.Deezer;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// The Deezer import-list providers (ADR-0012): the playlist link's shapes, the paging stop
/// conditions, the entries' ids, and the artist resolved by name or link.
/// </summary>
public sealed class DeezerProvidersTests
{
    private const long PlaylistId = 908622995;

    [Theory]
    [InlineData("908622995")]
    [InlineData("https://www.deezer.com/playlist/908622995")]
    [InlineData("https://www.deezer.com/en/playlist/908622995?utm_source=chat")]
    [InlineData("deezer.com/playlist/908622995")]
    public void Accepts_a_bare_id_and_the_playlist_links_shapes(string value)
    {
        var provider = new DeezerPlaylistProvider(Client());

        provider.Validate(Settings(("playlist", value)), null).Should().BeEmpty();
    }

    [Fact]
    public void Refuses_a_short_link_with_its_own_message()
    {
        var provider = new DeezerPlaylistProvider(Client());

        provider.Validate(Settings(("playlist", "https://deezer.page.link/AbCdEf")), null).Should()
            .ContainSingle().Which.Should().Be("Open the short link in a browser and paste the full playlist link.");
    }

    [Fact]
    public void Refuses_something_that_is_neither_a_link_nor_an_id()
    {
        var provider = new DeezerPlaylistProvider(Client());

        provider.Validate(Settings(("playlist", "https://open.spotify.com/playlist/abc")), null).Should()
            .ContainSingle().Which.Should().Contain("does not look like a Deezer playlist");
    }

    [Fact]
    public void Refuses_an_empty_playlist_field()
    {
        var provider = new DeezerPlaylistProvider(Client());

        provider.Validate(Settings(), null).Should().ContainSingle().Which.Should().Contain("Enter a Deezer playlist");
    }

    [Fact]
    public async Task Stops_paging_on_a_short_page()
    {
        var deezer = Client();
        deezer.GetPlaylistTracksAsync(PlaylistId, 0, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns(Page(tracks: 2, total: 50));

        var result = await Fetch(new DeezerPlaylistProvider(deezer), ("playlist", PlaylistId.ToString()));

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(2);
        await deezer.Received(1).GetPlaylistTracksAsync(
            PlaylistId,
            0,
            DeezerPlaylistProvider.PageSize,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stops_paging_when_the_total_is_reached()
    {
        var deezer = Client();
        deezer.GetPlaylistTracksAsync(PlaylistId, 0, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns(Page(DeezerPlaylistProvider.PageSize, DeezerPlaylistProvider.PageSize));

        var result = await Fetch(new DeezerPlaylistProvider(deezer), ("playlist", PlaylistId.ToString()));

        result.Entries.Should().HaveCount(DeezerPlaylistProvider.PageSize);
        await deezer.DidNotReceive().GetPlaylistTracksAsync(
            PlaylistId,
            DeezerPlaylistProvider.PageSize,
            DeezerPlaylistProvider.PageSize,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pages_until_the_total_is_reached()
    {
        var deezer = Client();
        deezer.GetPlaylistTracksAsync(PlaylistId, 0, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns(Page(DeezerPlaylistProvider.PageSize, 150));
        deezer.GetPlaylistTracksAsync(PlaylistId, DeezerPlaylistProvider.PageSize, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns(Page(50, 150, firstId: 1001));

        var result = await Fetch(new DeezerPlaylistProvider(deezer), ("playlist", PlaylistId.ToString()));

        result.Entries.Should().HaveCount(150);
        result.Entries[99].DeezerId.Should().Be(100);
        result.Entries[100].DeezerId.Should().Be(1001);
    }

    [Fact]
    public async Task Entries_carry_the_isrc_and_the_deezer_id()
    {
        var deezer = Client();
        deezer.GetPlaylistTracksAsync(PlaylistId, 0, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns(new DeezerTrackPage
            {
                Total = 1,
                Data =
                [
                    new DeezerTrack
                    {
                        Id = 116348632,
                        Title = "Hey Jude (Remastered 2015)",
                        Duration = 429,
                        Isrc = "GBUM71505902",
                        Artist = new DeezerArtist { Id = 1, Name = "The Beatles" },
                        Album = new DeezerAlbumRef { Id = 12047956, Title = "1 (Remastered 2015)" },
                    },
                ],
            });

        var result = await Fetch(new DeezerPlaylistProvider(deezer), ("playlist", PlaylistId.ToString()));

        var entry = result.Entries.Should().ContainSingle().Subject;
        entry.ExternalId.Should().Be("deezer:116348632");
        entry.Artist.Should().Be("The Beatles");
        entry.Title.Should().Be("Hey Jude (Remastered 2015)");
        entry.Album.Should().Be("1 (Remastered 2015)");
        entry.DurationMs.Should().Be(429_000);
        entry.Isrc.Should().Be("GBUM71505902");
        entry.DeezerId.Should().Be(116348632);
    }

    [Fact]
    public async Task An_unknown_or_private_playlist_fails_with_its_message()
    {
        var deezer = Client();
        deezer.GetPlaylistTracksAsync(PlaylistId, 0, DeezerPlaylistProvider.PageSize, Arg.Any<CancellationToken>())
            .Returns((DeezerTrackPage?)null);

        var result = await Fetch(new DeezerPlaylistProvider(deezer), ("playlist", PlaylistId.ToString()));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The Deezer playlist was not found, or it is private.");
    }

    [Fact]
    public async Task An_artist_is_found_by_its_exact_name_before_the_most_fanned()
    {
        var deezer = Client();
        deezer.SearchArtistsAsync("daft punk", DeezerArtistTopProvider.SearchLimit, Arg.Any<CancellationToken>())
            .Returns(new DeezerArtistSearchResult
            {
                Total = 2,
                Data =
                [
                    new DeezerArtistSearchHit { Id = 1477045, Name = "Daft Punk - Stardust", NbFan = 8_614 },
                    new DeezerArtistSearchHit { Id = 27, Name = "Daft Punk", NbFan = 5_217_271 },
                ],
            });
        deezer.GetArtistTopAsync(27, 10, Arg.Any<CancellationToken>()).Returns(Page(2, 2));

        var result = await Fetch(
            new DeezerArtistTopProvider(deezer),
            ("artist", "daft punk"),
            ("count", "10"));

        result.Success.Should().BeTrue();
        result.Entries.Should().HaveCount(2);
        await deezer.Received(1).GetArtistTopAsync(27, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_artist_without_an_exact_match_takes_the_most_fanned_of_the_first_five()
    {
        var deezer = Client();
        deezer.SearchArtistsAsync("Daft Punkn", DeezerArtistTopProvider.SearchLimit, Arg.Any<CancellationToken>())
            .Returns(new DeezerArtistSearchResult
            {
                Total = 2,
                Data =
                [
                    new DeezerArtistSearchHit { Id = 176017197, Name = "Daft Punkn Experience", NbFan = 63 },
                    new DeezerArtistSearchHit { Id = 27, Name = "Daft Punkn", NbFan = 5_217_271 },
                ],
            });
        deezer.GetArtistTopAsync(27, 10, Arg.Any<CancellationToken>()).Returns(Page(1, 1));

        var result = await Fetch(
            new DeezerArtistTopProvider(deezer),
            ("artist", "Daft Punkn"),
            ("count", "10"));

        result.Entries.Should().ContainSingle().Which.DeezerId.Should().Be(1);
    }

    [Fact]
    public async Task An_artist_link_is_used_as_it_is()
    {
        var deezer = Client();
        deezer.GetArtistTopAsync(27, 3, Arg.Any<CancellationToken>()).Returns(Page(1, 1));

        var result = await Fetch(
            new DeezerArtistTopProvider(deezer),
            ("artist", "https://www.deezer.com/artist/27"),
            ("count", "3"));

        result.Success.Should().BeTrue();
        await deezer.DidNotReceive().SearchArtistsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_artist_name_fails_with_its_message()
    {
        var deezer = Client();
        deezer.SearchArtistsAsync("Nobody", DeezerArtistTopProvider.SearchLimit, Arg.Any<CancellationToken>())
            .Returns(new DeezerArtistSearchResult());

        var result = await Fetch(
            new DeezerArtistTopProvider(deezer),
            ("artist", "Nobody"),
            ("count", "10"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Deezer has no artist named 'Nobody'.");
    }

    [Fact]
    public async Task Top_track_entries_carry_no_isrc()
    {
        var deezer = Client();
        deezer.GetArtistTopAsync(27, 10, Arg.Any<CancellationToken>()).Returns(new DeezerTrackPage
        {
            Total = 1,
            Data =
            [
                new DeezerTrack
                {
                    Id = 67238732,
                    Title = "Instant Crush (feat. Julian Casablancas)",
                    Duration = 337,
                    Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
                    Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
                },
            ],
        });

        var result = await Fetch(
            new DeezerArtistTopProvider(deezer),
            ("artist", "27"),
            ("count", "10"));

        var entry = result.Entries.Should().ContainSingle().Subject;
        entry.ExternalId.Should().Be("deezer:67238732");
        entry.Artist.Should().Be("Daft Punk");
        entry.Title.Should().Be("Instant Crush (feat. Julian Casablancas)");
        entry.DurationMs.Should().Be(337_000);
        entry.Isrc.Should().BeNull();
        entry.DeezerId.Should().Be(67238732);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("ten")]
    public void Refuses_a_count_outside_one_to_a_hundred(string count)
    {
        var provider = new DeezerArtistTopProvider(Client());

        provider.Validate(Settings(("artist", "Daft Punk"), ("count", count)), null).Should()
            .ContainSingle().Which.Should().Contain("between 1 and 100");
    }

    [Fact]
    public void Refuses_a_missing_artist_or_count()
    {
        var provider = new DeezerArtistTopProvider(Client());

        provider.Validate(Settings(), null).Should().HaveCount(2);
        provider.Validate(Settings(("artist", "Daft Punk")), null).Should().HaveCount(1);
    }

    [Fact]
    public void Accepts_a_count_within_one_to_a_hundred()
    {
        var provider = new DeezerArtistTopProvider(Client());

        provider.Validate(Settings(("artist", "Daft Punk"), ("count", "25")), null).Should().BeEmpty();
    }

    private static IDeezerClient Client() => Substitute.For<IDeezerClient>();

    private static DeezerTrackPage Page(int tracks, int total, int firstId = 1) => new()
    {
        Total = total,
        Data = [.. Enumerable.Range(firstId, tracks).Select(index => new DeezerTrack
        {
            Id = index,
            Title = $"Track {index.ToString(CultureInfo.InvariantCulture)}",
            Duration = 200,
            Isrc = $"ISRC{index.ToString(CultureInfo.InvariantCulture)}",
            Artist = new DeezerArtist { Id = 27, Name = "Daft Punk" },
            Album = new DeezerAlbumRef { Id = 6575789, Title = "Random Access Memories" },
        })],
    };

    private static JsonElement Settings(params (string Name, string Value)[] fields)
    {
        var json = string.Concat(
            "{",
            string.Join(",", fields.Select(field => string.Create(
                CultureInfo.InvariantCulture,
                $"\"{field.Name}\":\"{field.Value}\""))),
            "}");

        // Clone: the document is disposed as soon as the expression ends, and the element outlives it.
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<ImportListFetchResult> Fetch(
        IImportListProvider provider,
        params (string Name, string Value)[] fields) =>
        await provider.FetchAsync(
            new ImportList { Type = provider.Type, Settings = JsonSerializer.Serialize(Settings(fields)) },
            CancellationToken.None);
}
