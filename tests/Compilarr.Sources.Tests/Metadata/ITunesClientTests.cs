using System.Net;
using System.Text;
using Compilarr.Core.Metadata;
using Compilarr.Core.Metadata.ITunes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Compilarr.Sources.Tests.Metadata;

/// <summary>
/// Contract tests for <see cref="ITunesClient"/> against the responses recorded on 2026-09-28
/// (see <c>docs/research/research_metadata_plex.md</c> §1.2.1).
/// </summary>
public sealed class ITunesClientTests
{
    [Fact]
    public async Task Search_parses_the_recorded_results()
    {
        var (client, handler, _) = CreateClient(_ => ITunesFixtures.Json(ITunesFixtures.Read("search-queen-bohemian-rhapsody.json")));

        var results = await client.SearchSongsAsync("queen bohemian rhapsody", limit: 10);

        results.Should().HaveCount(5);

        var first = results[0];
        first.TrackId.Should().Be(6781024437);
        first.TrackName.Should().Be("Bohemian Rhapsody");
        first.ArtistName.Should().Be("Queen");
        first.CollectionName.Should().Be("A Night At The Opera (Deluxe Edition)");
        first.TrackTimeMillis.Should().Be(355155);
        first.ArtworkUrl100.Should().NotBeNullOrEmpty();
        first.PreviewUrl.Should().NotBeNullOrEmpty();

        var uri = handler.Requests.Should().ContainSingle().Subject;
        uri.AbsolutePath.Should().Be("/search");

        var decoded = Uri.UnescapeDataString(uri.Query);
        decoded.Should().Contain("term=queen bohemian rhapsody");
        decoded.Should().Contain("entity=song");
        decoded.Should().Contain("limit=10");
        decoded.Should().Contain("country=US");
    }

    [Fact]
    public async Task A_lookup_of_an_unknown_id_is_null()
    {
        var (client, handler, _) = CreateClient(_ => ITunesFixtures.Json(ITunesFixtures.Read("lookup-unknown.json")));

        (await client.LookupAsync(123456)).Should().BeNull();
        (await client.LookupAsync(123456)).Should().BeNull();

        handler.Requests.Should().ContainSingle().Subject.Query.Should().Be("?id=123456");
    }

    [Fact]
    public async Task A_search_with_no_results_is_empty()
    {
        var (client, _, _) = CreateClient(_ => ITunesFixtures.Json(ITunesFixtures.Read("lookup-unknown.json")));

        (await client.SearchSongsAsync("nothing at all")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/100x100bb.jpg", 600,
        "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/600x600bb.jpg")]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/100x100bb.jpg", 3000,
        "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/8b/0a/ea/x/3000x3000bb.jpg")]
    [InlineData("https://example.test/cover.jpg", 600, "https://example.test/cover.jpg")]
    public void Artwork_urls_are_rewritten_to_the_size_wanted(string source, int size, string expected)
    {
        ITunesClient.ToArtworkUrl(source, size).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Artwork_urls_that_are_missing_stay_null(string? source)
    {
        ITunesClient.ToArtworkUrl(source).Should().BeNull();
    }

    [Fact]
    public async Task Forbidden_is_not_retried()
    {
        // iTunes answers 403 once the caller goes over ~20 requests a minute; retrying spends more of
        // the budget, so the client must surface it after a single attempt.
        var handler = new FixtureHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await using var provider = BuildProvider(handler);

        var exception = await FluentActions
            .Awaiting(() => provider.GetRequiredService<IITunesClient>().LookupAsync(123456))
            .Should().ThrowAsync<MetadataProviderException>();

        exception.Which.Provider.Should().Be("itunes");
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        handler.Requests.Should().ContainSingle();
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCompilarrMetadata(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataCache, InMemoryMetadataCache>();

        // Only tests lower the retry delay; two seconds is the shipped value.
        services.Configure<MetadataOptions>(options => options.RetryBaseDelay = TimeSpan.FromMilliseconds(10));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StubHttpMessageHandlerFilter(primaryHandler));

        return services.BuildServiceProvider();
    }

    private static (ITunesClient Client, FixtureHttpMessageHandler Handler, InMemoryMetadataCache Cache) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FixtureHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://itunes.apple.com/") };
        var cache = new InMemoryMetadataCache();

        return (new ITunesClient(http, cache), handler, cache);
    }
}

/// <summary>The recorded iTunes responses, copied next to the test binaries.</summary>
internal static class ITunesFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "itunes", name));

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
