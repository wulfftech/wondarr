/**
 * The *arr paging parameters every list endpoint accepts.
 *
 * The controllers read them straight off the query string (`Request.ToPagingSpec`) rather than
 * binding them to an action parameter, so the committed OpenAPI document describes no query for
 * `/api/v1/wanted/*`, `/history` and `/blocklist` and the generated client types them as taking
 * none. `pagingValues` builds the object anyway; the hooks assert it once at the call site.
 */

/** `ascending` or `descending`, the two values `PagingResource` echoes. */
export type SortDirection = 'ascending' | 'descending';

/** What one list request asks the API for. */
export interface Paging {
  /** The 1-based page number. */
  page: number;
  /** The rows per page. */
  pageSize: number;
  /**
   * The column to order by, in the endpoint's own vocabulary. An empty key asks the server for its
   * default order.
   */
  sortKey: string;
  /** Which way that order runs. */
  sortDirection: SortDirection;
}

/** The page size the Activity and Wanted lists use. */
export const DEFAULT_PAGE_SIZE = 50;

/** The first page of a list, left for the server to order. */
export function firstPage(pageSize: number = DEFAULT_PAGE_SIZE): Paging {
  return { page: 1, pageSize, sortKey: '', sortDirection: 'descending' };
}

/** The query values a paging spec becomes. An empty sort key is left out so the server applies its default. */
export function pagingValues(paging: Paging): Record<string, string | number> {
  const values: Record<string, string | number> = {
    page: paging.page,
    pageSize: paging.pageSize,
    sortDirection: paging.sortDirection,
  };

  if (paging.sortKey !== '') {
    values.sortKey = paging.sortKey;
  }

  return values;
}

/** The direction a sortable column header toggles to. */
export function toggleDirection(direction: SortDirection): SortDirection {
  return direction === 'ascending' ? 'descending' : 'ascending';
}
