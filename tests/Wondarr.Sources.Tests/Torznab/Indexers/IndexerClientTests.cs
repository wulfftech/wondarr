using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// The clients end to end over the fake handler: the URLs a search produces, the releases it returns,
/// the downloads it hands back, and the secrets it keeps out of the log.
/// </summary>
public sealed class IndexerClientTests
{
    private static readonly ReleaseQuery Discovery = new("Daft Punk", "Discovery", 2001);

    [Fact]
    public async Task A_search_reads_caps_then_asks_the_music_search_and_parses_the_answer()
    {
        var handler = RecordingHandler.ServingCapsAndResults("caps-prowlarr.xml", "results-torznab.xml");
        var client = TorznabTest.TorznabClient(handler);

        var releases = await client.SearchAsync(TorznabFixtures.Indexer(), Discovery, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].AbsoluteUri.Should().Be("https://indexer.example/api?t=caps&apikey=" + TorznabFixtures.ApiKey);
        handler.Requests[1].GetLeftPart(UriPartial.Path).Should().Be("https://indexer.example/api");
        TorznabTest.Query(handler.Requests[1])["t"].Should().Be("music");

        releases.Should().HaveCount(3);
        releases.Should().OnlyContain(release => release.IndexerId == 1 && release.IndexerName == "Example Indexer");
        releases.Should().OnlyContain(release => release.Protocol == DownloadProtocol.Torrent);
    }

    [Fact]
    public async Task Releases_under_the_minimum_seeders_are_skipped()
    {
        var handler = RecordingHandler.ServingCapsAndResults("caps-prowlarr.xml", "results-torznab.xml");
        var client = TorznabTest.TorznabClient(handler);
        var indexer = TorznabFixtures.Indexer();
        indexer.Settings = indexer.Settings.Replace("\"apiPath\"", "\"minimumSeeders\": 10, \"apiPath\"", StringComparison.Ordinal);

        var releases = await client.SearchAsync(indexer, Discovery, CancellationToken.None);

        // The fixture's three releases have 42, 7 and 11 seeders.
        releases.Select(release => release.Seeders).Should().BeEquivalentTo(new int?[] { 42, 11 });
    }

    [Fact]
    public async Task A_connection_test_of_a_row_with_a_relative_url_fails_instead_of_throwing()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsReader(RecordingHandler.Serving("caps-prowlarr.xml")));

        var settings = JsonSerializer.SerializeToElement(new { url = "indexer.example", apiKey = TorznabFixtures.ApiKey });

        var result = await type.TestAsync(settings, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("absolute http or https");
    }

    [Fact]
    public async Task A_second_search_makes_no_caps_request()
    {
        var handler = RecordingHandler.ServingCapsAndResults("caps-prowlarr.xml", "results-torznab.xml");
        var client = TorznabTest.TorznabClient(handler);
        var indexer = TorznabFixtures.Indexer();

        await client.SearchAsync(indexer, Discovery, CancellationToken.None);
        await client.SearchAsync(indexer, Discovery, CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests.Count(url => url.Query.Contains("t=caps", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task A_newznab_indexer_without_music_search_is_asked_with_a_plain_q()
    {
        var handler = RecordingHandler.ServingCapsAndResults("caps-q-only.xml", "results-newznab.xml");
        var client = TorznabTest.NewznabClient(handler);

        var releases = await client.SearchAsync(
            TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet),
            Discovery,
            CancellationToken.None);

        TorznabTest.Query(handler.Requests[1])["t"].Should().Be("search");
        releases.Should().HaveCount(2);
        releases.Should().OnlyContain(release => release.Protocol == DownloadProtocol.Usenet);
    }

    [Fact]
    public async Task A_download_url_that_redirects_to_a_magnet_returns_the_magnet()
    {
        var magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(magnet) },
        });
        var client = TorznabTest.TorznabClient(handler);

        var download = await client.DownloadAsync(
            TorznabFixtures.Indexer(),
            Release("https://indexer.example/download/2.torrent"),
            CancellationToken.None);

        download.MagnetUrl.Should().Be(magnet);
        download.Content.Should().BeNull();
    }

    [Fact]
    public async Task A_release_whose_download_url_is_already_a_magnet_needs_no_request()
    {
        var magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
        var handler = new RecordingHandler(_ => TorznabFixtures.Response("results-torznab.xml"));
        var client = TorznabTest.TorznabClient(handler);

        var download = await client.DownloadAsync(TorznabFixtures.Indexer(), Release(magnet), CancellationToken.None);

        download.MagnetUrl.Should().Be(magnet);
        download.Content.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_download_returns_the_bytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var client = TorznabTest.TorznabClient(handler);

        var download = await client.DownloadAsync(
            TorznabFixtures.Indexer(),
            Release("https://indexer.example/download/2.torrent"),
            CancellationToken.None);

        download.Content.Should().Equal(bytes);
        download.MagnetUrl.Should().BeNull();
    }

    [Fact]
    public async Task A_download_over_ten_mib_is_refused()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[12 * 1024 * 1024]),
        });
        var client = TorznabTest.TorznabClient(handler);

        var act = () => client.DownloadAsync(
            TorznabFixtures.Indexer(),
            Release("https://indexer.example/download/2.torrent"),
            CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>().WithMessage("*10 MiB*");
    }

    [Fact]
    public async Task A_download_that_streams_more_than_ten_mib_is_refused_too()
    {
        // A lying or missing Content-Length must not let a huge body through.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnsizedContent(12 * 1024 * 1024),
        });
        var client = TorznabTest.TorznabClient(handler);

        var act = () => client.DownloadAsync(
            TorznabFixtures.Indexer(),
            Release("https://indexer.example/download/2.torrent"),
            CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>().WithMessage("*10 MiB*");
    }

    [Fact]
    public async Task The_log_never_contains_the_api_key()
    {
        var failSearch = false;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
                ? TorznabFixtures.Response("caps-prowlarr.xml")
                : failSearch
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : TorznabFixtures.Response("results-torznab.xml"));
        var logger = new CapturingLogger<TorznabIndexerClient>();
        var client = TorznabTest.TorznabClient(handler, logger: logger);

        var indexer = TorznabFixtures.Indexer();
        await client.SearchAsync(indexer, Discovery, CancellationToken.None);

        failSearch = true;
        var failed = () => client.SearchAsync(indexer, Discovery, CancellationToken.None);
        await failed.Should().ThrowAsync<IndexerException>().WithMessage("*401*");

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().OnlyContain(line => !line.Contains(TorznabFixtures.ApiKey, StringComparison.Ordinal));

        // The request URL is logged, but without its query.
        logger.Lines.Should().Contain(line => line.Contains("https://indexer.example/api", StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("apikey", StringComparison.Ordinal));
    }

    [Fact]
    public void The_client_is_chosen_by_the_indexer_type()
    {
        var handler = RecordingHandler.Serving("caps-prowlarr.xml");
        var factory = new IndexerClientFactory(
            TorznabTest.TorznabClient(handler),
            TorznabTest.NewznabClient(handler),
            new PushedReleaseClient(new StaticHttpClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<PushedReleaseClient>.Instance));

        factory.GetClient(TorznabFixtures.Indexer(type: "torznab")).Should().BeOfType<TorznabIndexerClient>();
        factory.GetClient(TorznabFixtures.Indexer(type: "Newznab", protocol: DownloadProtocol.Usenet)).Should().BeOfType<NewznabIndexerClient>();
        factory.GetClient(PushedReleaseClient.Row(null, DownloadProtocol.Torrent)).Should().BeOfType<PushedReleaseClient>();

        var act = () => factory.GetClient(TorznabFixtures.Indexer(type: "gazelle"));
        act.Should().Throw<IndexerException>().WithMessage("*gazelle*");
    }

    /// <summary>A release with only the fields a download needs.</summary>
    private static IndexerRelease Release(string downloadUrl) => new(
        "Daft Punk - Discovery [2001] [FLAC]",
        "example-1",
        downloadUrl,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        [],
        null,
        null,
        DownloadProtocol.Torrent,
        1,
        "Example Indexer",
        null);

    /// <summary>A body of the given size that announces no length, so the read cap is the only guard.</summary>
    private sealed class UnsizedContent(int length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[1024 * 1024];
            var written = 0;

            while (written < length)
            {
                var take = Math.Min(chunk.Length, length - written);
                await stream.WriteAsync(chunk.AsMemory(0, take));
                written += take;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}

/// <summary>The indexer types: their fields, their validation and their connection test.</summary>
public sealed class IndexerTypeTests
{
    private static JsonElement Settings(string url = TorznabFixtures.BaseUrl, string categories = "3000") =>
        JsonSerializer.SerializeToElement(new
        {
            url,
            apiPath = "/api",
            apiKey = TorznabFixtures.ApiKey,
            categories,
        });

    [Fact]
    public void The_torznab_type_serves_torrents_and_has_the_minimum_seeders_field()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsReader(RecordingHandler.Serving("caps-prowlarr.xml")));

        type.Type.Should().Be("torznab");
        type.Protocol.Should().Be(DownloadProtocol.Torrent);
        type.Fields.Select(field => field.Name).Should().Equal("url", "apiPath", "apiKey", "categories", "minimumSeeders");
        type.Fields.Single(field => field.Name == "apiKey").Secret.Should().BeTrue();
        type.Fields.Single(field => field.Name == "apiPath").Advanced.Should().BeTrue();
    }

    [Fact]
    public void The_newznab_type_serves_usenet_and_has_no_minimum_seeders_field()
    {
        var type = new NewznabIndexerType(TorznabTest.CapsReader(RecordingHandler.Serving("caps-prowlarr.xml")));

        type.Type.Should().Be("newznab");
        type.Protocol.Should().Be(DownloadProtocol.Usenet);
        type.Fields.Select(field => field.Name).Should().Equal("url", "apiPath", "apiKey", "categories");
    }

    [Fact]
    public void A_settings_object_without_a_url_is_refused()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsHandler());

        var failures = type.Validate(Settings(url: "indexer.example"));

        failures.Should().Contain("The URL must be an absolute http or https address.");
    }

    [Fact]
    public void Categories_that_are_not_numbers_are_refused()
    {
        var type = new NewznabIndexerType(TorznabTest.CapsHandler());

        var failures = type.Validate(Settings(categories: "3000,music"));

        failures.Should().Contain("The categories must be comma-separated numbers, for example 3000.");
    }

    [Fact]
    public void A_usable_settings_object_has_no_failures()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsHandler());

        type.Validate(Settings()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_test_succeeds_when_caps_parse()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsReader(RecordingHandler.Serving("caps-prowlarr.xml")));

        var result = await type.TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_401_answer_fails_the_test_and_names_the_api_key()
    {
        var type = new NewznabIndexerType(TorznabTest.CapsReader(
            RecordingHandler.Serving("caps-prowlarr.xml", HttpStatusCode.Unauthorized)));

        var result = await type.TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("The indexer answered 401: check the API key");
    }

    [Fact]
    public async Task An_error_answer_fails_the_test_with_its_description()
    {
        var type = new TorznabIndexerType(TorznabTest.CapsReader(RecordingHandler.Serving("error-100.xml")));

        var result = await type.TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Incorrect user credentials");
        result.Error.Should().Contain("API key");
        result.Error.Should().NotContain(TorznabFixtures.ApiKey);
    }

    [Fact]
    public async Task An_answer_without_caps_fails_the_test()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<rss/>", Encoding.UTF8, "application/xml"),
        });
        var type = new NewznabIndexerType(TorznabTest.CapsReader(handler));

        var result = await type.TestAsync(Settings(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("No <caps> in the answer.");
    }
}

/// <summary>What <see cref="ServiceCollectionExtensions.AddWondarrTorznab"/> registers.</summary>
public sealed class TorznabRegistrationTests
{
    [Fact]
    public void AddWondarrTorznab_registers_the_types_the_clients_and_the_named_http_client()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRegistry>(new SecretRegistry());
        services.AddLogging();
        services.AddWondarrTorznab();

        services.AddWondarrTorznab().Should().BeSameAs(services);

        using var provider = services.BuildServiceProvider();

        provider.GetServices<Wondarr.Core.Indexers.IIndexerType>().Select(type => type.Type)
            .Should().Equal("torznab", "newznab");
        provider.GetRequiredService<IIndexerClientFactory>().Should().BeOfType<IndexerClientFactory>();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(IndexerHttp.ClientName);
        client.Timeout.Should().Be(IndexerHttp.RequestTimeout);
        client.DefaultRequestHeaders.UserAgent.Should().Contain(agent => agent.ToString().StartsWith("Wondarr/", StringComparison.Ordinal));
    }
}
