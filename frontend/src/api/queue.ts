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
 * The wire spelling of `QueueItemState`. The OpenAPI document types the enum as an integer, but the
 * API serialises enums as camelCase strings (the same shape the other pages read).
 */
export type QueueItemStateName =
  'queued' | 'remotelyQueued' | 'downloading' | 'completed' | 'importing' | 'imported' | 'failed' | 'cancelled';

/** The wire spelling of `SearchOutcome`; see {@link QueueItemStateName}. */
export type SearchOutcomeName =
  'grabbed' | 'noAcceptableCandidate' | 'noResults' | 'sourceUnavailable' | 'failed' | 'cancelled';

/**
 * The queue's query as something the typed client accepts. The document describes only
 * `includeFinished`, and the controller reads the paging values off the request by hand, so the
 * generated query type is narrower than what actually travels. The assertion belongs here rather
 * than at the call site.
 */
function queueQuery(values: Record<string, string | number | boolean>): never {
  return values as never;
}

/** The grabs Wondarr is tracking, newest first. */
export function useQueue(paging: Paging, includeFinished: boolean): UseQueryResult<QueuePage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...QUEUE_QUERY_KEY, 'list', paging, includeFinished],
    queryFn: async (): Promise<QueuePage> => {
      const { data, response } = await client.GET('/api/v1/queue', {
        params: { query: queueQuery({ ...pagingValues(paging), includeFinished }) },
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
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY }),
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
    queryKey: [...QUEUE_QUERY_KEY, 'search', songId],
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
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY }),
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
