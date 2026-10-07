import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useCallback } from 'react';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { pagingValues, type Paging } from './paging';
import { readEnum, ValidationError, validationFields } from './profiles';
import type { components } from './schema';
import { WANTED_QUERY_KEY } from './wanted';

/** The library, the add dialog and the pasted-list review screen. */

export type SongResource = components['schemas']['SongResource'];
export type SongPage = components['schemas']['PagingResourceOfSongResource'];
export type SongAddResource = components['schemas']['SongAddResource'];
export type SongUpdateResource = components['schemas']['SongUpdateResource'];
export type SongLookupResource = components['schemas']['SongLookupResource'];
export type AlbumOptionResource = components['schemas']['AlbumOptionResource'];
export type ArtistResource = components['schemas']['ArtistResource'];
export type ImportListResource = components['schemas']['ImportListResource'];
export type ImportListItemResource = components['schemas']['ImportListItemResource'];
export type ImportListCandidate = components['schemas']['ImportListCandidate'];
export type ImportListItemPage = components['schemas']['PagingResourceOfImportListItemResource'];
export type BulkAddAcceptedResource = components['schemas']['BulkAddAcceptedResource'];
export type CommandResource = components['schemas']['CommandResource'];
export type SongMoveAcceptedResource = components['schemas']['SongMoveAcceptedResource'];
export type ConvertPlanResource = components['schemas']['ConvertPlanResource'];
export type ConvertAcceptedResource = components['schemas']['ConvertAcceptedResource'];

/** The query keys the library, add and review screens invalidate. */
export const SONGS_QUERY_KEY = ['songs'] as const;
export const ARTISTS_QUERY_KEY = ['artists'] as const;
export const COMMANDS_QUERY_KEY = ['commands'] as const;
export const IMPORT_LISTS_QUERY_KEY = ['import-lists'] as const;
export const IMPORT_LIST_ITEMS_QUERY_KEY = ['import-list-items'] as const;

/** How often a running command is polled, in milliseconds. */
export const COMMAND_POLL_INTERVAL_MS = 1500;

/**
 * The wire spelling of `CommandStatus`. The OpenAPI document types the enum as an integer, but the
 * API serialises enums as camelCase strings (see `readEnum`).
 */
export type CommandStatusName = 'queued' | 'started' | 'completed' | 'failed' | 'aborted';

/** The wire spelling of `ImportListItemState`; see {@link CommandStatusName}. */
export type ImportListItemStateName = 'pending' | 'added' | 'unresolved' | 'skipped';

/** What `GET /api/v1/song` filters on. */
export interface SongFilters {
  /** Only songs this artist is credited on, in any role. */
  artistId?: number;
  /** Only songs with this monitored flag. */
  monitored?: boolean;
}

/** What `GET /api/v1/importlistitem` filters on. */
export interface ImportListItemFilters {
  /** Only lines of this list; omitted means every list. */
  importListId?: number;
  /** Only lines in this state; omitted means every state. */
  state?: ImportListItemStateName;
}

/**
 * The paging query as something the typed client accepts. The document describes only the filter
 * parameters these *arr list endpoints declare, and the controllers read the paging values off the
 * request by hand, so the generated query type is narrower than what actually travels. The
 * assertion belongs here rather than at every call site.
 */
function listQuery(values: Record<string, string | number>): never {
  return values as never;
}

/**
 * Turns a failed response into the error the pages render, reusing P1-12's problem reader pieces. The
 * body is the one the client has already parsed: the response's own body is consumed by then, so
 * re-reading it would lose the detail the alert quotes.
 */
export function problemError(status: number, body: unknown, fallback: string): ValidationError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const message = typeof detail === 'string' && detail !== '' ? detail : fallback;

  return new ValidationError(status, message, validationFields(body));
}

/** Refreshes everything an add, a delete or a resolve changes. */
export function invalidateLibrary(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
  void queryClient.invalidateQueries({ queryKey: WANTED_QUERY_KEY });
  void queryClient.invalidateQueries({ queryKey: IMPORT_LISTS_QUERY_KEY });
  void queryClient.invalidateQueries({ queryKey: IMPORT_LIST_ITEMS_QUERY_KEY });
}

/** The songs the library holds, filtered and paged. */
export function useSongs(paging: Paging, filters: SongFilters = {}): UseQueryResult<SongPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...SONGS_QUERY_KEY, paging, filters],
    queryFn: async (): Promise<SongPage> => {
      const query: Record<string, string | number> = pagingValues(paging);

      if (filters.artistId !== undefined) {
        query.artistId = filters.artistId;
      }

      if (filters.monitored !== undefined) {
        query.monitored = String(filters.monitored);
      }

      const { data, response } = await client.GET('/api/v1/song', {
        params: { query: listQuery(query) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The library request failed.');
      }

      return data;
    },
  });
}

/** Changes a song's monitored flag, and optionally its quality profile. */
export function useUpdateSong(): UseMutationResult<
  SongResource,
  Error,
  { id: number; monitored?: boolean; qualityProfileId?: number }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: {
      id: number;
      monitored?: boolean;
      qualityProfileId?: number;
    }): Promise<SongResource> => {
      // The document marks both members required even though the API reads each as optional: a null
      // leaves the stored value alone, which is exactly what "only the switch moved" needs.
      const body: SongUpdateResource = {
        monitored: update.monitored ?? null,
        qualityProfileId: update.qualityProfileId ?? null,
      };

      const { data, error, response } = await client.PUT('/api/v1/song/{id}', {
        params: { path: { id: update.id } },
        body,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The song could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** Removes a song from the library. */
export function useDeleteSong(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/song/{id}', { params: { path: { id } } });

      if (!response.ok) {
        throw new ApiError(response.status, 'The song could not be deleted.');
      }
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** The releases one song could be filed under, plus its artist's Singles pseudo-album. */
export function useAlbumOptions(songId: number | null): UseQueryResult<AlbumOptionResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...SONGS_QUERY_KEY, 'album-contexts', songId],
    enabled: songId !== null,
    queryFn: async (): Promise<AlbumOptionResource[]> => {
      const { data, response } = await client.GET('/api/v1/song/{id}/albumcontexts', {
        params: { path: { id: songId ?? 0 } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The album options request failed.');
      }

      return data;
    },
  });
}

/** Moves one song to another album, overriding the album policy. */
export function useSetAlbum(): UseMutationResult<SongResource, Error, { id: number; albumKey: string }> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (move: { id: number; albumKey: string }): Promise<SongResource> => {
      const { data, error, response } = await client.PUT('/api/v1/song/{id}/albumcontext', {
        params: { path: { id: move.id } },
        body: { albumKey: move.albumKey },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The song could not be moved.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** Every artist with a song in the library, for the library's filter. */
export function useArtists(): UseQueryResult<ArtistResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: ARTISTS_QUERY_KEY,
    queryFn: async (): Promise<ArtistResource[]> => {
      const { data, response } = await client.GET('/api/v1/artist');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The artists request failed.');
      }

      return data;
    },
  });
}

/** A search for songs Wondarr could add, without adding them. */
export function useSongLookup(): UseMutationResult<SongLookupResource[], Error, string> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (term: string): Promise<SongLookupResource[]> => {
      const { data, error, response } = await client.POST('/api/v1/song/lookup', {
        body: { term },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The lookup failed.');
      }

      return data;
    },
  });
}

/** What an add answered with: the song, and whether the library already held it. */
export interface AddSongResult {
  /** The song the library holds now. */
  songId: number;
  /** The artist it credits, when the caller named one that the library knows. */
  primaryArtistId: number | null;
  /** True when the answer was 409 rather than a new song. */
  alreadyExisted: boolean;
}

/** Exactly one of the two ids, as `SongAddResource` reads it. */
export interface AddSongInput {
  mbRecordingId?: string | null;
  deezerId?: number | null;
  qualityProfileId?: number | null;
  libraryId?: number | null;
  monitored?: boolean | null;
}

/**
 * The add body. The document marks every member required because the record's parameters are all
 * nullable, but the endpoint takes whichever ones the caller knows, so absent ones are left out and
 * the built object is asserted once here.
 */
function addBody(input: AddSongInput): SongAddResource {
  const body: Record<string, unknown> = {};

  if (input.mbRecordingId != null) {
    body.mbRecordingId = input.mbRecordingId;
  }

  if (input.deezerId != null) {
    body.deezerId = input.deezerId;
  }

  if (input.qualityProfileId != null) {
    body.qualityProfileId = input.qualityProfileId;
  }

  if (input.libraryId != null) {
    body.libraryId = input.libraryId;
  }

  if (input.monitored != null) {
    body.monitored = input.monitored;
  }

  return body as unknown as SongAddResource;
}

/** The two ids a lookup result and a stored candidate both carry. */
export interface LookupIds {
  mbRecordingId: string | null;
  deezerId: number | string | null;
}

/**
 * The id a lookup candidate is added (or resolved) by: its recording MBID, else its Deezer track id.
 * Both endpoints take exactly one of the two, and a MusicBrainz candidate is the richer choice.
 */
export function candidateAddInput(candidate: LookupIds): AddSongInput {
  return candidate.mbRecordingId !== null && candidate.mbRecordingId !== ''
    ? { mbRecordingId: candidate.mbRecordingId }
    : { deezerId: candidate.deezerId === null ? null : Number(candidate.deezerId) };
}

/** The song id a 409 problem names, or `null` when the body did not carry one. */
function conflictSongId(body: unknown): number | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const value = (body as Record<string, unknown>).songId;

  return typeof value === 'number' ? value : null;
}

/**
 * Adds one song. The API answers 409 when the library already holds it; that is not a failure here,
 * so the mutation resolves with the existing song's id rather than throwing.
 */
export function useAddSong(): UseMutationResult<AddSongResult, Error, AddSongInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: AddSongInput): Promise<AddSongResult> => {
      const { data, error, response } = await client.POST('/api/v1/song', { body: addBody(input) });

      if (response.status === 409) {
        // The document declares no 409 for this path, so the problem body is untyped at this point
        // even though the client parses it like any other error answer.
        const existing = conflictSongId(error);

        if (existing === null) {
          throw new ApiError(409, 'The library already holds this song.');
        }

        return { songId: existing, primaryArtistId: null, alreadyExisted: true };
      }

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The song could not be added.');
      }

      return {
        songId: Number(data.id),
        primaryArtistId: data.primaryArtistId === null ? null : Number(data.primaryArtistId),
        alreadyExisted: false,
      };
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** What one preview request names: the Deezer track, its ISRC, or a song already in the library. */
export interface PreviewRequest {
  deezerId?: number;
  isrc?: string;
  songId?: number;
}

/**
 * Asks for a fresh Deezer preview URL. Preview URLs carry a signed token that expires after about
 * half an hour, so this is a plain call with no query cache behind it — every audition fetches a new
 * one (DECISIONS, build session 2 #7).
 */
export function usePreviewUrl(): (request: PreviewRequest) => Promise<string> {
  const client = useApiClient();

  return useCallback(
    async (request: PreviewRequest): Promise<string> => {
      const { data, response } = await client.GET('/api/v1/preview', { params: { query: request } });

      if (!response.ok || data === undefined) {
        throw new ApiError(
          response.status,
          response.status === 404 ? 'This track has no preview.' : 'The preview could not be fetched.',
        );
      }

      return data.url;
    },
    [client],
  );
}

/** The pasted lines and the command processing them. */
export function useImportList(id: number | null): UseQueryResult<ImportListResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...IMPORT_LISTS_QUERY_KEY, id],
    enabled: id !== null,
    queryFn: async (): Promise<ImportListResource> => {
      const { data, response } = await client.GET('/api/v1/importlist/{id}', {
        params: { path: { id: id ?? 0 } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The import list request failed.');
      }

      return data;
    },
  });
}

/** Stores a pasted block of lines and queues the command that resolves it. */
export function useBulkAdd(): UseMutationResult<
  BulkAddAcceptedResource,
  Error,
  { text: string; qualityProfileId?: number | null; libraryId?: number | null }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: {
      text: string;
      qualityProfileId?: number | null;
      libraryId?: number | null;
    }): Promise<BulkAddAcceptedResource> => {
      // Only the members the caller named: the endpoint reads a null as "the default", so leaving
      // them out and sending them as null mean the same thing. Asserted because the document marks
      // all three required.
      const body = {
        text: input.text,
        ...(input.qualityProfileId != null ? { qualityProfileId: input.qualityProfileId } : {}),
        ...(input.libraryId != null ? { libraryId: input.libraryId } : {}),
      } as unknown as components['schemas']['BulkAddResource'];

      const { data, error, response } = await client.POST('/api/v1/song/bulk', { body });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The list could not be added.');
      }

      return data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: IMPORT_LISTS_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: IMPORT_LIST_ITEMS_QUERY_KEY });
    },
  });
}

/** One command, optionally polled until it stops running. */
export function useCommand(
  id: number | null,
  options: { poll?: boolean } = {},
): UseQueryResult<CommandResource, Error> {
  const client = useApiClient();
  const poll = options.poll === true;

  return useQuery({
    queryKey: [...COMMANDS_QUERY_KEY, id],
    enabled: id !== null,
    refetchInterval: (query) => {
      if (!poll) {
        return false;
      }

      const status = readEnum<CommandStatusName>(query.state.data?.status);

      return status === 'completed' || status === 'failed' || status === 'aborted' ? false : COMMAND_POLL_INTERVAL_MS;
    },
    queryFn: async (): Promise<CommandResource> => {
      const { data, response } = await client.GET('/api/v1/command/{id}', {
        params: { path: { id: id ?? 0 } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The command request failed.');
      }

      return data;
    },
  });
}

/** The lines of the import lists, for the review screen. */
export function useImportListItems(
  paging: Paging,
  filters: ImportListItemFilters = {},
): UseQueryResult<ImportListItemPage, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...IMPORT_LIST_ITEMS_QUERY_KEY, paging, filters],
    queryFn: async (): Promise<ImportListItemPage> => {
      const query: Record<string, string | number> = pagingValues(paging);

      if (filters.importListId !== undefined) {
        query.importListId = filters.importListId;
      }

      if (filters.state !== undefined) {
        // The document types the enum as an integer; the API binds the camelCase name from the
        // query string, so the name is what travels.
        query.state = filters.state;
      }

      const { data, response } = await client.GET('/api/v1/importlistitem', {
        params: { query: listQuery(query) },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The import list items request failed.');
      }

      return data;
    },
  });
}

/** Resolves one unresolved line to the candidate the user picked, adding the song. */
export function useResolveItem(): UseMutationResult<
  ImportListItemResource,
  Error,
  { itemId: number; mbRecordingId?: string | null; deezerId?: number | null }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: {
      itemId: number;
      mbRecordingId?: string | null;
      deezerId?: number | null;
    }): Promise<ImportListItemResource> => {
      // Exactly one of the two ids, as the endpoint requires; the document marks both required.
      const body = {
        ...(input.mbRecordingId != null ? { mbRecordingId: input.mbRecordingId } : {}),
        ...(input.deezerId != null ? { deezerId: input.deezerId } : {}),
      } as unknown as components['schemas']['ImportListItemResolveResource'];

      const { data, error, response } = await client.POST('/api/v1/importlistitem/{id}/resolve', {
        params: { path: { id: input.itemId } },
        body,
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The line could not be resolved.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** Skips one line, so nothing more is done with it. */
export function useSkipItem(): UseMutationResult<ImportListItemResource, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (itemId: number): Promise<ImportListItemResource> => {
      const { data, response } = await client.POST('/api/v1/importlistitem/{id}/skip', {
        params: { path: { id: itemId } },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The line could not be skipped.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** Moves songs to another library, as a command the Activity page tracks. */
export function useMoveSongs(): UseMutationResult<
  SongMoveAcceptedResource,
  Error,
  { songIds: number[]; libraryId: number }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (move: { songIds: number[]; libraryId: number }): Promise<SongMoveAcceptedResource> => {
      const { data, error, response } = await client.POST('/api/v1/song/move', {
        body: { songIds: move.songIds, libraryId: move.libraryId },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The songs could not be moved.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}

/** What a conversion asks for: the songs, and the one-off rule (`null` for each library's own). */
export interface ConvertInput {
  /** The songs to convert; the endpoint takes at most a few dozen at a time. */
  songIds: number[];
  /**
   * A one-off output rule — `{ codec: 'mp3', bitrateKbps: 320 }` — or `null` to convert by each
   * library's own policy. The document types it as an unknown JSON node, so it is asserted once here.
   */
  rule?: Record<string, unknown> | null;
}

/** The body both convert endpoints take; the document marks every member required. */
function convertBody(input: ConvertInput): components['schemas']['ConvertRequestResource'] {
  return {
    songIds: input.songIds,
    libraryId: null,
    rule: input.rule ?? null,
  };
}

/** What converting the songs would do, without converting anything. */
export function useConvertPreview(): UseMutationResult<ConvertPlanResource, Error, ConvertInput> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (input: ConvertInput): Promise<ConvertPlanResource> => {
      const { data, error, response } = await client.POST('/api/v1/song/convert/preview', {
        body: convertBody(input),
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The conversion could not be planned.');
      }

      return data;
    },
  });
}

/** Converts the songs' files in place, as a command the Activity page tracks. */
export function useConvert(): UseMutationResult<ConvertAcceptedResource, Error, ConvertInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: ConvertInput): Promise<ConvertAcceptedResource> => {
      const { data, error, response } = await client.POST('/api/v1/song/convert', { body: convertBody(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The conversion could not be started.');
      }

      return data;
    },
    onSuccess: () => invalidateLibrary(queryClient),
  });
}
