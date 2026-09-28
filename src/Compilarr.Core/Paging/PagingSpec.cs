namespace Compilarr.Core.Paging;

/// <summary>
/// One page request: which page, how large, sorted by which key and in which direction. The values
/// are clamped on construction, so a hostile query string cannot ask for page <c>-3</c> of a million
/// rows. <c>SortKey</c> is passed through as the caller wrote it — a service decides what it means.
/// </summary>
public sealed record PagingSpec
{
    /// <summary>The largest page size a caller may ask for.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>The smallest page size a caller may ask for.</summary>
    public const int MinPageSize = 1;

    /// <summary>Initialises a new instance of the <see cref="PagingSpec"/> class, clamping the request.</summary>
    /// <param name="page">The 1-based page number; anything below 1 becomes 1.</param>
    /// <param name="pageSize">The rows per page; clamped to <see cref="MinPageSize"/>–<see cref="MaxPageSize"/>.</param>
    /// <param name="sortKey">The requested sort key, or <see langword="null"/> for the caller's default.</param>
    /// <param name="descending">Whether the caller asked for the sort to run backwards.</param>
    public PagingSpec(int page, int pageSize, string? sortKey, bool descending)
    {
        Page = Math.Max(page, 1);
        PageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        SortKey = string.IsNullOrWhiteSpace(sortKey) ? null : sortKey;
        Descending = descending;
    }

    /// <summary>Gets the 1-based page number.</summary>
    public int Page { get; init; }

    /// <summary>Gets the rows per page.</summary>
    public int PageSize { get; init; }

    /// <summary>Gets the requested sort key, or <see langword="null"/> for the service's default.</summary>
    public string? SortKey { get; init; }

    /// <summary>Gets a value indicating whether the sort runs backwards.</summary>
    public bool Descending { get; init; }

    /// <summary>Gets how many rows the page skips.</summary>
    public int Skip => (Page - 1) * PageSize;
}

/// <summary>One page of rows together with the total the filter matched.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Records">The rows on this page, already sorted and paged.</param>
/// <param name="TotalRecords">How many rows the filter matched in total, ignoring paging.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Records, int TotalRecords);
