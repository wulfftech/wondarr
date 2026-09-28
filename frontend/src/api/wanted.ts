import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { pagingValues, type Paging } from './paging';
import type { components } from './schema';

/** The Wanted lists and the Activity lists: what is missing, what happened, what is blocked. */

export type SongResource = components['schemas']['SongResource'];
export type SongPage = components['schemas']['PagingResourceOfSongResource'];
export type HistoryResource = components['schemas']['HistoryResource'];
export type HistoryPage = components['schemas']['PagingResourceOfHistoryResource'];
export type BlocklistResource = components['schemas']['BlocklistResource'];
export type BlocklistPage = components['schemas']['PagingResourceOfBlocklistResource'];

/** The query keys the Activity pages invalidate. */
export const WANTED_QUERY_KEY = ['wanted'] as const;
export const HISTORY_QUERY_KEY = ['history'] as const;
export const BLOCKLIST_QUERY_KEY = ['blocklist'] as const;

/** What `GET /api/v1/history` filters on; a value the server cannot read is ignored, not rejected. */
export interface HistoryFilters {
  /** Only this song's events. */
  songId?: number;
  /** Only this event type, spelled as the API serialises it (`grabbed`, `imported`, …). */
  eventType?: string;
}

/**
 * The paging query as something the typed client accepts. The document describes no query for these
 * paths — the controllers read the *arr parameters off the request by hand — so the generated types
 * say `never`. The assertion belongs here rather than at every call site.
 */
function pagingQuery(values: Record<string, string | number>): never {
  return values as never;
}

/** The monitored songs that have no file yet. */
export function useWantedMissing(paging: Paging): UseQueryResult<SongPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...WANTED_QUERY_KEY, 'missing', paging],
    queryFn: async (): Promise<SongPage> => {
      const { data, response } = await client.GET('/api/v1/wanted/missing', {
        params: { query: pagingQuery(pagingValues(paging)) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The wanted list request failed.');
      }

      return data;
    },
  });
}

/** The songs whose file is below their profile's cutoff. */
export function useWantedCutoff(paging: Paging): UseQueryResult<SongPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...WANTED_QUERY_KEY, 'cutoff', paging],
    queryFn: async (): Promise<SongPage> => {
      const { data, response } = await client.GET('/api/v1/wanted/cutoff', {
        params: { query: pagingQuery(pagingValues(paging)) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The wanted list request failed.');
      }

      return data;
    },
  });
}

/** The song lifecycle log, newest first. */
export function useHistory(paging: Paging, filters: HistoryFilters = {}): UseQueryResult<HistoryPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...HISTORY_QUERY_KEY, paging, filters],
    queryFn: async (): Promise<HistoryPage> => {
      const query: Record<string, string | number> = pagingValues(paging);

      if (filters.songId !== undefined) {
        query.songId = filters.songId;
      }

      if (filters.eventType !== undefined && filters.eventType !== '') {
        query.eventType = filters.eventType;
      }

      const { data, response } = await client.GET('/api/v1/history', {
        params: { query: pagingQuery(query) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The history request failed.');
      }

      return data;
    },
  });
}

/** The blocked candidates, newest first. */
export function useBlocklist(paging: Paging): UseQueryResult<BlocklistPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...BLOCKLIST_QUERY_KEY, paging],
    queryFn: async (): Promise<BlocklistPage> => {
      const { data, response } = await client.GET('/api/v1/blocklist', {
        params: { query: pagingQuery(pagingValues(paging)) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The blocklist request failed.');
      }

      return data;
    },
  });
}

/** Removes one entry from the blocklist and refreshes the list. */
export function useDeleteBlocklistItem(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/blocklist/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw new ApiError(response.status, 'The blocklist entry could not be removed.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: BLOCKLIST_QUERY_KEY }),
  });
}
