using System.Net;
using System.Text;
using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Sources;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// What an indexer answers instead of an NZB when its daily download quota is spent: told apart from
/// a real NZB, worded for the user, and kept free of secrets. Plus the status the Newznab client puts
/// on a failed download, which is how the search tells a 429 or a 403 from a dropped connection.
/// </summary>
public sealed class NzbAnswerTests
{
    private const string DogLimit = "You already reached your daily restriction of 10 downloads.";

    private static readonly Func<string, string> NoRedaction = text => text;

    // U+FEFF: the byte order mark some indexers put in front of a document.
    private static readonly string Bom = new((char)0xFEFF, 1);

    public static TheoryData<string> RealNzbs() => new()
    {
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"><file subject=\"x\"/></nzb>",
        Bom + "  \r\n<?xml version=\"1.0\" encoding=\"UTF-8\"?><nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"/>",
        "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"><file subject=\"x\"/></nzb>",
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\">\n<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"/>",
    };

    [Theory]
    [MemberData(nameof(RealNzbs))]
    public void A_real_nzb_is_not_a_refusal(string nzb)
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes(nzb), NoRedaction).Should().BeNull();
    }

    [Fact]
    public void A_plain_sentence_is_the_indexers_words()
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes(DogLimit), NoRedaction).Should().Be(DogLimit);
    }

    [Fact]
    public void A_newznab_error_document_gives_its_description()
    {
        const string body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<error code=\"500\" description=\"Daily download limit reached\"/>";

        NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), NoRedaction).Should().Be("Daily download limit reached");
    }

    [Fact]
    public void An_error_document_without_a_description_gives_its_code_and_without_either_a_general_sentence()
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><error code=\"429\"/>"), NoRedaction)
            .Should().Be("The indexer answered error 429.");
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><error/>"), NoRedaction)
            .Should().Be("The indexer did not answer with an NZB.");
    }

    [Fact]
    public void An_error_document_without_the_xml_declaration_is_read_too()
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes("<error code=\"100\" description=\"Incorrect user credentials\"/>"), NoRedaction)
            .Should().Be("Incorrect user credentials");
    }

    [Fact]
    public void An_error_document_is_found_after_a_bom_and_whitespace()
    {
        var body = Bom + "\r\n  <?xml version=\"1.0\"?><error code=\"100\" description=\"Incorrect user credentials\"/>";

        NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), NoRedaction).Should().Be("Incorrect user credentials");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public void An_empty_answer_has_a_general_sentence(string body)
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), NoRedaction).Should().Be("The indexer did not answer with an NZB.");
    }

    [Fact]
    public void A_document_that_starts_like_xml_but_breaks_before_its_first_element_is_left_to_the_nzb_parser()
    {
        NzbAnswer.Refusal(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><"), NoRedaction).Should().BeNull();
    }

    [Fact]
    public void The_text_is_one_line_and_at_most_200_characters()
    {
        var body = "Line one\r\nline\ttwo   " + new string('x', 400);

        var text = NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), NoRedaction)!;

        text.Should().StartWith("Line one line two xxx");
        text.Length.Should().Be(200);
        text.Should().NotContainAny("\r", "\n", "\t");
    }

    [Fact]
    public void A_registered_secret_is_removed_before_the_text_is_cut()
    {
        const string key = "S3cretIndexerKey";
        var secrets = new SecretRegistry();
        secrets.Register(key);

        // The key runs from character 186 to 201: cut first, half of it would survive redaction.
        var body = new string('a', 185) + " " + key + " is not valid";

        var text = NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), secrets.Redact)!;

        text.Should().NotContain("S3cret").And.NotContain("Key");
        text.Should().Contain("(removed)");
        text.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void An_api_key_in_a_url_the_indexer_quotes_is_removed_even_when_nobody_registered_it()
    {
        var body = "Bad request: https://dog.example/api?t=get&apikey=abcdef0123456789&id=1";

        var text = NzbAnswer.Refusal(Encoding.UTF8.GetBytes(body), CleanseLogMessage.Cleanse)!;

        text.Should().NotContain("abcdef0123456789");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task The_newznab_client_puts_the_status_on_a_failed_download(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status));
        var client = TorznabTest.NewznabClient(handler);

        var act = () => client.DownloadAsync(
            TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet),
            Release("https://indexer.example/getnzb/1.nzb"),
            CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<IndexerException>()).Which;
        exception.StatusCode.Should().Be(status);
        exception.Message.Should().Be($"The indexer answered {(int)status}.");
    }

    [Fact]
    public async Task The_newznab_client_hands_a_plain_text_answer_back_as_it_is_and_leaves_judging_it_to_the_search()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DogLimit) });
        var client = TorznabTest.NewznabClient(handler);

        var download = await client.DownloadAsync(
            TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet),
            Release("https://indexer.example/getnzb/1.nzb"),
            CancellationToken.None);

        Encoding.UTF8.GetString(download.Content!).Should().Be(DogLimit);
    }

    [Fact]
    public async Task A_dropped_connection_carries_no_status()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("boom"));
        var client = TorznabTest.NewznabClient(handler);

        var act = () => client.DownloadAsync(
            TorznabFixtures.Indexer(type: "newznab", protocol: DownloadProtocol.Usenet),
            Release("https://indexer.example/getnzb/1.nzb"),
            CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<IndexerException>()).Which;
        exception.StatusCode.Should().BeNull();
    }

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
        DownloadProtocol.Usenet,
        1,
        "Example Indexer",
        null);
}
