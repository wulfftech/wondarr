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

/** The quality ladder, the profiles built from it and the libraries songs are filed in. */

export type QualityDefinitionResource = components['schemas']['QualityDefinitionResource'];
export type QualityProfileResource = components['schemas']['QualityProfileResource'];
export type QualityProfileItemResource = components['schemas']['QualityProfileItemResource'];
export type LibraryResource = components['schemas']['LibraryResource'];
export type LibraryPreviewResource = components['schemas']['LibraryPreviewResource'];

/** The query keys the profile and library pages invalidate. */
export const QUALITY_DEFINITIONS_QUERY_KEY = ['quality-definitions'] as const;
export const QUALITY_PROFILES_QUERY_KEY = ['quality-profiles'] as const;
export const LIBRARIES_QUERY_KEY = ['libraries'] as const;

/**
 * The wire spelling of `LibraryLayout`. The OpenAPI document types the enum as an integer, but the
 * API serialises enums as camelCase strings (`LibraryResource` says so in as many words), so these
 * are the values that actually travel.
 */
export type LibraryLayoutName = 'flat' | 'artist' | 'artistAlbum' | 'plexamp';

/** The wire spelling of `AlbumPolicy`; see {@link LibraryLayoutName}. */
export type AlbumPolicyName = 'fewestAlbums' | 'singlesOnly' | 'originalAlbum' | 'singleRelease' | 'compilation';

/** The layout presets, in the order the Settings form offers them. */
export const LIBRARY_LAYOUTS: { value: LibraryLayoutName; label: string }[] = [
  { value: 'flat', label: 'Flat' },
  { value: 'artist', label: 'Artist' },
  { value: 'artistAlbum', label: 'Artist → Album' },
  { value: 'plexamp', label: 'Plexamp' },
];

/** The album policies with the one-line descriptions from LIBRARY_OUTPUT §7.3. */
export const ALBUM_POLICIES: { value: AlbumPolicyName; label: string; description: string }[] = [
  {
    value: 'fewestAlbums',
    label: 'Fewest albums',
    description: 'the fewest real albums per artist, the rest in a Singles album',
  },
  {
    value: 'singlesOnly',
    label: 'Singles only',
    description: 'one Singles album per artist — the fewest folders, no real album identity',
  },
  {
    value: 'originalAlbum',
    label: 'Original album',
    description: 'the earliest official album or EP each song appears on',
  },
  {
    value: 'singleRelease',
    label: 'Single release',
    description: "the song's own MusicBrainz single, one folder per song",
  },
  {
    value: 'compilation',
    label: 'Compilation',
    description: 'everything under one Various Artists compilation',
  },
];

/**
 * Reads an enum the document types as a number but the API sends as a camelCase string. The cast is
 * the one place those two disagree; the value is always one of the names above, because the server
 * serialises the enum with `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.
 */
export function readEnum<TName extends string>(value: unknown): TName {
  return value as TName;
}

/** A failed write whose body carried an RFC 7807 problem, with its per-field messages when it had any. */
export class ValidationError extends ApiError {
  /** The field messages the problem carried, keyed by the resource member the server named. */
  readonly fields: Record<string, string>;

  constructor(status: number, message: string, fields: Record<string, string>) {
    super(status, message);
    this.name = 'ValidationError';
    this.fields = fields;
  }
}

/**
 * Turns an RFC 7807 validation body's `errors` member — `{ field: [messages] }` — into the single
 * message per field a form input takes. A body with no `errors` reads as no field messages.
 */
export function validationFields(body: unknown): Record<string, string> {
  if (typeof body !== 'object' || body === null) {
    return {};
  }

  const errors = (body as Record<string, unknown>).errors;

  if (typeof errors !== 'object' || errors === null) {
    return {};
  }

  const fields: Record<string, string> = {};

  for (const [field, messages] of Object.entries(errors)) {
    const list: unknown[] = Array.isArray(messages) ? messages : [messages];
    const first: unknown = list[0];

    if (typeof first === 'string' && first !== '') {
      fields[field] = first;
    }
  }

  return fields;
}

/** Reads a problem member the API may have left out. */
function problemText(body: unknown, member: 'detail' | 'title'): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const value = (body as Record<string, unknown>)[member];

  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * Turns a failed response into the error a form renders. The body is the one the client has already
 * parsed: the response's own body is consumed by then, so re-reading it would lose the problem.
 */
function readProblem(status: number, body: unknown, fallback: string): ValidationError {
  const message = problemText(body, 'detail') ?? problemText(body, 'title') ?? fallback;

  return new ValidationError(status, message, validationFields(body));
}

/** The quality ladder every profile and the Wanted lists name their qualities from. */
export function useQualityDefinitions(): UseQueryResult<QualityDefinitionResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: QUALITY_DEFINITIONS_QUERY_KEY,
    queryFn: async (): Promise<QualityDefinitionResource[]> => {
      const { data, response } = await client.GET('/api/v1/qualitydefinition');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The quality definitions request failed.');
      }

      return data;
    },
  });
}

/** Every quality profile. */
export function useQualityProfiles(): UseQueryResult<QualityProfileResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: QUALITY_PROFILES_QUERY_KEY,
    queryFn: async (): Promise<QualityProfileResource[]> => {
      const { data, response } = await client.GET('/api/v1/qualityprofile');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The quality profiles request failed.');
      }

      return data;
    },
  });
}

/** Creates a profile when its `id` is 0, replaces it otherwise. */
export function useSaveQualityProfile(): UseMutationResult<QualityProfileResource, Error, QualityProfileResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (profile: QualityProfileResource): Promise<QualityProfileResource> => {
      const result =
        profile.id === 0
          ? await client.POST('/api/v1/qualityprofile', { body: profile })
          : await client.PUT('/api/v1/qualityprofile/{id}', {
              params: { path: { id: Number(profile.id) } },
              body: profile,
            });

      if (!result.response.ok || result.data === undefined) {
        throw readProblem(result.response.status, result.error, 'The quality profile could not be saved.');
      }

      return result.data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUALITY_PROFILES_QUERY_KEY }),
  });
}

/** Deletes a profile; the API answers 409 while a song still uses it. */
export function useDeleteQualityProfile(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/qualityprofile/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw readProblem(response.status, error, 'The quality profile could not be deleted.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUALITY_PROFILES_QUERY_KEY }),
  });
}

/** Every library. Libraries are seeded and edited, never created here. */
export function useLibraries(): UseQueryResult<LibraryResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: LIBRARIES_QUERY_KEY,
    queryFn: async (): Promise<LibraryResource[]> => {
      const { data, response } = await client.GET('/api/v1/library');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The libraries request failed.');
      }

      return data;
    },
  });
}

/** Replaces a library's settings. */
export function useSaveLibrary(): UseMutationResult<LibraryResource, Error, LibraryResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (library: LibraryResource): Promise<LibraryResource> => {
      const { data, error, response } = await client.PUT('/api/v1/library/{id}', {
        params: { path: { id: Number(library.id) } },
        body: library,
      });

      if (!response.ok || data === undefined) {
        throw readProblem(response.status, error, 'The library could not be saved.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: LIBRARIES_QUERY_KEY }),
  });
}

/**
 * Renders a naming template against the library's sample song, for the live preview under the
 * template field. It is a POST because the template being previewed is not the stored one; nothing
 * is written and no query is invalidated.
 *
 * A template the server cannot render comes back 200 with its `errors` filled in and `path` null, so
 * the caller reads those rather than catching: only a transport or template-independent failure throws.
 */
export function useNamingPreview(): UseMutationResult<
  LibraryPreviewResource,
  Error,
  { id: number; template: string }
> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async ({ id, template }: { id: number; template: string }): Promise<LibraryPreviewResource> => {
      const { data, error, response } = await client.POST('/api/v1/library/{id}/preview', {
        params: { path: { id } },
        body: { songId: null, template },
      });

      if (!response.ok || data === undefined) {
        throw readProblem(response.status, error, 'The template could not be previewed.');
      }

      return data;
    },
  });
}
