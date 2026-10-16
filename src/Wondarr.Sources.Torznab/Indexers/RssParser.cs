// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/RssParser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: it produces IndexerRelease rather than ReleaseInfo/TorrentInfo (languages,
// comment URLs, the size-in-description path and the enclosure mime-type rules are dropped), a
// missing pubDate is a null date rather than a fatal feed error, and the <error> check of Lidarr's
// NewznabRssParser lives here so both dialects share it.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Domain;
using Wondarr.Core.Logging;
using Wondarr.Core.Searching;
using Wondarr.Core.Sources;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// The shared half of the Torznab and Newznab RSS readers: it loads the feed's XML, turns an
/// <c>&lt;error&gt;</c> answer into an <see cref="IndexerException"/>, walks the channel's items and
/// reads the fields every RSS dialect has. The dialect subclasses read the namespaced
/// <c>torznab:attr</c> and <c>newznab:attr</c> elements.
/// </summary>
public abstract partial class RssParser
{
    private readonly ISecretRegistry _secrets;
    private protected readonly ILogger Logger;

    /// <summary>Initialises a new instance of the <see cref="RssParser"/> class.</summary>
    /// <param name="secrets">Where the API key is registered so an error answer that echoes it is scrubbed.</param>
    /// <param name="logger">The log sink; an item that cannot be parsed is a warning, not a failure.</param>
    protected RssParser(ISecretRegistry secrets, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _secrets = secrets;
        Logger = logger;
    }

    /// <summary>The most bytes of a non-success answer's body that are read looking for an <c>&lt;error/&gt;</c>.</summary>
    private const long MaxErrorBodyBytes = 64 * 1024;

    /// <summary>
    /// Turns an <c>&lt;error code="…" description="…"/&gt;</c> answer into an
    /// <see cref="IndexerException"/> that carries the indexer's own code and description. A code from
    /// 100 to 199 is an API key problem, so the message says so.
    /// </summary>
    /// <param name="document">The answer's XML.</param>
    /// <param name="redact">Optional scrubber applied to the message, so a server that echoes the API key cannot leak it.</param>
    public static void CheckError(XDocument document, Func<string, string>? redact = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var error = document.Descendants("error").FirstOrDefault();
        if (error is null)
        {
            return;
        }

        var description = error.Attribute("description")?.Value;
        if (string.IsNullOrEmpty(description))
        {
            description = "the indexer reported an error";
        }

        var hasCode = int.TryParse(error.Attribute("code")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
        var detail = hasCode ? $"{code.ToString(CultureInfo.InvariantCulture)}: {description}" : description;

        if (redact is not null)
        {
            detail = redact(detail);
        }

        throw new IndexerException(hasCode && code is >= 100 and <= 199
            ? $"The indexer rejected the API key ({detail})."
            : $"The indexer answered an error ({detail}).");
    }

    /// <summary>
    /// Throws the indexer's own error when a non-success answer carries one. An indexer says why it
    /// refused a request in the body (NZBFinder answers a free account's search with HTTP 401 and
    /// <c>&lt;error code="102" description="…"/&gt;</c>), and the status alone hides that. A body that is
    /// not such an error (empty, an HTML page, other XML) is left alone and the method returns, so the
    /// caller throws its own status message.
    /// </summary>
    /// <param name="response">The non-success answer.</param>
    /// <param name="redact">Optional scrubber applied to the message, so a server that echoes the API key cannot leak it.</param>
    /// <param name="cancellationToken">Cancels the read of the body.</param>
    public static async Task ThrowIfErrorBodyAsync(HttpResponseMessage response, Func<string, string>? redact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        string content;

        try
        {
            // The status is already known to be bad, so a body that is too big or never arrives only
            // means the indexer's reason stays unknown.
            await response.Content.LoadIntoBufferAsync(MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
            content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!CancellationRules.IsCallerCancellation(exception, cancellationToken))
        {
            return;
        }

        XDocument document;

        try
        {
            document = LoadXmlDocument(content);
        }
        catch (XmlException)
        {
            return;
        }

        if (document.Root is { Name.LocalName: "error" } error &&
            (error.Attribute("description") is not null || error.Attribute("code") is not null))
        {
            CheckError(document, redact);
        }
    }

    /// <summary>Parses a feed's answer into the releases it lists.</summary>
    /// <param name="content">The answer's body.</param>
    /// <param name="indexer">The indexer row the answer came from.</param>
    /// <param name="protocol">How the indexer's releases are downloaded.</param>
    /// <returns>Every item that could be parsed; an unusable item is skipped with a warning.</returns>
    public IReadOnlyList<IndexerRelease> Parse(string content, Indexer indexer, DownloadProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(indexer);

        var document = LoadAnswer(content);

        var releases = new List<IndexerRelease>();

        foreach (var item in GetItems(document))
        {
            try
            {
                releases.Add(ParseItem(item, indexer, protocol));
            }
            catch (Exception exception) when (exception is not IndexerException)
            {
                LogItemFailed(Logger, exception, GetTitle(item));
            }
        }

        return releases;
    }

    /// <summary>
    /// Checks that an answer is usable without reading its items: it must be XML and must not be an
    /// <c>&lt;error/&gt;</c>, the two failures <see cref="Parse"/> throws for. A connection test sends
    /// one search and asks no more of the answer than this.
    /// </summary>
    /// <param name="content">The answer's body.</param>
    /// <exception cref="IndexerException">The answer is not XML, or is an error answer.</exception>
    public void Check(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        _ = LoadAnswer(content);
    }

    /// <summary>Loads an answer as XML and turns an <c>&lt;error/&gt;</c> answer into an exception.</summary>
    /// <param name="content">The answer's body.</param>
    private XDocument LoadAnswer(string content)
    {
        XDocument document;
        try
        {
            document = LoadXmlDocument(content);
        }
        catch (XmlException exception)
        {
            throw new IndexerException("The indexer's answer is not valid XML.", exception);
        }

        CheckError(document, _secrets.Redact);

        return document;
    }

    /// <summary>Reads the answer as XML, ignoring any DTD and resolving nothing.</summary>
    /// <param name="content">The answer's body.</param>
    private static XDocument LoadXmlDocument(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
        };

        using var reader = XmlReader.Create(new StringReader(content), settings);

        return XDocument.Load(reader);
    }

    /// <summary>The channel's items; an answer without a channel has none.</summary>
    /// <param name="document">The answer's XML.</param>
    private static IEnumerable<XElement> GetItems(XDocument document)
    {
        var channel = document.Root?.Element("channel");

        return channel is null ? [] : channel.Elements("item");
    }

    /// <summary>Reads one item into a release, then lets the dialect add what it knows.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="indexer">The indexer row the item came from.</param>
    /// <param name="protocol">How the indexer's releases are downloaded.</param>
    private IndexerRelease ParseItem(XElement item, Indexer indexer, DownloadProtocol protocol)
    {
        var release = new IndexerRelease(
            GetTitle(item),
            GetReleaseId(item),
            GetDownloadUrl(item),
            GetMagnetUrl(item),
            GetInfoHash(item)?.ToLowerInvariant(),
            GetSize(item),
            GetPublishDate(item),
            GetSeeders(item),
            GetPeers(item),
            GetGrabs(item),
            GetCategories(item),
            GetDownloadVolumeFactor(item),
            GetInfoUrl(item),
            protocol,
            indexer.Id,
            indexer.Name,
            FileList: null);

        return PostProcessItem(item, release);
    }

    /// <summary>A hook for the dialects, called with the release the base fields produced.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="release">The release read so far.</param>
    /// <returns>The release to keep.</returns>
    protected virtual IndexerRelease PostProcessItem(XElement item, IndexerRelease release) => release;

    /// <summary>The item's title.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string GetTitle(XElement item) => item.Element("title")?.Value ?? "Unknown";

    /// <summary>
    /// The item's guid, or a hash of its download URL, or its title, when it has none. The URL itself is
    /// never the id: it can carry an API key or a passkey, and the id is stored and logged.
    /// </summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string GetReleaseId(XElement item) =>
        item.Element("guid")?.Value
            ?? (GetDownloadUrl(item) is { } url
                ? "url-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16]
                : GetTitle(item));

    /// <summary>The enclosure's URL, or the item's link when there is no enclosure.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string? GetDownloadUrl(XElement item)
    {
        var enclosure = item.Element("enclosure")?.Attribute("url")?.Value;

        return enclosure is { Length: > 0 } ? enclosure : item.Element("link")?.Value;
    }

    /// <summary>The item's magnet URL; the Torznab dialect knows one.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string? GetMagnetUrl(XElement item) => null;

    /// <summary>The torrent's info hash; the Torznab dialect knows one.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string? GetInfoHash(XElement item) => null;

    /// <summary>The release's size: the enclosure's length unless the dialect states one.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual long? GetSize(XElement item) =>
        long.TryParse(item.Element("enclosure")?.Attribute("length")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : null;

    /// <summary>When the release appeared; the Newznab dialect prefers its usenetdate attribute.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual DateTimeOffset? GetPublishDate(XElement item) => ParseDate(item.Element("pubDate")?.Value);

    /// <summary>The swarm's seeders; the Torznab dialect knows them.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual int? GetSeeders(XElement item) => null;

    /// <summary>The swarm's peers; the Torznab dialect knows them.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual int? GetPeers(XElement item) => null;

    /// <summary>How often the release was grabbed; the Newznab dialect knows it.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual int? GetGrabs(XElement item) => null;

    /// <summary>The newznab category ids the release carries; the dialects read them.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual IReadOnlyList<int> GetCategories(XElement item) => [];

    /// <summary>The download volume factor; the Torznab dialect knows it.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual double? GetDownloadVolumeFactor(XElement item) => null;

    /// <summary>The release's details page, the item's comments element.</summary>
    /// <param name="item">The RSS item.</param>
    protected virtual string? GetInfoUrl(XElement item) =>
        item.Element("comments")?.Value is { Length: > 0 } comments ? comments : null;

    /// <summary>Parses an RSS date; an unreadable one is <see langword="null"/>.</summary>
    /// <param name="value">The date's text.</param>
    protected static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrEmpty(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping the indexer item '{Title}'.")]
    private static partial void LogItemFailed(ILogger logger, Exception exception, string title);
}
