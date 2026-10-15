using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Wondarr.Sources.Torznab.Parsing;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Searching;

/// <summary>
/// The usenet search and the indexer's daily NZB quota (M-42b): the NZBs a search keeps for its grab,
/// the indexer that says it is out of downloads, the cap per indexer for the whole search, and which
/// posts are read first.
/// </summary>
public sealed partial class IndexerSourceProviderTests
{
    private const string DogLimit = "You already reached your daily restriction of 10 downloads.";

    private const string Flac = "Daft Punk - Discovery (2001) [FLAC]";

    // The search reads the posts of one indexer at most this many at a time.
    private const int InFlightReads = 4;

    [Fact]
    public async Task An_indexer_that_answers_with_its_limit_sentence_is_not_asked_for_another_nzb_and_the_message_says_so()
    {
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => Plain(DogLimit),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().BeInRange(1, InFlightReads, "only the reads already in flight when the first answer came back are spent; 8 were planned for the first album alone");
        result.Queries.Should().HaveCount(3, "the other albums are still searched, just not read");
        result.Candidates.Should().BeEmpty();
        result.Message.Should().Be("Usenet: Indexer 1: " + DogLimit, "the sentence is the indexer's error; it is not counted as an unreadable release");
        cache.Count.Should().Be(0, "a sentence is not an NZB, so nothing is kept for a grab");
    }

    [Fact]
    public async Task Another_indexer_is_still_read_when_one_is_out_of_downloads()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (indexer, _) => indexer.Id == 1 ? Plain(DogLimit) : new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, indexerCount: 2, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().BeInRange(1, InFlightReads);
        client.DownloadsFrom(2).Should().Be(8);
        result.Candidates.Should().NotBeEmpty().And.OnlyContain(candidate => candidate.Release!.IndexerId == 2);
        result.Message.Should().Contain("Indexer 1: " + DogLimit).And.NotContain("Indexer 2");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_429_or_403_to_a_download_cuts_the_indexer_off_the_same_way(HttpStatusCode status)
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => throw new IndexerException($"The indexer answered {(int)status}.", status),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().BeInRange(1, InFlightReads);
        result.Message.Should().Be($"Usenet: Indexer 1: The indexer answered {(int)status}.");
    }

    [Fact]
    public async Task A_newznab_error_document_is_the_limit_too_and_is_never_parsed_as_an_nzb()
    {
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => Plain("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<error code=\"500\" description=\"Daily download limit reached\"/>"),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().BeInRange(1, InFlightReads);
        result.Message.Should().Be("Usenet: Indexer 1: Daily download limit reached", "an unreadable-release count would mean it went to the NZB parser");
        cache.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("dropped")]
    [InlineData("http")]
    [InlineData("server")]
    public async Task A_transient_failure_does_not_cut_an_indexer_off_and_the_next_nzb_is_read(string failure)
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, release) => release.ReleaseId.EndsWith("-0", StringComparison.Ordinal)
                ? throw failure switch
                {
                    "dropped" => (Exception)new IndexerException("The indexer could not be reached.", new HttpRequestException("boom")),
                    "http" => new HttpRequestException("boom"),
                    _ => new IndexerException("The indexer answered 503.", HttpStatusCode.ServiceUnavailable),
                }
                : new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(8, "the failure was one read's; the other seven were still made");
        result.Candidates.Should().HaveCount(7);
        result.Message.Should().Contain("1 release(s) could not be read").And.NotContain("Indexer 1:");
    }

    [Fact]
    public async Task At_most_8_nzbs_are_read_from_an_indexer_across_the_three_album_targets()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 8),
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Queries.Should().HaveCount(3);
        client.DownloadsFrom(1).Should().Be(8, "it was 8 per album, 24 in all, before");
    }

    [Fact]
    public async Task The_cap_is_for_the_whole_search_so_a_later_album_gets_only_what_the_first_left()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 5),
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Count(entry => entry.Guid.Contains("Discovery", StringComparison.Ordinal)).Should().Be(5);
        client.DownloadLog.Count(entry => entry.Guid.Contains("Digital Love", StringComparison.Ordinal)).Should().Be(3, "8 minus the 5 already read");
        client.DownloadLog.Count(entry => entry.Guid.Contains("Musique", StringComparison.Ordinal)).Should().Be(0);
    }

    [Fact]
    public async Task Each_indexer_has_its_own_cap_of_8()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 20),
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, indexerCount: 2, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(8);
        client.DownloadsFrom(2).Should().Be(8);
    }

    [Fact]
    public async Task A_release_listed_for_two_albums_is_read_once()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, _) => [.. Enumerable.Range(0, 4).Select(index => UsenetRelease(Flac, $"i{indexer.Id}-shared-{index}", grabs: 10 - index))],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Should().HaveCount(4);
        client.DownloadLog.Select(entry => entry.Guid).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Torrent_reads_keep_the_per_query_cap_of_8()
    {
        var torrent = AlbumTorrent(Flac);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) =>
            [
                .. Enumerable.Range(0, 10).Select(index => Release(
                    Flac,
                    $"i{indexer.Id}-{query.Album}-{index}",
                    seeders: 50 - index,
                    downloadUrl: $"https://indexer.example/dl/{query.Album}/{index}")),
            ],
            Download = (_, _) => new IndexerDownload(torrent, null),
        };
        var provider = Provider(client, out _);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Queries.Should().HaveCount(3);
        client.DownloadLog.Should().HaveCount(24, "8 for each of the three albums: a .torrent read is not a quota download");
    }

    [Fact]
    public async Task Posts_that_name_a_quality_are_read_before_the_others_even_when_the_others_have_more_grabs()
    {
        ReleaseQualityParser.Parse("Daft Punk - Discovery").Should().Be(1, "the name claims nothing: the Unknown quality");
        ReleaseQualityParser.Parse(Flac).Should().NotBe(1);

        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery"
                ?
                [
                    .. Enumerable.Range(0, 6).Select(index => UsenetRelease("Daft Punk - Discovery", $"i{indexer.Id}-plain-{index}", grabs: 50 + index)),
                    .. Enumerable.Range(0, 6).Select(index => UsenetRelease(Flac, $"i{indexer.Id}-claim-{index}", grabs: 1 + index)),
                ]
                : [],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Select(entry => entry.Guid).Should().BeEquivalentTo(
            [.. Enumerable.Range(0, 6).Select(index => $"i1-claim-{index}"), "i1-plain-5", "i1-plain-4"],
            "the six that name a quality, then the two most grabbed of the rest; none of the others is dropped from the order, the cap just ends it");
    }

    [Fact]
    public async Task Among_posts_that_all_name_a_quality_or_all_do_not_the_most_grabbed_are_read_first()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery"
                ? [.. Enumerable.Range(0, 12).Select(index => UsenetRelease(Flac, $"i{indexer.Id}-post-{index}", grabs: index))]
                : [],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Select(entry => entry.Guid).Should().BeEquivalentTo(Enumerable.Range(4, 8).Select(index => $"i1-post-{index}"));
    }

    [Fact]
    public async Task Every_nzb_a_search_read_is_kept_for_the_grab_under_the_key_the_candidate_gives()
    {
        var nzb = AlbumNzb();
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 2) : [],
            Download = (_, _) => new IndexerDownload(nzb, null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().HaveCount(2);
        cache.Count.Should().Be(2);

        foreach (var candidate in result.Candidates)
        {
            var release = candidate.Release!;

            cache.TryGet(NzbCache.KeyOf(release.IndexerId, release.DownloadUrl, release.ReleaseId), out var kept)
                .Should().BeTrue("the grab looks the NZB up with the fields of the release it carries");
            kept.Should().Equal(nzb);
        }
    }

    [Fact]
    public async Task A_second_search_within_the_lifetime_downloads_nothing_for_the_posts_the_first_one_read()
    {
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 8) : [],
            Download = (_, _) => new IndexerDownload(AlbumNzb(), null),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var first = await provider.SearchAsync(Request(), CancellationToken.None);
        client.DownloadLog.Should().HaveCount(8);

        var second = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadLog.Should().HaveCount(8, "every post the second search listed was in the cache");
        second.Candidates.Select(candidate => candidate.BlocklistKey).Should().BeEquivalentTo(first.Candidates.Select(candidate => candidate.BlocklistKey));
        second.Candidates.Should().HaveCount(8);
    }

    [Fact]
    public async Task A_post_in_the_cache_takes_no_slot_of_the_cap_of_8()
    {
        var nzb = AlbumNzb();
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 12) : [],
            Download = (_, _) => new IndexerDownload(nzb, null),
        };

        // Four of the twelve were read by an earlier search.
        foreach (var cached in client.Answer(new Indexer { Id = 1, Name = "Indexer 1" }, new ReleaseQuery("Daft Punk", "Discovery", 2001)).Take(4))
        {
            cache.Put(NzbCache.KeyOf(1, cached.DownloadUrl, cached.ReleaseId), nzb);
        }

        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        client.DownloadsFrom(1).Should().Be(8, "the cap is 8 downloads; the 4 cached posts were free");
        result.Candidates.Should().HaveCount(12);
    }

    [Fact]
    public async Task A_post_in_the_cache_is_still_read_when_its_indexer_has_refused()
    {
        var nzb = AlbumNzb();
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 6) : [],
            Download = (_, _) => Plain(DogLimit),
        };

        foreach (var cached in client.Answer(new Indexer { Id = 1, Name = "Indexer 1" }, new ReleaseQuery("Daft Punk", "Discovery", 2001)).Take(2))
        {
            cache.Put(NzbCache.KeyOf(1, cached.DownloadUrl, cached.ReleaseId), nzb);
        }

        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Candidates.Should().HaveCount(2, "the two posts held in the cache are listed without asking the indexer");
        result.Message.Should().Be("Usenet: Indexer 1: " + DogLimit);
        client.DownloadsFrom(1).Should().BeInRange(1, InFlightReads);
    }

    [Fact]
    public async Task A_broken_nzb_is_counted_unreadable_and_not_kept()
    {
        var cache = new NzbCache(TimeProvider.System);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => query.Album == "Discovery" ? UsenetReleases(indexer, query, 2) : [],
            Download = (_, _) => Plain("<?xml version=\"1.0\"?><nzb><file subject=\"cut off"),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, nzbCache: cache);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Message.Should().Be("Usenet: 2 release(s) could not be read", "a half document is the release's fault, not the indexer's quota");
        cache.Count.Should().Be(0);
        client.DownloadsFrom(1).Should().Be(2);
    }

    [Fact]
    public async Task The_indexers_words_are_cleaned_of_the_api_key_the_registry_knows()
    {
        const string key = "S3cretIndexerKey";
        var secrets = new SecretRegistry();
        secrets.Register(key);
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 2),
            Download = (_, _) => Plain($"Invalid key {key} for this account"),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet, secrets: secrets);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Message.Should().NotContain(key).And.Contain("Invalid key (removed) for this account");
    }

    [Fact]
    public async Task A_long_answer_is_cut_to_200_characters_in_the_message()
    {
        var client = new QuotaIndexerClient
        {
            Answer = (indexer, query) => UsenetReleases(indexer, query, 2),
            Download = (_, _) => Plain("  " + new string('w', 500)),
        };
        var provider = Provider(client, out _, protocol: DownloadProtocol.Usenet);

        var result = await provider.SearchAsync(Request(), CancellationToken.None);

        result.Message.Should().Be("Usenet: Indexer 1: " + new string('w', 200));
    }

    private static IndexerDownload Plain(string body) => new(Encoding.UTF8.GetBytes(body), null);

    private static IReadOnlyList<IndexerRelease> UsenetReleases(Indexer indexer, ReleaseQuery query, int count) =>
        [.. Enumerable.Range(0, count).Select(index => UsenetRelease(Flac, $"i{indexer.Id}-{query.Album}-{index}", grabs: 100 - index))];

    private static IndexerRelease UsenetRelease(string title, string guid, int grabs) =>
        Release(title, guid, grabs: grabs, downloadUrl: "https://usenet.example/get/" + Uri.EscapeDataString(guid), protocol: DownloadProtocol.Usenet);

    /// <summary>
    /// A client that counts every download, per indexer and release. Each download yields first, so the
    /// parallel reads really overlap the way they do against an indexer over the network.
    /// </summary>
    private sealed class QuotaIndexerClient : IIndexerClient
    {
        public Func<Indexer, ReleaseQuery, IReadOnlyList<IndexerRelease>> Answer { get; init; } = (_, _) => [];

        public Func<Indexer, IndexerRelease, IndexerDownload> Download { get; init; } = (_, _) => throw new IndexerException("no download");

        public ConcurrentQueue<(long IndexerId, string Guid)> DownloadLog { get; } = new();

        public int DownloadsFrom(long indexerId) => DownloadLog.Count(entry => entry.IndexerId == indexerId);

        public Task<IReadOnlyList<IndexerRelease>> SearchAsync(Indexer indexer, ReleaseQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexerRelease>>(
                [.. Answer(indexer, query).Select(release => release with { IndexerId = indexer.Id, IndexerName = indexer.Name })]);

        public async Task<IndexerDownload> DownloadAsync(Indexer indexer, IndexerRelease release, CancellationToken cancellationToken)
        {
            DownloadLog.Enqueue((indexer.Id, release.ReleaseId));
            await Task.Yield();

            return Download(indexer, release);
        }
    }
}
