import {
  useMutation,
  useQuery,
  type QueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';
import { SONGS_QUERY_KEY, useCommand, type CommandResource } from './songs';

/**
 * Compact library (ADR-0007): re-plan every song's album under the library's album policy and move
 * the files that land elsewhere. `GET /api/v1/library/{id}/compact` is the dry run — it changes
 * nothing — and the work itself is the `CompactLibrary` command.
 */

export type CompactPlanResource = components['schemas']['CompactPlanResource'];
export type CompactMoveResource = components['schemas']['CompactMoveResource'];
export type CompactAlbumResource = components['schemas']['CompactAlbumResource'];

/** The dry run's query key, keyed by library so two libraries never share a plan. */
export const COMPACT_PLAN_QUERY_KEY = ['compact-plan'] as const;

/**
 * What Compact library would do to one library. The plan is not cached across opens: the library's
 * albums change as songs are added and imported, so a remembered plan would be shown against a
 * library that has moved on.
 */
export function useCompactPlan(libraryId: number | null, enabled: boolean): UseQueryResult<CompactPlanResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...COMPACT_PLAN_QUERY_KEY, libraryId],
    enabled: enabled && libraryId !== null,
    staleTime: 0,
    gcTime: 0,
    queryFn: async (): Promise<CompactPlanResource> => {
      const { data, response } = await client.GET('/api/v1/library/{id}/compact', {
        params: { path: { id: libraryId ?? 0 } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The compaction plan request failed.');
      }

      return data;
    },
  });
}

/**
 * Queues the compaction of one library. The command endpoint hands the whole body to the handler, so
 * the library id travels beside `name` rather than nested under a `body` member.
 */
export function useRunCompact(): UseMutationResult<CommandResource, Error, number> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (libraryId: number): Promise<CommandResource> => {
      const { data, response } = await client.POST('/api/v1/command', {
        body: { name: 'CompactLibrary', libraryId },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The compaction could not be queued.');
      }

      return data as CommandResource;
    },
  });
}

/** The queued compaction, polled until it stops running. */
export function useCompactCommand(commandId: number | null): UseQueryResult<CommandResource, Error> {
  return useCommand(commandId, { poll: true });
}

/** Everything a finished compaction changes: the library's songs and the plan it was read from. */
export function invalidateCompaction(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
  void queryClient.invalidateQueries({ queryKey: COMPACT_PLAN_QUERY_KEY });
}
