import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';
import { invalidateLibrary, problemError, SONGS_QUERY_KEY } from './songs';

/** Music videos (ADR-0014): the settings, a song's one video, finding it, picking one, and removing it. */

export type MusicVideoSettingsResource = components['schemas']['MusicVideoSettingsResource'];
export type MusicVideoSettingsUpdateResource = components['schemas']['MusicVideoSettingsUpdateResource'];
export type MusicVideoResource = components['schemas']['MusicVideoResource'];
export type MusicVideoCandidateResource = components['schemas']['MusicVideoCandidateResource'];
export type MusicVideoSearchResultResource = components['schemas']['MusicVideoSearchResultResource'];
export type MusicVideoRemovedResource = components['schemas']['MusicVideoRemovedResource'];
export type MusicVideoStatus = components['schemas']['MusicVideoStatus'];

export const MUSIC_VIDEO_KEY = ['musicvideo'] as const;
export const MUSIC_VIDEO_SETTINGS_QUERY_KEY = [...MUSIC_VIDEO_KEY, 'settings'] as const;

/** The most songs one bulk search call may name. */
const MAX_BULK_SONGS = 200;

/** How often a video that is queued or downloading is asked for again, in milliseconds. */
export const MUSIC_VIDEO_POLL_INTERVAL_MS = 3000;

/** A present video shorter than this many pixels high is called low quality, and offers a better one. */
export const LOW_QUALITY_HEIGHT = 720;

/** YouTube's thumbnail for a video id. */
export function videoThumbnailUrl(videoId: string): string {
  return `https://i.ytimg.com/vi/${encodeURIComponent(videoId)}/hqdefault.jpg`;
}

/**
 * The toast that says what is on its way: «title» when the video is known by name. "Queued" while the download
 * waits for the shared yt-dlp gate, "Downloading" only once it has started.
 */
export function downloadMessage(title: string | null | undefined, status: MusicVideoStatus | null | undefined): string {
  const verb = status === 'downloading' ? 'Downloading' : 'Queued';

  return title === null || title === undefined || title === '' ? `${verb} music video` : `${verb} «${title}»`;
}

/** The address of a video on YouTube. */
export function youTubeUrl(videoId: string): string {
  return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`;
}

/** The fields the settings page edits, in the camelCase spelling the API and `readOnlyFields` use. */
export type MusicVideoFieldName =
  | 'enabled'
  | 'rootPath'
  | 'layout'
  | 'namingTemplate'
  | 'besideTrackSuffix'
  | 'maxHeight'
  | 'writeNfo'
  | 'writeThumbnail'
  | 'scheduledSearchLimit'
  | 'scheduledSearchEnabled'
  | 'scheduledSearchIntervalHours';

/** The naming template's tokens, as the server accepts them. */
export const NAMING_TOKENS = ['{Artist}', '{AlbumArtist}', '{Title}', '{Album}', '{Year}'] as const;

/** Whether the video is still being worked on, so a view of it should keep asking. */
export function isVideoBusy(status: MusicVideoStatus | undefined): boolean {
  return status === 'queued' || status === 'downloading';
}

/** The music video settings as they are stored. They read fine while the feature is off. */
export function useMusicVideoSettings(): UseQueryResult<MusicVideoSettingsResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: MUSIC_VIDEO_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<MusicVideoSettingsResource> => {
      const { data, response } = await client.GET('/api/v1/musicvideo/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The music video settings request failed.');
      }

      return data;
    },
  });
}

/**
 * Whether music videos are turned on. False while the settings are loading or cannot be read, so
 * nothing about the feature shows until the server has said it is on.
 */
export function useMusicVideosEnabled(): boolean {
  return useMusicVideoSettings().data?.enabled === true;
}

/** Turning the feature on or off changes what every song table and song page shows. */
function settingsWritten(queryClient: ReturnType<typeof useQueryClient>, result: MusicVideoSettingsResource): void {
  queryClient.setQueryData(MUSIC_VIDEO_SETTINGS_QUERY_KEY, result);
  void queryClient.invalidateQueries({ queryKey: MUSIC_VIDEO_KEY });
  invalidateLibrary(queryClient);
}

/** Writes the changed settings. The PUT is partial; `maxHeight: 0` clears the cap. */
export function useUpdateMusicVideoSettings(): UseMutationResult<
  MusicVideoSettingsResource,
  Error,
  MusicVideoSettingsUpdateResource
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: MusicVideoSettingsUpdateResource): Promise<MusicVideoSettingsResource> => {
      const { data, error, response } = await client.PUT('/api/v1/musicvideo/settings', { body: update });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The music video settings could not be saved.');
      }

      return data;
    },
    onSuccess: (result) => settingsWritten(queryClient, result),
    // A refused save can still have stored the folder (it then waits for the confirm), so read it again.
    onError: () => void queryClient.invalidateQueries({ queryKey: MUSIC_VIDEO_SETTINGS_QUERY_KEY }),
  });
}

/** Writes the marker into the saved root: the user has seen it is missing or unmarked and says it is right. */
export function useConfirmMusicVideoRoot(): UseMutationResult<MusicVideoSettingsResource, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<MusicVideoSettingsResource> => {
      const { data, error, response } = await client.POST('/api/v1/musicvideo/settings/root/confirm');

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The video folder could not be confirmed.');
      }

      return data;
    },
    onSuccess: (result) => settingsWritten(queryClient, result),
  });
}

/**
 * A song's video, or `null` when it has none. While it is queued or downloading the query asks again every
 * few seconds, and the song tables are refreshed when it settles.
 */
export function useSongMusicVideo(
  songId: number | null,
  enabled = true,
): UseQueryResult<MusicVideoResource | null, Error> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: [...MUSIC_VIDEO_KEY, 'song', songId],
    enabled: enabled && songId !== null,
    queryFn: async (): Promise<MusicVideoResource | null> => {
      const { data, response } = await client.GET('/api/v1/musicvideo/song/{songId}', {
        params: { path: { songId: songId ?? 0 } },
      });

      if (response.status === 404) {
        return null;
      }

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The music video request failed.');
      }

      return data;
    },
    refetchInterval: (state) =>
      isVideoBusy(state.state.data?.status) || isReplacementPending(songId, state.state.data)
        ? MUSIC_VIDEO_POLL_INTERVAL_MS
        : false,
  });

  const status = query.data?.status;
  const busy = isVideoBusy(status);
  const wasBusy = useRef(false);

  // The song tables carry the same state in their video column: when the video goes from busy to settled, they are
  // read again.
  useEffect(() => {
    if (wasBusy.current && !busy) {
      void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
      invalidateLibrary(queryClient);
    } else if (!busy && (status === 'present' || status === 'failed')) {
      invalidateLibrary(queryClient);
    }

    wasBusy.current = busy;
  }, [busy, status, queryClient]);

  return query;
}

/**
 * Replacements that were queued from this page: the filed video stays `present` while its replacement downloads, so
 * the per-song query would never poll. It does until the row changes (the replacement landed or failed).
 */
const PENDING_REPLACEMENTS = new Map<number, { updatedAt: string; since: number }>();

/** How long after a pick a replacement is still waited for, in milliseconds. */
const REPLACEMENT_PATIENCE_MS = 15 * 60 * 1000;

function isReplacementPending(songId: number | null, video: MusicVideoResource | null | undefined): boolean {
  const pending = songId === null ? undefined : PENDING_REPLACEMENTS.get(songId);

  if (songId === null || pending === undefined) {
    return false;
  }

  if (
    video === null ||
    video === undefined ||
    video.updatedAt !== pending.updatedAt ||
    Date.now() - pending.since > REPLACEMENT_PATIENCE_MS
  ) {
    PENDING_REPLACEMENTS.delete(songId);

    return false;
  }

  return true;
}

/** A search for a song, by its id; or with `autoDownload: false`, which only lists the candidates. */
export type SearchInput = number | { songId: number; autoDownload: boolean };

/**
 * Searches MusicBrainz and YouTube Music for a song's video. A confident match has its download queued
 * (`queued`); otherwise the ranked candidates come back for the person to pick from.
 */
export function useSearchMusicVideo(): UseMutationResult<MusicVideoSearchResultResource, Error, SearchInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: SearchInput): Promise<MusicVideoSearchResultResource> => {
      const { songId, autoDownload } = typeof input === 'number' ? { songId: input, autoDownload: true } : input;
      const { data, error, response } = await client.POST('/api/v1/musicvideo/song/{songId}/search', {
        // The default is to download a confident match, so only a person who wants to pick says no.
        params: { path: { songId }, query: autoDownload ? {} : { autoDownload: false } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The music video search failed.');
      }

      return data;
    },
    onSuccess: (result, input) => {
      if (result.queued) {
        const songId = typeof input === 'number' ? input : input.songId;
        void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
        invalidateLibrary(queryClient);
      }
    },
  });
}

/**
 * Queues the download of the video the person picked. A different video already filed is replaced once it lands, and
 * so is the same one when the filed copy is under 720p. Resolves to where the download stands: queued, or downloading.
 */
export function useGrabMusicVideo(): UseMutationResult<
  MusicVideoStatus,
  Error,
  { songId: number; videoId: string; videoTitle?: string }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ songId, videoId, videoTitle }): Promise<MusicVideoStatus> => {
      const { data, error, response } = await client.POST('/api/v1/musicvideo/song/{songId}/grab', {
        params: { path: { songId } },
        body: { youTubeVideoId: videoId, ...(videoTitle === undefined ? {} : { videoTitle }) },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The download could not be queued.');
      }

      return data?.status ?? 'queued';
    },
    onSuccess: (_result, { songId }) => {
      const held = queryClient.getQueryData<MusicVideoResource | null>([...MUSIC_VIDEO_KEY, 'song', songId]);

      if (held?.status === 'present') {
        PENDING_REPLACEMENTS.set(songId, { updatedAt: held.updatedAt, since: Date.now() });
      }

      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
      invalidateLibrary(queryClient);
    },
  });
}

/** Clears the note that the last replacement of a song's video failed; the video stays as it is. */
export function useDismissReplaceFailure(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (songId: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/musicvideo/song/{songId}/replace-failure', {
        params: { path: { songId } },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The note could not be dismissed.');
      }
    },
    onSuccess: (_result, songId) => {
      PENDING_REPLACEMENTS.delete(songId);
      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
    },
  });
}

/** Removes a song's video, and blocklists the YouTube video for the song unless told not to. */
export function useDeleteMusicVideo(): UseMutationResult<
  MusicVideoRemovedResource,
  Error,
  { songId: number; blocklist: boolean }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ songId, blocklist }): Promise<MusicVideoRemovedResource> => {
      const { data, error, response } = await client.DELETE('/api/v1/musicvideo/song/{songId}', {
        params: { path: { songId }, query: { blocklist } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The music video could not be removed.');
      }

      return data;
    },
    onSuccess: (_result, { songId }) => {
      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
      invalidateLibrary(queryClient);
    },
  });
}

/** Queues a search for each song; only a confident match is downloaded. Resolves to how many were queued. */
export function useBulkSearchMusicVideos(): UseMutationResult<number, Error, number[]> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (songIds: number[]): Promise<number> => {
      let queued = 0;

      // The API takes at most 200 songs a call; a bigger selection goes in turns.
      for (let start = 0; start < songIds.length; start += MAX_BULK_SONGS) {
        const { data, error, response } = await client.POST('/api/v1/musicvideo/search', {
          body: { songIds: songIds.slice(start, start + MAX_BULK_SONGS) },
        });

        if (!response.ok || data === undefined) {
          throw problemError(response.status, error, 'The searches could not be queued.');
        }

        queued += Number(data.queued);
      }

      return queued;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: MUSIC_VIDEO_KEY });
      invalidateLibrary(queryClient);
    },
  });
}
