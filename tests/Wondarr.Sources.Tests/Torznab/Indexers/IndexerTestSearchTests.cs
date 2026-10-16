using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Indexers;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// An indexer's connection test reads caps and then sends one real search, and an indexer's own error
/// reason survives a non-success status. NZBFinder answers <c>t=caps</c> without looking at the key,
/// then refuses every search with HTTP 401 and <c>&lt;error code="102"/&gt;</c>; a test that only read
/// caps passed a free account that could never search.
/// </summary>
public sealed class IndexerTestSearchTests
{
    private const string PremiumOnly = """<error code="102" description="You need to be a premium member to use the API"/>""";

    private const string PremiumOnlyMessage =
        "The indexer rejected the API key (102: You need to be a premium member to use the API).";

    private const string EmptyChannel = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
          <channel><title>Example Indexer</title></channel>
        </rss>
        """;

    private static readonly ReleaseQuery Discovery = new("Daft Punk", "Discovery", 2001);

    private static JsonElement Settings() =>
        JsonSerializer.SerializeToElement(new
        {
            url = TorznabFixtures.BaseUrl,
            apiPath = "/api",
            apiKey = TorznabFixtures.ApiKey,
            categories = "3000",
        });

    private static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/xml"),
    };

    /// <summary>A handler that serves the caps fixture for <c>t=caps</c> and the given answer for every other request.</summary>
    private static RecordingHandler CapsThen(Func<HttpResponseMessage> search, string caps = "caps-prowlarr.xml") =>
        new(request => request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
            ? TorznabFixtures.Response(caps)
            : search());

    private static IIndexerType TypeOf(string dialect, HttpMessageHandler handler) =>
        dialect == "torznab" ? TorznabTest.TorznabType(handler) : TorznabTest.NewznabType(handler);

    private static IIndexerClient ClientOf(string dialect, HttpMessageHandler handler) =>
        dialect == "torznab" ? TorznabTest.TorznabClient(handler) : TorznabTest.NewznabClient(handler);

    private static Indexer RowOf(string dialect) => dialect == "torznab"
        ? TorznabFixtures.Indexer()
        : TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet);

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_test_search_refused_with_401_fails_with_the_indexers_reason(string dialect)
    {
        var handler = CapsThen(() => Xml(PremiumOnly, HttpStatusCode.Unauthorized));

        var result = await TypeOf(dialect, handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(PremiumOnlyMessage);
        result.Error.Should().NotContain(TorznabFixtures.ApiKey);
    }

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_test_search_with_an_empty_channel_passes(string dialect)
    {
        var handler = CapsThen(() => Xml(EmptyChannel));

        var result = await TypeOf(dialect, handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_test_search_answered_200_with_an_error_body_fails_with_its_description(string dialect)
    {
        var handler = CapsThen(() => Xml("""<error code="100" description="Incorrect user credentials"/>"""));

        var result = await TypeOf(dialect, handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The indexer rejected the API key (100: Incorrect user credentials).");
    }

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_test_search_answered_500_without_a_body_keeps_the_plain_status_message(string dialect)
    {
        var handler = CapsThen(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await TypeOf(dialect, handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The indexer answered 500.");
    }

    [Theory]
    [InlineData("<html><body>Bad gateway</body></html>")]
    [InlineData("<html><body><error>Bad gateway</error></body></html>")]
    [InlineData("<h1>Bad gateway")]
    [InlineData("""<error/>""")]
    [InlineData("")]
    public async Task A_non_success_body_that_is_not_an_indexer_error_keeps_the_plain_status_message(string body)
    {
        var handler = CapsThen(() => Xml(body, HttpStatusCode.BadGateway));

        var result = await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The indexer answered 502.");
    }

    [Fact]
    public async Task A_test_search_answered_200_with_something_that_is_not_xml_fails()
    {
        var handler = CapsThen(() => Xml("not xml at all"));

        var result = await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The indexer's answer is not valid XML.");
    }

    [Fact]
    public async Task The_test_sends_the_caps_request_and_then_one_search_for_a_single_result()
    {
        var handler = CapsThen(() => Xml(EmptyChannel));

        await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        TorznabTest.Query(handler.Requests[0])["t"].Should().Be("caps");

        var search = TorznabTest.Query(handler.Requests[1]);

        handler.Requests[1].GetLeftPart(UriPartial.Path).Should().Be("https://indexer.example/api");
        search["t"].Should().Be("search");
        search["q"].Should().Be("test");
        search["limit"].Should().Be("1");
        search["cat"].Should().Be("3000");
        search["apikey"].Should().Be(TorznabFixtures.ApiKey);
    }

    [Fact]
    public async Task A_test_never_asks_for_an_nzb()
    {
        var handler = CapsThen(() => Xml(EmptyChannel));

        await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        handler.Requests.Should().NotContain(url => url.Query.Contains("t=get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_indexer_whose_caps_offer_no_text_search_is_tested_with_the_search_a_normal_search_sends()
    {
        const string caps = """
            <caps>
              <limits max="100" default="100"/>
              <searching>
                <search available="no" supportedParams="q"/>
                <music-search available="yes" supportedParams="q,artist,album"/>
              </searching>
              <categories><category id="3000" name="Music" description="Music"/></categories>
            </caps>
            """;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal) ? Xml(caps) : Xml(EmptyChannel));

        var result = await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeTrue();

        var search = TorznabTest.Query(handler.Requests[1]);

        search["t"].Should().Be("music");
        search["artist"].Should().Be("test");
        search["album"].Should().Be("test");
        search["limit"].Should().Be("1");
    }

    [Fact]
    public async Task A_caps_request_the_indexer_refuses_with_an_error_body_fails_the_test_with_its_reason()
    {
        var handler = new RecordingHandler(_ => Xml(PremiumOnly, HttpStatusCode.Unauthorized));

        var result = await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(PremiumOnlyMessage);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_error_description_that_echoes_the_api_key_is_redacted()
    {
        var handler = CapsThen(() => Xml(
            $"""<error code="100" description="Key {TorznabFixtures.ApiKey} is not valid"/>""",
            HttpStatusCode.Unauthorized));

        var result = await TypeOf("newznab", handler).TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain(TorznabFixtures.ApiKey);
        result.Error.Should().Contain("(removed)");
    }

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_real_search_refused_with_401_shows_the_indexers_reason(string dialect)
    {
        var handler = CapsThen(() => Xml(PremiumOnly, HttpStatusCode.Unauthorized));

        var act = () => ClientOf(dialect, handler).SearchAsync(RowOf(dialect), Discovery, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Be(PremiumOnlyMessage);
    }

    [Theory]
    [InlineData("newznab")]
    [InlineData("torznab")]
    public async Task A_real_search_answered_500_without_a_body_keeps_the_plain_status_message(string dialect)
    {
        var handler = CapsThen(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => ClientOf(dialect, handler).SearchAsync(RowOf(dialect), Discovery, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Be("The indexer answered 500.");
    }

    [Fact]
    public async Task A_real_search_refused_with_429_and_a_non_key_error_names_its_code_and_description()
    {
        var handler = CapsThen(() => Xml("""<error code="500" description="Request limit reached"/>""", HttpStatusCode.TooManyRequests));

        var act = () => ClientOf("newznab", handler).SearchAsync(RowOf("newznab"), Discovery, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).Which.Message
            .Should().Be("The indexer answered an error (500: Request limit reached).");
    }
}
