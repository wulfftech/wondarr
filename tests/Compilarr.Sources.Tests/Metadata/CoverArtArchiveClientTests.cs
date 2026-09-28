using System.Net;
using Compilarr.Core.Metadata;
using Compilarr.Core.Metadata.CoverArt;
using FluentAssertions;
using Xunit;

namespace Compilarr.Sources.Tests.Metadata;

/// <summary>
/// Contract tests for <see cref="CoverArtArchiveClient"/>. The service answers a <c>HEAD</c> with a
/// 307 to archive.org when art exists and a 404 when it does not, so the stub answers the same way.
/// </summary>
public sealed class CoverArtArchiveClientTests
{
    private const string ReleaseGroupId = "6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd";
    private const string ReleaseId = "6defd963-fe91-4550-b18e-82c685603c2b";

    private const string ArchiveOrgLocation =
        "https://archive.org/download/mbid-6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd/mbid-6b47c9a0-b9e1-3df9-a5e8-50a6ce0dbdbd-13587214964_thumb500.jpg";

    [Fact]
    public async Task Release_group_art_answers_with_the_stable_archive_url()
    {
        var (client, handler, _) = CreateClient(_ => Redirect());

        var url = await client.GetReleaseGroupFrontUrlAsync(ReleaseGroupId);

        // Deliberately not the Location: the archive.org copy moves, the Cover Art Archive URL does not.
        url.Should().Be($"https://coverartarchive.org/release-group/{ReleaseGroupId}/front-500");

        // One request, a HEAD, and no second hop: the redirect is the answer, not a step towards one.
        handler.Methods.Should().ContainSingle().Which.Should().Be(HttpMethod.Head);
        handler.Requests.Should().ContainSingle().Subject.AbsolutePath
            .Should().Be($"/release-group/{ReleaseGroupId}/front-500");
    }

    [Fact]
    public async Task Release_art_uses_the_release_endpoint()
    {
        var (client, handler, _) = CreateClient(_ => Redirect());

        var url = await client.GetReleaseFrontUrlAsync(ReleaseId.ToUpperInvariant());

        url.Should().Be($"https://coverartarchive.org/release/{ReleaseId}/front-500");
        handler.Requests.Should().ContainSingle().Subject.AbsolutePath.Should().Be($"/release/{ReleaseId}/front-500");
    }

    [Fact]
    public async Task Existing_art_is_cached_so_the_second_ask_makes_no_request()
    {
        var (client, handler, _) = CreateClient(_ => Redirect());

        var first = await client.GetReleaseGroupFrontUrlAsync(ReleaseGroupId);
        var second = await client.GetReleaseGroupFrontUrlAsync(ReleaseGroupId);

        handler.Requests.Should().ContainSingle();
        second.Should().Be(first);
    }

    [Fact]
    public async Task Missing_art_is_null_and_negative_cached()
    {
        var (client, handler, cache) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetReleaseGroupFrontUrlAsync(ReleaseGroupId)).Should().BeNull();
        (await client.GetReleaseGroupFrontUrlAsync(ReleaseGroupId)).Should().BeNull();

        handler.Requests.Should().ContainSingle();
        cache.SetCount.Should().Be(1);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public async Task Rejects_ids_that_are_not_musicbrainz_ids(string id)
    {
        var (client, handler, _) = CreateClient(_ => throw new InvalidOperationException("no request expected"));

        await FluentActions
            .Awaiting(() => client.GetReleaseGroupFrontUrlAsync(id))
            .Should().ThrowAsync<ArgumentException>();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_error_surfaces_as_a_metadata_provider_exception()
    {
        var (client, _, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await FluentActions
            .Awaiting(() => client.GetReleaseFrontUrlAsync(ReleaseId))
            .Should().ThrowAsync<MetadataProviderException>();

        exception.Which.Provider.Should().Be("coverartarchive");
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    private static HttpResponseMessage Redirect() => new(HttpStatusCode.TemporaryRedirect)
    {
        Headers = { Location = new Uri(ArchiveOrgLocation) },
    };

    private static (CoverArtArchiveClient Client, FixtureHttpMessageHandler Handler, InMemoryMetadataCache Cache)
        CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FixtureHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://coverartarchive.org/") };
        var cache = new InMemoryMetadataCache();

        return (new CoverArtArchiveClient(http, cache), handler, cache);
    }
}
