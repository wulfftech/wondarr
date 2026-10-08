// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Newznab/NewznabRssParser.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: it produces IndexerRelease rather than ReleaseInfo (languages, the artist and
// album attributes and the enclosure mime-type warnings are dropped), and the <error> check lives in
// the shared RssParser base.

using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Wondarr.Core.Logging;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Reads a Newznab feed: the shared RSS fields plus the <c>newznab:attr</c> elements that carry the
/// size, the grab count, the categories and the usenet date.
/// </summary>
public sealed class NewznabRssParser : RssParser
{
    /// <summary>The namespace the <c>newznab:attr</c> elements live in.</summary>
    public const string Namespace = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    /// <summary>Initialises a new instance of the <see cref="NewznabRssParser"/> class.</summary>
    /// <param name="secrets">Where the API key is registered so an error answer that echoes it is scrubbed.</param>
    /// <param name="logger">The log sink.</param>
    public NewznabRssParser(ISecretRegistry secrets, ILogger logger)
        : base(secrets, logger)
    {
    }

    /// <inheritdoc />
    protected override long? GetSize(XElement item) =>
        long.TryParse(Attribute(item, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : base.GetSize(item);

    /// <inheritdoc />
    protected override DateTimeOffset? GetPublishDate(XElement item) =>
        ParseDate(Attribute(item, "usenetdate")) ?? base.GetPublishDate(item);

    /// <inheritdoc />
    protected override int? GetGrabs(XElement item) =>
        int.TryParse(Attribute(item, "grabs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var grabs)
            ? grabs
            : null;

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

    /// <summary>The value of the first <c>newznab:attr</c> element with the given name.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="key">The attribute's name, compared without case.</param>
    private static string? Attribute(XElement item, string key) =>
        item.Elements(XNamespace.Get(Namespace) + "attr")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

    /// <summary>The values of every <c>newznab:attr</c> element with the given name.</summary>
    /// <param name="item">The RSS item.</param>
    /// <param name="key">The attribute's name, compared without case.</param>
    private static IEnumerable<string> Attributes(XElement item, string key) =>
        item.Elements(XNamespace.Get(Namespace) + "attr")
            .Where(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("value")?.Value)
            .OfType<string>();
}
