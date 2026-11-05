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
import { BLOCKLIST_QUERY_KEY, HISTORY_QUERY_KEY, WANTED_QUERY_KEY } from './wanted';

/**
 * The queue and the interactive search (ARCHITECTURE §5.6): what is downloading, how it is going,
 * and — for one song — every candidate a search saw, with the score and the reasons behind it.
 */

export type QueueResource = components['schemas']['QueueResource'];
export type QueuePage = components['schemas']['PagingResourceOfQueueResource'];
export type QueueStatusResource = components['schemas']['QueueStatusResource'];
export type InteractiveSearchResource = components['schemas']['InteractiveSearchResource'];
export type ReleaseResource = components['schemas']['ReleaseResource'];
export type ScoreBreakdown = components['schemas']['ScoreBreakdown'];
export type ScoreAdjustment = components['schemas']['ScoreAdjustment'];
export type ReleaseRejectionResource = components['schemas']['ReleaseRejectionResource'];

/**
 * The queue's query keys. Both live under `['queue']`, so the `queue` message the API broadcasts with
 * SignalR invalidates the list and the status in one call.
 */
export const QUEUE_QUERY_KEY = ['queue'] as const;
export const QUEUE_STATUS_QUERY_KEY = ['queue', 'status'] as const;

/**
 * The interactive search's root key. It sits outside `['queue']` on purpose: every search submits a
 * fresh Soulseek search against a shared budget, so a queue push or a grab must never refetch it.
 */
export const INTERACTIVE_SEARCH_QUERY_KEY = ['interactive-search'] as const;

/** The states of a queue item, as the document lists them (the API sends the camelCase names). */
export type QueueItemStateName = components['schemas']['QueueItemState'];

/** How a search run ended, as the document lists it. */
export type SearchOutcomeName = components['schemas']['SearchOutcome'];

/**
 * The queue's query as something the typed client accepts. The document describes only
 * `includeFinished`, and the controller reads the paging values off the request by hand, so the
 * generated query type is narrower than what actually travels. The assertion belongs here rather
 * than at the call site.
 */
function queueQuery(values: Record<string, string | number | boolean>): never {
  return values as never;
}

/** The grabs Wondarr is tracking, newest first; `songId` limits them to one song's. */
export function useQueue(paging: Paging, includeFinished: boolean, songId?: number): UseQueryResult<QueuePage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...QUEUE_QUERY_KEY, 'list', paging, includeFinished, songId ?? null],
    queryFn: async (): Promise<QueuePage> => {
      const { data, response } = await client.GET('/api/v1/queue', {
        params: {
          query: queueQuery({
            ...pagingValues(paging),
            includeFinished,
            ...(songId === undefined ? {} : { songId }),
          }),
        },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The queue request failed.');
      }

      return data;
    },
  });
}

/** The queue's summary, for the header's badge. */
export function useQueueStatus(): UseQueryResult<QueueStatusResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: QUEUE_STATUS_QUERY_KEY,
    queryFn: async (): Promise<QueueStatusResource> => {
      const { data, response } = await client.GET('/api/v1/queue/status');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The queue status request failed.');
      }

      return data;
    },
  });
}

/** What one removal asks for: whether to blocklist the candidate, and whether to replace it at once. */
export interface RemoveQueueItemInput {
  /** The queue item to take out. */
  id: number;
  /** Whether the candidate is blocklisted, so the song is not grabbed from it again. */
  blocklist?: boolean;
  /** Whether a blocklisted candidate should not be replaced straight away. */
  skipRedownload?: boolean;
}

/** Takes one grab out of the queue, optionally blocklisting what it was. */
export function useRemoveQueueItem(): UseMutationResult<void, Error, RemoveQueueItemInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: RemoveQueueItemInput): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/queue/{id}', {
        params: {
          path: { id: input.id },
          query: {
            blocklist: input.blocklist ?? false,
            skipRedownload: input.skipRedownload ?? false,
          },
        },
      });

      if (!response.ok) {
        throw new ApiError(
          response.status,
          response.status === 409
            ? 'The download is being imported; try again in a moment.'
            : 'The queue item could not be removed.',
        );
      }
    },
    onSuccess: async (_data, input) => {
      const invalidations = [queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY })];

      // A blocklisted removal adds a blocklist entry and makes the song wanted again.
      if (input.blocklist === true) {
        invalidations.push(
          queryClient.invalidateQueries({ queryKey: BLOCKLIST_QUERY_KEY }),
          queryClient.invalidateQueries({ queryKey: HISTORY_QUERY_KEY }),
          queryClient.invalidateQueries({ queryKey: WANTED_QUERY_KEY }),
        );
      }

      await Promise.all(invalidations);
    },
  });
}

/**
 * Every candidate a search for one song saw, with its score and the rules it failed. Each open runs
 * a fresh search on the server — a candidate's availability changes minute to minute, so nothing is
 * cached (`MATCHING_ENGINE.md` §6.4) — and a failure is never retried: the search budget is shared.
 */
export function useInteractiveSearch(
  songId: number | null,
  enabled: boolean,
): UseQueryResult<InteractiveSearchResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...INTERACTIVE_SEARCH_QUERY_KEY, songId],
    enabled: enabled && songId !== null,
    staleTime: 0,
    gcTime: 0,
    retry: false,
    queryFn: async (): Promise<InteractiveSearchResource> => {
      const { data, response } = await client.GET('/api/v1/release', {
        params: { query: { songId: songId ?? 0 } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The interactive search failed.');
      }

      return data;
    },
  });
}

/** Grabs one candidate the interactive search returned. */
export function useGrabRelease(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (candidateId: number): Promise<void> => {
      const { response } = await client.POST('/api/v1/release', {
        body: { candidateId },
      });

      if (!response.ok) {
        throw new ApiError(
          response.status,
          response.status === 409 ? 'Already downloading' : 'The candidate could not be grabbed.',
        );
      }
    },
    // A grab writes a `grabbed` history event. The interactive search is not touched.
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY }),
        queryClient.invalidateQueries({ queryKey: HISTORY_QUERY_KEY }),
      ]);
    },
  });
}

/**
 * Starts the automatic search for one song: the same command the scheduler runs, posted with the
 * song it is for. The command's progress shows up in System → Tasks.
 */
export function useSongSearchCommand(): UseMutationResult<void, Error, number> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (songId: number): Promise<void> => {
      const { response } = await client.POST('/api/v1/command', {
        // The endpoint hands the whole body to the handler, so the song id sits beside the name.
        body: { name: 'SongSearch', songId },
      });

      if (!response.ok) {
        throw new ApiError(response.status, 'The search could not be started.');
      }
    },
  });
}
