using System.Net;
using System.Text.Json;
using Compilarr.Core.Metadata.Deezer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Compilarr.Api.Tests;

/// <summary>
/// The add dialog's audition button. Preview URLs are never stored, so every answer is a fresh
/// signed URL marked <c>no-store</c>.
/// </summary>
public sealed class PreviewApiTests
{
    private const string PreviewEndpoint = "/api/v1/preview";
    private const long DeezerId = 3135556;
    private const string Isrc = "GBDUW1300040";
    private const string PreviewUrl = "https://cdns-preview-d.dzcdn.net/stream/get-lucky.mp3?hdnea=exp=1";

    [Fact]
    public async Task A_deezer_id_answers_with_a_fresh_url_that_must_not_be_cached()
    {
        var deezer = Deezer();
        deezer.GetFreshPreviewUrlAsync(DeezerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(PreviewUrl));

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var response = await client.GetAsync(new Uri($"{PreviewEndpoint}?deezerId={DeezerId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var resource = await SongApiTests.ReadJsonAsync(response);
        resource.GetProperty("url").GetString().Should().Be(PreviewUrl);

        await deezer.Received(1).GetFreshPreviewUrlAsync(DeezerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_isrc_and_a_song_resolve_through_deezer_the_same_way()
    {
        var deezer = Deezer();
        deezer.GetTrackByIsrcAsync(Isrc, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeezerTrack?>(new DeezerTrack { Id = DeezerId, Title = "Get Lucky", Isrc = Isrc }));
        deezer.GetFreshPreviewUrlAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(PreviewUrl));

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var byIsrc = await client.GetAsync(new Uri($"{PreviewEndpoint}?isrc={Isrc}", UriKind.Relative));
        byIsrc.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SongApiTests.ReadJsonAsync(byIsrc)).GetProperty("url").GetString().Should().Be(PreviewUrl);

        // A stored song without a Deezer id is bridged through its first ISRC.
        var songId = await SongApiTests.SeedSongAsync(factory, "Get Lucky", mbRecordingId: null, monitored: true);
        await GiveSongAnIsrcAsync(factory, songId, Isrc);

        using var bySong = await client.GetAsync(new Uri($"{PreviewEndpoint}?songId={songId}", UriKind.Relative));
        bySong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SongApiTests.ReadJsonAsync(bySong)).GetProperty("url").GetString().Should().Be(PreviewUrl);
    }

    [Fact]
    public async Task A_track_without_a_preview_and_a_bad_request_are_not_found_and_bad()
    {
        var deezer = Deezer();
        deezer.GetFreshPreviewUrlAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        using var factory = SongApiTests.FakeProviders(deezer: deezer);
        using var client = SongApiTests.Authenticated(factory);

        using var noPreview = await client.GetAsync(new Uri($"{PreviewEndpoint}?deezerId={DeezerId}", UriKind.Relative));
        noPreview.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var twoParameters = await client.GetAsync(
            new Uri($"{PreviewEndpoint}?deezerId={DeezerId}&isrc={Isrc}", UriKind.Relative));

        twoParameters.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        twoParameters.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var problem = await SongApiTests.ReadJsonAsync(twoParameters);
        problem.GetProperty("detail").GetString().Should().Contain("deezerId");

        using var noParameters = await client.GetAsync(new Uri(PreviewEndpoint, UriKind.Relative));
        noParameters.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var unknownSong = await client.GetAsync(new Uri($"{PreviewEndpoint}?songId=4242", UriKind.Relative));
        unknownSong.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_preview_endpoint_requires_the_api_key()
    {
        using var factory = SongApiTests.FakeProviders();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"{PreviewEndpoint}?deezerId={DeezerId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A Deezer client that answers nothing until a test says so.</summary>
    private static IDeezerClient Deezer() => Substitute.For<IDeezerClient>();

    /// <summary>Gives a seeded song an ISRC, so the preview can bridge it to Deezer.</summary>
    private static async Task GiveSongAnIsrcAsync(CompilarrAppFactory factory, long songId, string isrc)
    {
        using var scope = factory.Services.CreateScope();
        await using var context = scope.ServiceProvider.GetRequiredService<Compilarr.Core.Persistence.CompilarrDbContext>();

        var song = await context.Songs.FindAsync(songId);
        song!.Isrcs = [isrc];
        await context.SaveChangesAsync();
    }
}
