using System.Net;
using System.Text;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.Deezer;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Wondarr.Sources.Tests.Metadata;

/// <summary>
/// Contract tests for <see cref="DeezerClient"/> against the responses recorded on 2026-09-28
/// (see <c>docs/research/research_metadata_plex.md</c> §1.2.1).
/// </summary>
public sealed class DeezerClientTests
{
    private const long BohemianRhapsodyTrackId = 3541552051;
    private const long BohemianRhapsodyAlbumId = 816377711;

    /// <summary>Deezer reports a quota as HTTP 200 with an error object carrying code 4.</summary>
    private const string QuotaBody =
        """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""";

    /// <summary>Any other error code, which the client cannot recover from.</summary>
    private const string OtherErrorBody =
        """{"error":{"type":"OAuthException","message":"Invalid OAuth access token.","code":300}}""";

    /// <summary>A track carrying a signed preview URL, cut down from the recorded search hit.</summary>
    private const string TrackWithPreviewBody =
        """{"id":66609426,"title":"Get Lucky","isrc":"USQX91300809","duration":248,"preview":"https://cdnt-preview.dzcdn.net/api/1/1/1/b/f/0/1bf80a82992903ff685ba1b7275223f8.mp3?hdnea=exp=1790591831","artist":{"id":27,"name":"Daft Punk"},"album":{"id":6516139,"title":"Get Lucky","cover_xl":"https://cdn-images.dzcdn.net/images/cover/bc49adb87758e0c8c4e508a9c5cce85d/1000x1000-000000-80-0-0.jpg"}}""";

    [Fact]
    public async Task Search_parses_durations_isrcs_and_album_covers()
    {
        var (client, _, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("search-plain-get-lucky.json")));

        var result = await client.SearchTracksAsync("daft punk get lucky");

        result.Total.Should().Be(74);
        result.Data.Should().HaveCountGreaterThanOrEqualTo(10);

        var first = result.Data[0];
        first.Title.Should().Be("Get Lucky (Radio Edit - feat. Pharrell Williams and Nile Rodgers)");
        first.TitleVersion.Should().Be("(Radio Edit - feat. Pharrell Williams and Nile Rodgers)");
        first.Duration.Should().Be(248);
        first.Isrc.Should().Be("USQX91300809");
        first.ExplicitLyrics.Should().BeFalse();
        first.Artist.Name.Should().Be("Daft Punk");
        first.Album.CoverXl.Should().NotBeNullOrEmpty();

        result.Data[1].Album.Title.Should().Be("Random Access Memories");
    }

    [Fact]
    public async Task Search_sends_a_plain_text_query()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("search-plain-get-lucky.json")));

        await client.SearchTracksAsync("daft punk get lucky");

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be("/search");

        var decoded = Uri.UnescapeDataString(uri.Query);
        decoded.Should().Contain("q=daft punk get lucky");

        // Deezer's field syntax answers with nothing at all, so the query must stay plain text.
        decoded.Should().NotContain("artist:");
        decoded.Should().NotContain("track:");
        decoded.Should().Contain("limit=25");
    }

    [Fact]
    public async Task Isrc_lookup_finds_the_track_and_caches_it()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("track-isrc-GBUM71029604.json")));

        var track = await client.GetTrackByIsrcAsync("gbum71029604");
        var again = await client.GetTrackByIsrcAsync("gbum71029604");

        track.Should().NotBeNull();
        track!.Id.Should().Be(BohemianRhapsodyTrackId);
        track.Duration.Should().Be(358);
        track.Isrc.Should().Be("GBUM71029604");
        track.Album.Id.Should().Be(BohemianRhapsodyAlbumId);
        track.Preview.Should().BeEmpty();

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/track/isrc:GBUM71029604");
        again!.Id.Should().Be(BohemianRhapsodyTrackId);
    }

    [Fact]
    public async Task A_playlist_page_parses_its_tracks_and_total()
    {
        var (client, handler, _) = CreateClient(_ =>
            DeezerFixtures.Json(DeezerFixtures.Read("playlist-908622995-tracks-p0.json")));

        var page = await client.GetPlaylistTracksAsync(908622995, 0, 100);

        page.Should().NotBeNull();
        page!.Total.Should().Be(50);
        page.Data.Should().HaveCount(2);

        var first = page.Data[0];
        first.Id.Should().Be(116348632);
        first.Title.Should().Be("Hey Jude (Remastered 2015)");
        first.Isrc.Should().Be("GBUM71505902");
        first.Duration.Should().Be(429);
        first.Artist.Name.Should().Be("The Beatles");
        first.Album.Title.Should().Be("1 (Remastered 2015)");

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be("/playlist/908622995/tracks");
        uri.Query.Should().Contain("index=0");
        uri.Query.Should().Contain("limit=100");
    }

    [Fact]
    public async Task An_unknown_or_private_playlist_is_null_and_negative_cached()
    {
        var (client, handler, cache) = CreateClient(_ =>
            DeezerFixtures.Json(DeezerFixtures.Read("playlist-unknown.json")));

        (await client.GetPlaylistTracksAsync(908622995, 0, 100)).Should().BeNull();
        (await client.GetPlaylistTracksAsync(908622995, 0, 100)).Should().BeNull();

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/playlist/908622995/tracks");
        cache.SetCount.Should().Be(1);
    }

    [Fact]
    public async Task An_artist_top_page_parses_its_tracks_without_isrcs()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("artist-27-top-3.json")));

        var page = await client.GetArtistTopAsync(27, 3);

        page.Should().NotBeNull();
        page!.Total.Should().Be(100);
        page.Data.Should().HaveCount(3);

        var first = page.Data[0];
        first.Id.Should().Be(67238732);
        first.Title.Should().Be("Instant Crush (feat. Julian Casablancas)");
        first.Duration.Should().Be(337);
        first.Isrc.Should().BeNull();
        first.Artist.Name.Should().Be("Daft Punk");
        first.Album.Title.Should().Be("Random Access Memories");

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/artist/27/top");
        handler.Requests[0].Query.Should().Contain("limit=3");
    }

    [Fact]
    public async Task An_artist_search_parses_names_and_fan_counts()
    {
        var (client, handler, _) = CreateClient(_ =>
            DeezerFixtures.Json(DeezerFixtures.Read("search-artist-daft-punk.json")));

        var search = await client.SearchArtistsAsync("daft punk", 3);

        search.Total.Should().Be(16);
        search.Data.Should().HaveCount(3);
        search.Data[0].Id.Should().Be(27);
        search.Data[0].Name.Should().Be("Daft Punk");
        search.Data[0].NbFan.Should().Be(5_217_271);
        search.Data[1].Name.Should().Be("Daft Punk - Stardust");

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be("/search/artist");
        Uri.UnescapeDataString(uri.Query).Should().Contain("q=daft punk");
        uri.Query.Should().Contain("limit=3");
    }

    [Fact]
    public async Task A_fresh_preview_bypasses_the_cache()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("track-isrc-GBUM71029604.json")));

        // The first call caches the track — preview URL and all — and the second must not be served
        // from it: that preview is signed and has expired.
        (await client.GetTrackAsync(BohemianRhapsodyTrackId)).Should().NotBeNull();
        (await client.GetFreshPreviewUrlAsync(BohemianRhapsodyTrackId)).Should().BeNull();

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].AbsolutePath.Should().Be($"/track/{BohemianRhapsodyTrackId}");
    }

    [Fact]
    public async Task A_track_with_a_preview_returns_its_url()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(TrackWithPreviewBody));

        var url = await client.GetFreshPreviewUrlAsync(66609426);

        url.Should().Be("https://cdnt-preview.dzcdn.net/api/1/1/1/b/f/0/1bf80a82992903ff685ba1b7275223f8.mp3?hdnea=exp=1790591831");
        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be("/track/66609426");
    }

    [Fact]
    public async Task An_unknown_id_is_null_and_negative_cached()
    {
        var (client, handler, cache) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("track-unknown.json")));

        // Deezer serves the "no data" body with HTTP 200, not 404.
        (await client.GetTrackAsync(BohemianRhapsodyTrackId)).Should().BeNull();
        (await client.GetTrackAsync(BohemianRhapsodyTrackId)).Should().BeNull();
        (await client.GetAlbumAsync(BohemianRhapsodyAlbumId)).Should().BeNull();

        handler.Requests.Should().HaveCount(2);
        cache.SetCount.Should().Be(2);
    }

    [Fact]
    public async Task An_album_parses_its_record_type_cover_and_upc()
    {
        var (client, handler, _) = CreateClient(_ => DeezerFixtures.Json(DeezerFixtures.Read("album-816377711.json")));

        var album = await client.GetAlbumAsync(BohemianRhapsodyAlbumId);

        album.Should().NotBeNull();
        album!.RecordType.Should().Be("album");
        album.Upc.Should().Be("602488030915");
        album.CoverXl.Should().NotBeNullOrEmpty();
        album.NbTracks.Should().Be(80);

        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be($"/album/{BohemianRhapsodyAlbumId}");
    }

    [Fact]
    public async Task Another_error_code_surfaces_as_a_provider_exception()
    {
        var (client, _, _) = CreateClient(_ => DeezerFixtures.Json(OtherErrorBody));

        var exception = await FluentActions
            .Awaiting(() => client.GetTrackAsync(BohemianRhapsodyTrackId))
            .Should().ThrowAsync<MetadataProviderException>();

        exception.Which.Provider.Should().Be("deezer");
    }

    [Fact]
    public async Task The_quota_handler_turns_a_code_four_body_into_a_throttle()
    {
        var handler = new DeezerQuotaHandler
        {
            InnerHandler = new FixtureHttpMessageHandler(_ => DeezerFixtures.Json(QuotaBody)),
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.deezer.com/track/1"),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(DeezerQuotaHandler.RetryAfterSeconds));
    }

    [Fact]
    public async Task The_quota_handler_leaves_every_other_body_alone()
    {
        var handler = new DeezerQuotaHandler
        {
            InnerHandler = new FixtureHttpMessageHandler(_ => DeezerFixtures.Json(DeezerFixtures.Read("track-unknown.json"))),
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.deezer.com/track/1"),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken.None)).Should().Contain("\"code\":800");
    }

    [Fact]
    public async Task A_quota_error_is_retried_and_the_second_answer_wins()
    {
        var handler = new ScriptedHttpMessageHandler(
            _ => DeezerFixtures.Json(QuotaBody),
            _ => DeezerFixtures.Json(DeezerFixtures.Read("track-isrc-GBUM71029604.json")));

        await using var provider = BuildProvider(handler, TimeSpan.FromMilliseconds(10));

        var track = await provider.GetRequiredService<IDeezerClient>().GetTrackAsync(BohemianRhapsodyTrackId);

        track.Should().NotBeNull();
        track!.Id.Should().Be(BohemianRhapsodyTrackId);

        // Exactly two attempts: the 429 the quota handler produced, then the real answer.
        handler.Timestamps.Should().HaveCount(2);
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler primaryHandler, TimeSpan retryBaseDelay)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddWondarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        services.Configure<MetadataOptions>(options => options.RetryBaseDelay = retryBaseDelay);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primaryHandler));

        return services.BuildServiceProvider();
    }

    private static (DeezerClient Client, FixtureHttpMessageHandler Handler, InMemoryMetadataCache Cache) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FixtureHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/") };
        var cache = new InMemoryMetadataCache();

        return (new DeezerClient(http, cache), handler, cache);
    }
}

/// <summary>The recorded Deezer responses, copied next to the test binaries.</summary>
internal static class DeezerFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "deezer", name));

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
