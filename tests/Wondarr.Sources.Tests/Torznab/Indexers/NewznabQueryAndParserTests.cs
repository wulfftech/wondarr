using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// The URL <see cref="NewznabQuery"/> builds for each of the caps fixtures: the music search when the
/// indexer supports artist and album, the plain q search otherwise, and the limit capped at 100.
/// </summary>
public sealed class NewznabQueryTests
{
    private static readonly IndexerEndpoint Endpoint =
        new(TorznabFixtures.BaseUrl, "/api", TorznabFixtures.ApiKey, [3000], MinimumSeeders: 1);

    private static readonly ReleaseQuery Discovery = new("Daft Punk", "Discovery", 2001);

    [Fact]
    public void The_music_search_url_carries_artist_album_categories_and_the_api_key()
    {
        var capabilities = ReadCaps("caps-prowlarr.xml");
        var url = NewznabQuery.Build(Endpoint, capabilities, Discovery);

        url.GetLeftPart(UriPartial.Path).Should().Be("https://indexer.example/api");

        var query = TorznabTest.Query(url);
        query["t"].Should().Be("music");
        query["artist"].Should().Be("Daft Punk");
        query["album"].Should().Be("Discovery");
        query["cat"].Should().Be("3000");
        query["extended"].Should().Be("1");
        query["apikey"].Should().Be(TorznabFixtures.ApiKey);
        query["offset"].Should().Be("0");
        query["limit"].Should().Be("100");
    }

    [Fact]
    public void An_indexer_without_music_search_is_asked_with_a_plain_q()
    {
        var capabilities = ReadCaps("caps-q-only.xml");
        var url = NewznabQuery.Build(Endpoint, capabilities, Discovery);

        var query = TorznabTest.Query(url);
        query["t"].Should().Be("search");
        query["q"].Should().Be("Daft Punk Discovery");
        query["cat"].Should().Be("3000");
        query["apikey"].Should().Be(TorznabFixtures.ApiKey);
        query["limit"].Should().Be("100");
        query.Should().NotContainKey("artist");
        query.Should().NotContainKey("album");
    }

    [Fact]
    public void An_audio_search_element_counts_as_music_search()
    {
        var capabilities = ReadCaps("caps-audio-search.xml");
        var url = NewznabQuery.Build(Endpoint, capabilities, Discovery);

        TorznabTest.Query(url)["t"].Should().Be("music");
    }

    [Fact]
    public void The_limit_is_the_lesser_of_100_and_the_caps_maximum()
    {
        var capabilities = new NewznabCapabilities { MaxPageSize = 50 };
        var url = NewznabQuery.Build(Endpoint, capabilities, Discovery);

        TorznabTest.Query(url)["limit"].Should().Be("50");
    }

    [Fact]
    public void A_plus_in_a_search_term_becomes_a_space()
    {
        var capabilities = new NewznabCapabilities { MusicSearchParams = ["q", "artist", "album"] };
        var url = NewznabQuery.Build(Endpoint, capabilities, new ReleaseQuery("AC+DC", "Back in Black", null));

        var query = TorznabTest.Query(url);
        query["artist"].Should().Be("AC DC");
        query["album"].Should().Be("Back in Black");
    }

    [Fact]
    public void The_categories_are_joined_with_commas()
    {
        var endpoint = Endpoint with { Categories = [3040, 3000, 3040] };
        var url = NewznabQuery.Build(endpoint, new NewznabCapabilities(), Discovery);

        TorznabTest.Query(url)["cat"].Should().Be("3040,3000");
    }

    [Fact]
    public void A_url_with_a_trailing_slash_and_a_bare_api_path_still_join()
    {
        var endpoint = new IndexerEndpoint("https://indexer.example/", "api", null, [3000], null);
        var url = NewznabQuery.Build(endpoint, new NewznabCapabilities(), Discovery);

        url.GetLeftPart(UriPartial.Path).Should().Be("https://indexer.example/api");
    }

    private static NewznabCapabilities ReadCaps(string fixture)
    {
        var handler = RecordingHandler.Serving(fixture);

        return TorznabTest.CapsReader(handler).ReadAsync(Endpoint, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>
/// The caps reader over the checked-in answers: what it parses, the URL it asks with, the 24-hour
/// cache, and the errors it names.
/// </summary>
public sealed class NewznabCapabilitiesReaderTests
{
    private static readonly IndexerEndpoint Endpoint =
        new(TorznabFixtures.BaseUrl, "/api", TorznabFixtures.ApiKey, [3000], MinimumSeeders: 1);

    [Fact]
    public async Task A_category_whose_id_is_not_a_number_does_not_break_the_caps()
    {
        var capabilities = await ReadAsync("caps-bad-category.xml");

        capabilities.SupportsMusicSearch.Should().BeTrue();
        capabilities.Categories.Should().ContainSingle().Which.Id.Should().Be(0);
    }

    [Fact]
    public async Task Prowlarrs_caps_parse_limits_searches_and_categories()
    {
        var capabilities = await ReadAsync("caps-prowlarr.xml");

        capabilities.DefaultPageSize.Should().Be(100);
        capabilities.MaxPageSize.Should().Be(500);
        capabilities.SearchParams.Should().Equal("q");
        capabilities.MusicSearchParams.Should().Equal("q", "artist", "album");
        capabilities.SupportsMusicSearch.Should().BeTrue();
        capabilities.SupportsSearch.Should().BeTrue();

        var category = capabilities.Categories.Should().ContainSingle().Subject;
        category.Id.Should().Be(3000);
        category.Name.Should().Be("Music");
        category.Subcategories.Should().HaveCount(2);
        category.Subcategories.Should().Contain(sub => sub.Id == 3010 && sub.Name == "MP3");
        category.Subcategories.Should().Contain(sub => sub.Id == 3040 && sub.Name == "Lossless");
    }

    [Fact]
    public async Task Caps_without_music_search_leave_it_unavailable()
    {
        var capabilities = await ReadAsync("caps-q-only.xml");

        capabilities.DefaultPageSize.Should().Be(25);
        capabilities.MaxPageSize.Should().Be(100);
        capabilities.SearchParams.Should().Equal("q");
        capabilities.MusicSearchParams.Should().BeNull();
        capabilities.SupportsMusicSearch.Should().BeFalse();
    }

    [Fact]
    public async Task An_audio_search_element_is_read_as_music_search()
    {
        var capabilities = await ReadAsync("caps-audio-search.xml");

        capabilities.MusicSearchParams.Should().Equal("q", "artist", "album");
    }

    [Fact]
    public async Task The_caps_url_carries_the_api_key()
    {
        var handler = RecordingHandler.Serving("caps-prowlarr.xml");
        await TorznabTest.CapsReader(handler).ReadAsync(Endpoint, CancellationToken.None);

        handler.Requests.Should().ContainSingle()
            .Subject.AbsoluteUri.Should().Be("https://indexer.example/api?t=caps&apikey=" + TorznabFixtures.ApiKey);
    }

    [Fact]
    public async Task A_caps_answer_is_remembered_for_24_hours()
    {
        var handler = RecordingHandler.Serving("caps-prowlarr.xml");
        var reader = TorznabTest.CapsReader(handler);

        NewznabCapabilitiesReader.CacheDuration.Should().Be(TimeSpan.FromHours(24));

        var first = await reader.GetAsync(7, Endpoint, CancellationToken.None);
        var second = await reader.GetAsync(7, Endpoint, CancellationToken.None);

        second.Should().BeSameAs(first);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_caps_read_is_not_cached_and_is_tried_again()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
            ++attempts == 1
                ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                : TorznabFixtures.Response("caps-prowlarr.xml"));
        var reader = TorznabTest.CapsReader(handler);

        var first = await reader.GetAsync(7, Endpoint, CancellationToken.None);
        var second = await reader.GetAsync(7, Endpoint, CancellationToken.None);

        first.Should().BeSameAs(NewznabCapabilities.Defaults);
        first.SupportsMusicSearch.Should().BeFalse();
        second.MaxPageSize.Should().Be(500);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_error_answer_in_the_100_range_names_the_api_key_problem()
    {
        var reader = TorznabTest.CapsReader(RecordingHandler.Serving("error-100.xml"));

        var act = () => reader.ReadAsync(Endpoint, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>())
            .WithMessage("*Incorrect user credentials*")
            .Which.Message.Should().Contain("API key");
    }

    [Fact]
    public async Task A_401_answer_names_the_api_key()
    {
        var handler = RecordingHandler.Serving("caps-prowlarr.xml", System.Net.HttpStatusCode.Unauthorized);
        var reader = TorznabTest.CapsReader(handler);

        var act = () => reader.ReadAsync(Endpoint, CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>()
            .WithMessage("The indexer answered 401: check the API key");
    }

    [Fact]
    public async Task An_answer_without_a_caps_element_is_named()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<rss/>", System.Text.Encoding.UTF8, "application/xml"),
        });
        var reader = TorznabTest.CapsReader(handler);

        var act = () => reader.ReadAsync(Endpoint, CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>()
            .WithMessage("No <caps> in the answer.");
    }

    [Fact]
    public async Task The_api_key_is_registered_with_the_secret_registry_before_the_request()
    {
        var secrets = new SecretRegistry();
        var handler = RecordingHandler.Serving("caps-prowlarr.xml");

        await TorznabTest.CapsReader(handler, secrets: secrets).ReadAsync(Endpoint, CancellationToken.None);

        secrets.Redact("https://indexer.example/api?t=caps&apikey=" + TorznabFixtures.ApiKey)
            .Should().Be("https://indexer.example/api?t=caps&apikey=(removed)");
    }

    [Fact]
    public async Task An_error_description_that_echoes_the_key_is_scrubbed()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""<error code="100" description="Incorrect API key '{{TorznabFixtures.ApiKey}}'/>""",
                System.Text.Encoding.UTF8,
                "application/xml"),
        });
        var reader = TorznabTest.CapsReader(handler, secrets: new SecretRegistry());

        var act = () => reader.ReadAsync(Endpoint, CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>())
            .Which.Message.Should().NotContain(TorznabFixtures.ApiKey);
    }

    private static async Task<NewznabCapabilities> ReadAsync(string fixture)
    {
        var handler = RecordingHandler.Serving(fixture);

        return await TorznabTest.CapsReader(handler).ReadAsync(Endpoint, CancellationToken.None);
    }
}

/// <summary>The RSS parsers over the checked-in feeds: every field of every item.</summary>
public sealed class RssParserTests
{
    private static readonly Indexer Indexer = TorznabFixtures.Indexer(id: 7);

    [Fact]
    public void Torznab_results_parse_every_field_of_every_item()
    {
        var releases = new TorznabRssParser(new SecretRegistry(), NullLogger.Instance)
            .Parse(TorznabFixtures.Read("results-torznab.xml"), Indexer, Wondarr.Core.Sources.DownloadProtocol.Torrent);

        releases.Should().HaveCount(3);

        var magnet = releases[0];
        magnet.Title.Should().Be("Daft Punk - Discovery [2001] [FLAC]");
        magnet.ReleaseId.Should().Be("example-1");
        magnet.DownloadUrl.Should().StartWith("magnet:?xt=urn:btih:0123456789abcdef");
        magnet.MagnetUrl.Should().Be(magnet.DownloadUrl);
        magnet.InfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
        magnet.Size.Should().Be(3412345678L);
        magnet.PublishDate.Should().Be(new DateTimeOffset(2001, 3, 13, 0, 0, 0, TimeSpan.Zero));
        magnet.Seeders.Should().Be(42);
        magnet.Peers.Should().Be(50);
        magnet.Grabs.Should().BeNull();
        magnet.Categories.Should().Equal(3000, 3040);
        magnet.DownloadVolumeFactor.Should().Be(1);
        magnet.InfoUrl.Should().Be("https://indexer.example/details/1#comments");
        magnet.Protocol.Should().Be(Wondarr.Core.Sources.DownloadProtocol.Torrent);
        magnet.IndexerId.Should().Be(7);
        magnet.IndexerName.Should().Be("Example Indexer");
        magnet.FileList.Should().BeNull();

        var torrent = releases[1];
        torrent.ReleaseId.Should().Be("example-2");
        torrent.DownloadUrl.Should().Be("https://indexer.example/download/2.torrent");
        torrent.MagnetUrl.Should().BeNull();
        torrent.InfoHash.Should().Be("fedcba9876543210fedcba9876543210fedcba98");
        torrent.Size.Should().Be(123456789L);
        torrent.PublishDate.Should().Be(new DateTimeOffset(2001, 3, 14, 0, 0, 0, TimeSpan.Zero));
        torrent.Seeders.Should().Be(7);
        torrent.Peers.Should().Be(10);
        torrent.Categories.Should().Equal(3010);
        torrent.DownloadVolumeFactor.Should().Be(1);

        var freeleech = releases[2];
        freeleech.ReleaseId.Should().Be("example-3");
        freeleech.InfoHash.Should().BeNull();
        freeleech.Size.Should().Be(5678901234L);
        freeleech.Seeders.Should().Be(11);
        freeleech.Peers.Should().Be(12);
        freeleech.Categories.Should().Equal(3040);
        freeleech.DownloadVolumeFactor.Should().Be(0);
    }

    [Fact]
    public void Newznab_results_parse_every_field_of_every_item()
    {
        var releases = new NewznabRssParser(new SecretRegistry(), NullLogger.Instance)
            .Parse(TorznabFixtures.Read("results-newznab.xml"), Indexer, Wondarr.Core.Sources.DownloadProtocol.Usenet);

        releases.Should().HaveCount(2);

        var first = releases[0];
        first.Title.Should().Be("Daft Punk - Discovery [2001] [FLAC]");
        first.ReleaseId.Should().Be("example-nzb-1");
        first.DownloadUrl.Should().Be("https://indexer.example/nzb/1.nzb");
        first.MagnetUrl.Should().BeNull();
        first.InfoHash.Should().BeNull();
        first.Size.Should().Be(3412345678L);
        first.PublishDate.Should().Be(new DateTimeOffset(2001, 3, 12, 0, 0, 0, TimeSpan.Zero));
        first.Seeders.Should().BeNull();
        first.Peers.Should().BeNull();
        first.Grabs.Should().Be(133);
        first.Categories.Should().Equal(3040);
        first.DownloadVolumeFactor.Should().BeNull();
        first.Protocol.Should().Be(Wondarr.Core.Sources.DownloadProtocol.Usenet);

        var second = releases[1];
        second.ReleaseId.Should().Be("example-nzb-2");
        second.Size.Should().Be(123456789L);
        second.PublishDate.Should().Be(new DateTimeOffset(2001, 3, 13, 0, 0, 0, TimeSpan.Zero));
        second.Grabs.Should().Be(12);
        second.Categories.Should().Equal(3010);
    }

    [Fact]
    public void An_error_answer_becomes_an_indexer_exception()
    {
        var parser = new TorznabRssParser(new SecretRegistry(), NullLogger.Instance);

        var act = () => parser.Parse(
            TorznabFixtures.Read("error-100.xml"),
            Indexer,
            Wondarr.Core.Sources.DownloadProtocol.Torrent);

        act.Should().Throw<IndexerException>()
            .WithMessage("*Incorrect user credentials*")
            .Which.Message.Should().Contain("API key");
    }
}
