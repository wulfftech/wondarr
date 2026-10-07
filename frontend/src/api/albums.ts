import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { problemError, invalidateLibrary, SONGS_QUERY_KEY } from './songs';
import type { components } from './schema';

/** The album search, a release group's releases, a tracklist, and adding the selected tracks. */

export type AlbumSearchResultResource = components['schemas']['AlbumSearchResultResource'];
export type AlbumReleaseResource = components['schemas']['AlbumReleaseResource'];
export type AlbumTrackResource = components['schemas']['AlbumTrackResource'];
export type AlbumAddAcceptedResource = components['schemas']['AlbumAddAcceptedResource'];
export type AlbumRefResource = components['schemas']['AlbumRefResource'];

/** The query keys the Add songs page's Album tab reads. */
export const ALBUMS_QUERY_KEY = ['albums'] as const;

/** A search for albums Wondarr could add, without adding them. */
export function useAlbumLookup(): UseMutationResult<AlbumSearchResultResource[], Error, string> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (term: string): Promise<AlbumSearchResultResource[]> => {
      const { data, error, response } = await client.GET('/api/v1/album/lookup', {
        params: { query: { term } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The album search failed.');
      }

      return data;
    },
  });
}

/** The releases one MusicBrainz release group holds, the group's default first. */
export function useReleases(releaseGroupId: string | null): UseQueryResult<AlbumReleaseResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...ALBUMS_QUERY_KEY, 'releases', releaseGroupId],
    enabled: releaseGroupId !== null,
    queryFn: async (): Promise<AlbumReleaseResource[]> => {
      const { data, response } = await client.GET('/api/v1/album/releasegroup/{id}/releases', {
        params: { path: { id: releaseGroupId ?? '' } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The releases request failed.');
      }

      return data;
    },
  });
}

/** The tracklist of one release (or one Deezer album), with what the library already holds. */
export function useTracklist(
  source: string | null,
  id: string | null,
): UseQueryResult<AlbumTrackResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...ALBUMS_QUERY_KEY, 'tracks', source, id],
    enabled: source !== null && id !== null,
    queryFn: async (): Promise<AlbumTrackResource[]> => {
      const { data, response } = await client.GET('/api/v1/album/{source}/{id}/tracks', {
        params: { path: { source: source ?? '', id: id ?? '' } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The tracklist request failed.');
      }

      return data;
    },
  });
}

/** What an album add asks for. `trackKeys` left out means "every track the library does not hold". */
export interface AlbumAddInput {
  /** `musicbrainz` for a release, `deezer` for a Deezer album. */
  source: string;
  /** The release MBID, or the Deezer album id. */
  id: string;
  /** The selected tracks' recording MBIDs, or Deezer track ids for a Deezer album. */
  trackKeys?: string[];
  libraryId?: number | null;
  qualityProfileId?: number | null;
  monitored?: boolean | null;
}

/** Adds the selected tracks of one album as songs pinned to that release, as a command. */
export function useAddAlbum(): UseMutationResult<AlbumAddAcceptedResource, Error, AlbumAddInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: AlbumAddInput): Promise<AlbumAddAcceptedResource> => {
      // Every member is nullable on the wire, so the ones the caller left out travel as null, which
      // the endpoint reads as "the default".
      const body: components['schemas']['AlbumAddResource'] = {
        source: input.source,
        id: input.id,
        trackKeys: input.trackKeys ?? null,
        libraryId: input.libraryId ?? null,
        qualityProfileId: input.qualityProfileId ?? null,
        monitored: input.monitored ?? null,
      };

      const { data, error, response } = await client.POST('/api/v1/album/add', { body });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The album could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/**
 * The release a song is pinned to, or `null` when it is filed under a pseudo-album (the API answers
 * 404 there, which is an answer rather than a failure).
 */
export function useSongAlbum(songId: number | null): UseQueryResult<AlbumRefResource | null, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...SONGS_QUERY_KEY, 'album-ref', songId],
    enabled: songId !== null,
    queryFn: async (): Promise<AlbumRefResource | null> => {
      const { data, response } = await client.GET('/api/v1/song/{id}/album', {
        params: { path: { id: songId ?? 0 } },
      });

      if (response.status === 404) {
        return null;
      }

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The song album request failed.');
      }

      return data;
    },
  });
}
