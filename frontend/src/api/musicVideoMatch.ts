import { notifications } from '@mantine/notifications';
import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { MUSIC_VIDEO_KEY } from './musicVideos';
import { pagingValues, type Paging } from './paging';
import type { components } from './schema';
import { invalidateLibrary, problemError } from './songs';

/** The Match page's Music videos tab (ADR-0014, 2026-11-19 amendment): files the folder scan could not settle. */

export type MusicVideoMatchRowResource = components['schemas']['MusicVideoMatchRowResource'];
export type MusicVideoMatchCandidateResource = components['schemas']['MusicVideoMatchCandidateResource'];
export type MusicVideoMatchSongResource = components['schemas']['MusicVideoMatchSongResource'];
export type MusicVideoMatchSummaryResource = components['schemas']['MusicVideoMatchSummaryResource'];
export type MusicVideoMatchBulkResultResource = components['schemas']['MusicVideoMatchBulkResultResource'];
export type MusicVideoMatchResolvedResource = components['schemas']['MusicVideoMatchResolvedResource'];
export type MusicVideoMatchPage = components['schemas']['PagingResourceOfMusicVideoMatchRowResource'];

/** The three lists the tab shows, by the state the API filters on. */
export type MusicVideoMatchState = 'ambiguous' | 'unmatched' | 'ignored';

/** What a bulk call does to each selected file. */
export type MusicVideoBulkAction = 'acceptTop' | 'ignore';

export const MUSIC_VIDEO_MATCH_KEY = [...MUSIC_VIDEO_KEY, 'match'] as const;

/** How often the counts are read again, since a scan adds files with nothing in the page to invalidate on. */
const SUMMARY_POLL_MS = 30_000;

/**
 * The paging values are not part of the generated query type (the controller reads them off the request),
 * so the assertion lives here, once.
 */
function pagedQuery(values: Record<string, string | number>): never {
  return values as never;
}

/** The files in one state, a page at a time. Only asks while the feature is on. */
export function useMusicVideoMatchFiles(
  state: MusicVideoMatchState,
  paging: Paging,
  enabled = true,
): UseQueryResult<MusicVideoMatchPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...MUSIC_VIDEO_MATCH_KEY, 'files', state, paging],
    enabled,
    queryFn: async (): Promise<MusicVideoMatchPage> => {
      const { data, response } = await client.GET('/api/v1/musicvideo/match', {
        params: { query: pagedQuery({ ...pagingValues(paging), state }) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The music video list request failed.');
      }

      return data;
    },
  });
}

/** The counts per state, for the tab's badge and the settings page. Off until the feature is on. */
export function useMusicVideoMatchSummary(enabled: boolean): UseQueryResult<MusicVideoMatchSummaryResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...MUSIC_VIDEO_MATCH_KEY, 'summary'],
    enabled,
    refetchInterval: SUMMARY_POLL_MS,
    queryFn: async (): Promise<MusicVideoMatchSummaryResource> => {
      const { data, response } = await client.GET('/api/v1/musicvideo/match/summary');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The music video counts request failed.');
      }

      return data;
    },
  });
}

/** The library's songs matching the words, for picking one by hand. A term of nothing is not asked. */
export function useMusicVideoSongSearch(term: string): UseQueryResult<MusicVideoMatchSongResource[], Error> {
  const client = useApiClient();
  const trimmed = term.trim();

  return useQuery({
    queryKey: [...MUSIC_VIDEO_MATCH_KEY, 'songs', trimmed],
    enabled: trimmed !== '',
    queryFn: async (): Promise<MusicVideoMatchSongResource[]> => {
      const { data, response } = await client.GET('/api/v1/musicvideo/match/songs', {
        params: { query: { term: trimmed } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The song search failed.');
      }

      return data;
    },
  });
}

/** What the page refreshes once files change state: the lists, the counts, and every song's video column. */
function settled(queryClient: ReturnType<typeof useQueryClient>): void {
  void queryClient.invalidateQueries({ queryKey: MUSIC_VIDEO_KEY });
  invalidateLibrary(queryClient);
}

/**
 * What one file is settled as. `doneMessage` is what the toast says when it worked: the feedback lives in the hook, so it
 * is still shown when the tab re-renders while the request runs.
 */
export type MusicVideoMatchResolveInput = (
  { id: number; songId: number; replace?: boolean } | { id: number; ignore: true } | { id: number; unignore: true }
) & { doneMessage?: string };

const failed = (error: Error): void => {
  notifications.show({ message: error.message, color: 'red' });
};

/** What a bulk call did, in a sentence a person can check against what they selected. */
export function bulkMessage(
  action: MusicVideoBulkAction,
  adopted: number,
  ignored: number,
  skipped: number,
): {
  title: string;
  message: string;
} {
  if (action === 'ignore') {
    return {
      title: `Ignored ${ignored}`,
      message: skipped === 0 ? 'Those files are not offered again.' : `${skipped} could not be ignored.`,
    };
  }

  return {
    title: `Matched ${adopted}, skipped ${skipped}`,
    message:
      skipped === 0
        ? 'Every selected file was linked to its song.'
        : 'A file is only taken when its top match scores 95% or more and leads the next by over 5 points, and its song has no video. The rest still need a pick.',
  };
}

/** Links a file to a song, ignores it, or puts an ignored file back. The file is never moved or renamed. */
export function useResolveMusicVideoMatch(): UseMutationResult<
  MusicVideoMatchResolvedResource,
  Error,
  MusicVideoMatchResolveInput
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: MusicVideoMatchResolveInput): Promise<MusicVideoMatchResolvedResource> => {
      // Exactly one of the three choices; the document marks every member required because they are nullable.
      const body = {
        songId: 'songId' in input ? input.songId : null,
        replace: 'songId' in input && input.replace === true,
        ignore: 'ignore' in input,
        unignore: 'unignore' in input,
      };
      const { data, error, response } = await client.POST('/api/v1/musicvideo/match/{id}/resolve', {
        params: { path: { id: input.id } },
        body,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The file could not be settled.');
      }

      return data;
    },
    onSuccess: (_result, input) => {
      if (input.doneMessage !== undefined) {
        notifications.show({ message: input.doneMessage, color: 'green' });
      }

      settled(queryClient);
    },
    onError: failed,
  });
}

/** Settles many files at once: the top candidate for each, or ignoring each. */
export function useBulkMusicVideoMatch(): UseMutationResult<
  MusicVideoMatchBulkResultResource,
  Error,
  { ids: number[]; action: MusicVideoBulkAction }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ ids, action }): Promise<MusicVideoMatchBulkResultResource> => {
      const { data, error, response } = await client.POST('/api/v1/musicvideo/match/bulk', {
        body: { ids, action },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The files could not be settled.');
      }

      return data;
    },
    onSuccess: (result, { action }) => {
      const shown = bulkMessage(action, Number(result.adopted), Number(result.ignored), Number(result.skipped));

      notifications.show({ ...shown, color: Number(result.skipped) === 0 ? 'green' : 'orange' });
      settled(queryClient);
    },
    onError: failed,
  });
}

/** Starts a scan of the video folder. It runs in the background; the counts catch up as it works. */
export function useScanMusicVideos(): UseMutationResult<void, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<void> => {
      const { error, response } = await client.POST('/api/v1/musicvideo/scan');

      if (!response.ok) {
        throw problemError(response.status, error, 'The scan could not be started.');
      }
    },
    onSuccess: () => {
      notifications.show({ message: 'Scanning the video folder', color: 'blue' });
      settled(queryClient);
    },
    onError: failed,
  });
}
