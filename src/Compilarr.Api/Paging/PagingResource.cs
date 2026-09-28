using System.Globalization;
using Compilarr.Core.Paging;
using Microsoft.AspNetCore.Http;

namespace Compilarr.Api.Paging;

/// <summary>
/// The paging envelope every list endpoint returns, with Lidarr's field names
/// (<c>page</c>, <c>pageSize</c>, <c>sortKey</c>, <c>sortDirection</c>, <c>totalRecords</c>,
/// <c>records</c>) so Homepage/Homarr widgets and other *arr clients can read our lists
/// (ARCHITECTURE §5.6).
/// </summary>
/// <typeparam name="T">The resource type on the page.</typeparam>
/// <param name="Page">The 1-based page number that was returned.</param>
/// <param name="PageSize">The rows per page that was used.</param>
/// <param name="SortKey">The sort key the page was ordered by.</param>
/// <param name="SortDirection"><c>ascending</c> or <c>descending</c>.</param>
/// <param name="TotalRecords">How many rows the filter matched in total.</param>
/// <param name="Records">The rows on this page.</param>
public sealed record PagingResource<T>(
    int Page,
    int PageSize,
    string SortKey,
    string SortDirection,
    int TotalRecords,
    IReadOnlyList<T> Records);

/// <summary>Reads the paging query parameters and shape the resulting page for the wire.</summary>
public static class PagingResourceExtensions
{
    /// <summary>The page number used when <c>page</c> is missing or unreadable.</summary>
    public const int DefaultPage = 1;

    /// <summary>The page size used when <c>pageSize</c> is missing or unreadable.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The <c>sortDirection</c> value meaning oldest/smallest first.</summary>
    public const string Ascending = "ascending";

    /// <summary>The <c>sortDirection</c> value meaning newest/largest first.</summary>
    public const string Descending = "descending";

    /// <summary>
    /// Reads <c>page</c>, <c>pageSize</c>, <c>sortKey</c> and <c>sortDirection</c>. The wanted and
    /// history lists are newest-first by default in Lidarr, so an absent or unreadable
    /// <c>sortDirection</c> means <c>descending</c>, and the services fall back to their own default
    /// key when <c>sortKey</c> is missing or unknown.
    /// </summary>
    /// <param name="request">The current request.</param>
    public static PagingSpec ToPagingSpec(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = request.Query;
        var sortKey = query["sortKey"].ToString();

        return new PagingSpec(
            ReadInt(query["page"], DefaultPage),
            ReadInt(query["pageSize"], DefaultPageSize),
            string.IsNullOrWhiteSpace(sortKey) ? null : sortKey,
            !string.Equals(ReadString(query["sortDirection"]), Ascending, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Wraps a core page in the wire envelope, mapping every record with <paramref name="selector"/>.
    /// <paramref name="defaultSortKey"/> is the key the service used when the caller did not name one,
    /// so the response echoes what was actually applied.
    /// </summary>
    /// <typeparam name="T">The row type the service returned.</typeparam>
    /// <typeparam name="TResource">The resource type the endpoint returns.</typeparam>
    /// <param name="page">The page the service returned.</param>
    /// <param name="spec">The request the service was asked with.</param>
    /// <param name="defaultSortKey">The service's default sort key.</param>
    /// <param name="selector">Maps one row to its resource.</param>
    public static PagingResource<TResource> ToPagingResource<T, TResource>(
        this PagedResult<T> page,
        PagingSpec spec,
        string defaultSortKey,
        Func<T, TResource> selector)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(selector);

        return new PagingResource<TResource>(
            spec.Page,
            spec.PageSize,
            spec.SortKey ?? defaultSortKey,
            spec.Descending ? Descending : Ascending,
            page.TotalRecords,
            [.. page.Records.Select(selector)]);
    }

    private static int ReadInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string ReadString(string? text) => string.IsNullOrWhiteSpace(text) ? Descending : text;
}
