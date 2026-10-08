// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Torznab/TorznabRssParser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: it produces IndexerRelease rather than TorrentInfo (languages, indexer flags
// and the enclosure mime-type warnings are dropped), a magnet URL also comes from a magnet: download
// URL, and the files attribute is read for a debug line only — a Torznab feed states a file count but
// no per-file list, so IndexerRelease.FileList stays null until Gazelle fills it (P7-03b).

using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Reads a Torznab feed: the shared RSS fields plus the <c>torznab:attr</c> elements that carry the
/// swarm numbers, the info hash, the magnet URL, the size, the categories and the volume factors.
/// </summary>
public sealed partial class TorznabRssParser : RssParser
{
    /// <summary>The namespace the <c>torznab:attr</c> elements live in.</summary>
    public const string Namespace = "http://torznab.com/schemas/2015/feed";

    /// <summary>Initialises a new instance of the <see cref="TorznabRssParser"/> class.</summary>
    /// <param name="secrets">Where the API key is registered so an error answer that echoes it is scrubbed.</param>
    /// <param name="logger">The log sink.</param>
    public TorznabRssParser(ISecretRegistry secrets, ILogger logger)
        : base(secrets, logger)
    {
    }

    /// <inheritdoc />
    protected override string? GetMagnetUrl(XElement item)
    {
        var magnet = Attribute(item, "magneturl");

        if (magnet is { Length: > 0 })
        {
            return magnet;
        }

        return GetDownloadUrl(item) is { } url && url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    /// <inheritdoc />
    protected override string? GetInfoHash(XElement item) => Attribute(item, "infohash");

    /// <inheritdoc />
    protected override long? GetSize(XElement item) =>
        long.TryParse(Attribute(item, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : base.GetSize(item);

    /// <inheritdoc />
    protected override int? GetSeeders(XElement item) =>
        int.TryParse(Attribute(item, "seeders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders)
            ? seeders
            : null;

    /// <inheritdoc />
    protected override int? GetPeers(XElement item)
    {
        if (int.TryParse(Attribute(item, "peers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var peers))
        {
            return peers;
        }

        // Jackett and several trackers report the halves instead of the total.
        if (int.TryParse(Attribute(item, "seeders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders) &&
            int.TryParse(Attribute(item, "leechers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var leechers))
        {
            return seeders + leechers;
        }

        return null;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<int> GetCategories(XElement item)
    {
        var categories = new List<int>();

        foreach (var value in Attributes(item, "category"))
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var category))
            {
                categories.Add(category);
            }
        }

        return categories;
    }

    /// <inheritdoc />
    protected override double? GetDownloadVolumeFactor(XElement item) =>
        double.TryParse(
            Attribute(item, "downloadvolumefactor"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var factor)
            ? factor
            : null;

    /// <inheritdoc />
    protected override IndexerRelease PostProcessItem(XElement item, IndexerRelease release)
    {
        // A Torznab feed states how many files the torrent holds but not their names or sizes, so the
        // count is logged and FileList stays null; Gazelle fills it in (P7-03b).
        if (int.TryParse(Attribute(item, "files"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var files))
        {
            LogFileCount(Logger, files);
        }

        return release;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The release holds {Files} files")]
    private static partial void LogFileCount(ILogger logger, int files);

    /// <summary>The value of the first <c>torznab:attr</c> element with the given name.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="key">The attribute's name, compared without case.</param>
    private static string? Attribute(XElement item, string key) =>
        item.Elements(XNamespace.Get(Namespace) + "attr")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

    /// <summary>The values of every <c>torznab:attr</c> element with the given name.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="key">The attribute's name, compared without case.</param>
    private static IEnumerable<string> Attributes(XElement item, string key) =>
        item.Elements(XNamespace.Get(Namespace) + "attr")
            .Where(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("value")?.Value)
            .OfType<string>();
}
