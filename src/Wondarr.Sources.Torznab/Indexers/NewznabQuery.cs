// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Core/Indexers/Newznab/NewznabRequestGenerator.cs at da7b4dfb1a9e7e1d6625c2dbc3fff96971ab26bd, GPL-3.0.
// Adapted for Wondarr: only the music search path remains (Wondarr never searches TV), one request
// per search instead of a pageable chain, and the limit is the lesser of 100 and the caps maximum.

using System.Text;

namespace Wondarr.Sources.Torznab.Indexers;

/// <summary>
/// Builds the URL a music search is sent to: <c>t=music</c> with the artist and the album when the
/// indexer's caps say it supports both, otherwise <c>t=search</c> with the artist and the album as
/// one cleaned query, the way Lidarr's request generator does.
/// </summary>
public static class NewznabQuery
{
    /// <summary>The most results one search asks for, whatever the indexer offers.</summary>
    public const int MaxLimit = 100;

    /// <summary>Builds the URL of one search.</summary>
    /// <param name="endpoint">The indexer's settings.</param>
    /// <param name="capabilities">What the indexer's caps said it supports.</param>
    /// <param name="query">What to search for.</param>
    /// <returns>The URL to send the search to.</returns>
    public static Uri Build(IndexerEndpoint endpoint, NewznabCapabilities capabilities, ReleaseQuery query)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(query);

        var url = new StringBuilder(endpoint.BaseUri.AbsoluteUri).Append('?');

        if (capabilities.SupportsMusicSearch)
        {
            url.Append("t=music")
                .Append("&artist=").Append(Newsnabify(query.Artist))
                .Append("&album=").Append(Newsnabify(query.Album));
        }
        else
        {
            url.Append("t=search")
                .Append("&q=").Append(Newsnabify(string.Concat(query.Artist, " ", query.Album)));
        }

        url.Append("&cat=").Append(string.Join(",", endpoint.Categories.Distinct()))
            .Append("&extended=1");

        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            url.Append("&apikey=").Append(Uri.EscapeDataString(endpoint.ApiKey));
        }

        url.Append("&offset=0")
            .Append("&limit=").Append(Math.Clamp(capabilities.MaxPageSize, 1, MaxLimit).ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new Uri(url.ToString(), UriKind.Absolute);
    }

    /// <summary>
    /// Cleans a search term the way Lidarr's <c>NewsnabifyTitle</c> does: a plus becomes a space, and
    /// the rest is URL-escaped.
    /// </summary>
    /// <param name="title">The term to clean.</param>
    public static string Newsnabify(string title) => Uri.EscapeDataString(title.Replace("+", " "));
}
