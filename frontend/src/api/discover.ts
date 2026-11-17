import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';
import { problemError } from './songs';

/** The Discover page: suggestions seeded by one song or by the whole library, and what the user turned down. */

export type DiscoverResource = components['schemas']['DiscoverResource'];
export type DiscoverSuggestion = components['schemas']['DiscoverSuggestionResource'];
export type DiscoverSource = components['schemas']['DiscoverSourceResource'];
export type DiscoverDismissal = components['schemas']['DiscoverDismissalResource'];
export type DiscoverDismissalInput = components['schemas']['DiscoverDismissalInputResource'];

export const DISCOVER_QUERY_KEY = ['discover'] as const;
export const DISCOVER_DISMISSALS_QUERY_KEY = ['discover-dismissals'] as const;

/** How many suggestions one request asks for, and how many more "Show more" adds. */
export const DISCOVER_PAGE_SIZE = 50;
/** The most the API returns in one answer. */
export const DISCOVER_MAX_LIMIT = 200;

/** What picks the list: a seed song for a radio, else a mix seed for the library mix. */
export interface DiscoverRequest {
  /** A radio's seed song; omitted for the mix. */
  songId?: number;
  /** The mix's random seed; omitted for a radio. */
  mixSeed?: number;
  limit: number;
}

/** A radio from one song, or the mix drawn from the whole library. */
export function useDiscover(request: DiscoverRequest | null): UseQueryResult<DiscoverResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...DISCOVER_QUERY_KEY, request],
    enabled: request !== null,
    // The server caches the suggestions per seed; nothing here should reshuffle them on focus.
    staleTime: Infinity,
    queryFn: async (): Promise<DiscoverResource> => {
      const query: { songId?: number; mixSeed?: number; limit?: number } = { limit: request?.limit };

      if (request?.songId !== undefined) {
        query.songId = request.songId;
      } else if (request?.mixSeed !== undefined) {
        query.mixSeed = request.mixSeed;
      }

      const { data, error, response } = await client.GET('/api/v1/discover', { params: { query } });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The suggestions could not be loaded.');
      }

      return data;
    },
  });
}

/** The suggestions the user turned down. */
export function useDiscoverDismissals(): UseQueryResult<DiscoverDismissal[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: DISCOVER_DISMISSALS_QUERY_KEY,
    queryFn: async (): Promise<DiscoverDismissal[]> => {
      const { data, response } = await client.GET('/api/v1/discover/dismissals');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The dismissed list could not be loaded.');
      }

      return data;
    },
  });
}

/** Turns a suggestion down; the answer carries the id the undo needs. */
export function useDismissSuggestion(): UseMutationResult<DiscoverDismissal, Error, DiscoverDismissalInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: DiscoverDismissalInput): Promise<DiscoverDismissal> => {
      const { data, error, response } = await client.POST('/api/v1/discover/dismissals', { body: input });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'That suggestion could not be dismissed.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: DISCOVER_DISMISSALS_QUERY_KEY }),
  });
}

/** Takes a dismissal back, so the suggestion can come up again. */
export function useUndoDismissal(): UseMutationResult<void, Error, number | string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number | string): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/discover/dismissals/{id}', {
        params: { path: { id: Number(id) } },
      });

      if (!response.ok) {
        throw new ApiError(response.status, 'The dismissal could not be undone.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: DISCOVER_DISMISSALS_QUERY_KEY }),
  });
}
