import { notifications } from '@mantine/notifications';
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
export type MusicVideoPendingReplacementResource = components['schemas']['MusicVideoPendingReplacementResource'];

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

/** A best copy that YouTube was last asked about this long ago is asked about again: the button comes back. */
export const BEST_AVAILABLE_FRESH_MS = 30 * 24 * 60 * 60 * 1000;

/**
 * Whether the last check found nothing better than the filed copy, and is recent enough to be believed: the video's
 * "Download again" is then pointless and stays hidden.
 */
export function nothingBetterOnYouTube(video: MusicVideoResource, now = Date.now()): boolean {
  const best = video.bestAvailableHeight;
  const height = video.height;
  const checkedAt = video.bestAvailableCheckedAt;

  if (best === null || best === undefined || height === null || height === undefined || !checkedAt) {
    return false;
  }

  const checked = Date.parse(checkedAt);

  return Number(best) <= Number(height) && Number.isFinite(checked) && now - checked < BEST_AVAILABLE_FRESH_MS;
}

/**
 * The toast that says what is on its way: «title» when the video is known by name. With the number of other downloads
 * ahead of it (`aheadInQueue`, from the server), "Downloading" when there are none and "Queued … behind N other
 * download(s)" otherwise. Without it, the status says: "Queued" while it waits for the shared yt-dlp gate. A re-grab of
 * the video the song already holds (`checking`) only looks for a better copy, so it says that and not "Downloading".
 */
export function downloadMessage(
  title: string | null | undefined,
  status: MusicVideoStatus | null | undefined,
  aheadInQueue?: number | string | null,
  checking = false,
): string {
  const named = title === null || title === undefined || title === '' ? 'music video' : `«${title}»`;

  if (checking) {
    const copy = title === null || title === undefined || title === '' ? 'a better copy' : `a better copy of ${named}`;
    const ahead = Number(aheadInQueue ?? 0);

    return Number.isFinite(ahead) && ahead > 0
      ? `Queued the check for ${copy} behind ${String(ahead)} other ${ahead === 1 ? 'download' : 'downloads'}`
      : `Checking YouTube for ${copy}`;
  }

  if (aheadInQueue !== null && aheadInQueue !== undefined && Number.isFinite(Number(aheadInQueue))) {
    const ahead = Number(aheadInQueue);

    return ahead <= 0
      ? `Downloading ${named}`
      : `Queued ${named} behind ${String(ahead)} other ${ahead === 1 ? 'download' : 'downloads'}`;
  }

  return `${status === 'downloading' ? 'Downloading' : 'Queued'} ${named}`;
}

/** Feedback for a mutation lives in the hook, not in the caller: a per-call callback is dropped if the caller re-renders. */
const failed = (error: Error): void => {
  notifications.show({ message: error.message, color: 'red' });
};

/** Options of a hook whose caller shows the error itself, inline, so the hook must not toast it as well. */
export interface FeedbackOptions {
  /** Leave the error to the caller (one display per failure). Default false: the hook toasts it. */
  silent?: boolean;
}

/** The error handler for a hook: the toast, unless the caller shows the error itself. */
const failedUnless = (silent: boolean | undefined) => (silent === true ? undefined : failed);

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
    // A filed video stays `present` while its replacement downloads; the server says so in `pendingReplacement`.
    refetchInterval: (state) =>
      isVideoBusy(state.state.data?.status) || hasPendingReplacement(state.state.data)
        ? MUSIC_VIDEO_POLL_INTERVAL_MS
        : false,
  });

  const status = query.data?.status;
  const busy = isVideoBusy(status) || hasPendingReplacement(query.data);
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

/** Whether a download is on its way to replace the filed video (queued behind another, or running). */
export function hasPendingReplacement(video: MusicVideoResource | null | undefined): boolean {
  return video?.pendingReplacement !== null && video?.pendingReplacement !== undefined;
}

/** A search for a song, by its id; or with `autoDownload: false`, which only lists the candidates. */
export type SearchInput = number | { songId: number; autoDownload: boolean };

/**
 * Searches MusicBrainz and YouTube Music for a song's video. A confident match has its download queued
 * (`queued`); otherwise the ranked candidates come back for the person to pick from.
 */
export function useSearchMusicVideo(
  options: FeedbackOptions = {},
): UseMutationResult<MusicVideoSearchResultResource, Error, SearchInput> {
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
        notifications.show({
          message: downloadMessage(
            result.candidates.find((candidate) => candidate.confident)?.title,
            result.status,
            result.aheadInQueue,
          ),
          color: 'blue',
        });
        void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
        invalidateLibrary(queryClient);
      }
    },
    onError: failedUnless(options.silent),
  });
}

/** Where a queued download stands, and how many others are ahead of it. */
export interface MusicVideoGrabOutcome {
  status: MusicVideoStatus;
  aheadInQueue: number;
}

/**
 * Queues the download of the video the person picked. A different video already filed is replaced once it lands, and
 * so is the same one when the filed copy is under 720p. Resolves to where the download stands: queued, or downloading.
 */
export function useGrabMusicVideo(): UseMutationResult<
  MusicVideoGrabOutcome,
  Error,
  { songId: number; videoId: string; videoTitle?: string; checkingBetterCopy?: boolean }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ songId, videoId, videoTitle }): Promise<MusicVideoGrabOutcome> => {
      const { data, error, response } = await client.POST('/api/v1/musicvideo/song/{songId}/grab', {
        params: { path: { songId } },
        body: { youTubeVideoId: videoId, ...(videoTitle === undefined ? {} : { videoTitle }) },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The download could not be queued.');
      }

      return {
        status: data?.status ?? 'queued',
        ...(data?.aheadInQueue === undefined ? {} : { aheadInQueue: Number(data.aheadInQueue) }),
      } as MusicVideoGrabOutcome;
    },
    onSuccess: (outcome, { songId, videoTitle, checkingBetterCopy }) => {
      notifications.show({
        message: downloadMessage(videoTitle, outcome.status, outcome.aheadInQueue, checkingBetterCopy === true),
        color: 'blue',
      });
      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
      invalidateLibrary(queryClient);
    },
    onError: failed,
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
      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
    },
    onError: failed,
  });
}

/**
 * Removes a song's video, and blocklists the YouTube video for the song unless told not to. `onRemoved` runs from the
 * hook, so it still runs when the view that asked has been remounted meanwhile.
 */
export function useDeleteMusicVideo(
  options: FeedbackOptions & { onRemoved?: () => void } = {},
): UseMutationResult<MusicVideoRemovedResource, Error, { songId: number; blocklist: boolean }> {
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
      notifications.show({ message: 'Removed the music video', color: 'green' });
      void queryClient.invalidateQueries({ queryKey: [...MUSIC_VIDEO_KEY, 'song', songId] });
      invalidateLibrary(queryClient);
      options.onRemoved?.();
    },
    onError: failedUnless(options.silent),
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
    onSuccess: (queued) => {
      notifications.show({
        message: `Queued a music video search for ${String(queued)} ${queued === 1 ? 'song' : 'songs'}`,
        color: 'blue',
      });
      void queryClient.invalidateQueries({ queryKey: MUSIC_VIDEO_KEY });
      invalidateLibrary(queryClient);
    },
    onError: failed,
  });
}
