import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { validationFields, ValidationError } from './profiles';
import type { ImportListResource } from './songs';
import type { components } from './schema';

/**
 * The import lists (ARCHITECTURE §5.8): one row per source Wondarr watches — a playlist, a CSV file
 * — with the items it has seen and what became of them. The editor is rendered from
 * `GET /api/v1/importlist/schema`, so a provider added on the server needs no change here.
 */

export type ImportListSchemaResource = components['schemas']['ImportListSchemaResource'];
/** The import lists share the notifications' field shape, so the schema form is the same one. */
export type ImportListFieldResource = components['schemas']['NotificationFieldResource'];
export type CsvPreviewResource = components['schemas']['CsvPreviewResource'];
export type ImportListSyncAcceptedResource = components['schemas']['ImportListSyncAcceptedResource'];

/** The query keys the import list screens read and invalidate. */
export const IMPORT_LISTS_QUERY_KEY = ['import-lists'] as const;
export const IMPORT_LIST_SCHEMA_QUERY_KEY = ['import-lists', 'schema'] as const;

/** What a list's `policy` says happens to a song the source no longer holds. */
export const IMPORT_LIST_POLICIES: readonly { value: string; label: string }[] = [
  { value: 'AddOnly', label: 'Add only' },
  { value: 'AddAndUnmonitor', label: 'Add, and unmonitor songs that leave the list' },
  { value: 'Mirror', label: 'Mirror: also delete songs that leave the list and have no file' },
];

/** The reminder the policy select carries: what a removal never does. */
export const IMPORT_LIST_POLICY_HELP = 'A list never deletes a file';

/**
 * The body of a create or an update. `sourceText` is only read by the CSV provider: a new file's
 * text, or — left out — the text the list already stores.
 */
export interface ImportListInput {
  type: string;
  name: string;
  settings: Record<string, unknown>;
  sourceText?: string;
  policy: string;
  qualityProfileId: number | null;
  libraryId: number | null;
  enabled: boolean;
  /** Hours between scheduled syncs; `null` (an emptied field) is the server's default of 24. */
  syncIntervalHours: number | null;
  plexPlaylist: boolean;
  m3uExport: boolean;
}

/** The body of a CSV preview: the file's text and the column mapping the user has typed so far. */
export interface ImportListPreviewInput {
  sourceText: string;
  settings: Record<string, unknown>;
}

/**
 * Turns a failed response into the error the forms render, reusing the problem reader the other forms
 * share. The body is the one the client has already parsed: the response's own body is consumed by
 * then, so re-reading it would lose the field messages the server sent.
 */
function problemError(status: number, body: unknown, fallback: string): ValidationError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const message = typeof detail === 'string' && detail !== '' ? detail : fallback;

  return new ValidationError(status, message, validationFields(body));
}

/** Everything a create, an update, a delete or a sync changes. */
function invalidateImportLists(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: IMPORT_LISTS_QUERY_KEY });
}

/** Every import list, ordered by id. */
export function useImportLists(): UseQueryResult<ImportListResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: IMPORT_LISTS_QUERY_KEY,
    queryFn: async (): Promise<ImportListResource[]> => {
      const { data, response } = await client.GET('/api/v1/importlist');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The import lists request failed.');
      }

      return data;
    },
  });
}

/**
 * Every provider and the settings form it wants. The schema is fixed for the life of the server, so
 * it is fetched once and never considered stale.
 */
export function useImportListSchema(): UseQueryResult<ImportListSchemaResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: IMPORT_LIST_SCHEMA_QUERY_KEY,
    staleTime: Infinity,
    queryFn: async (): Promise<ImportListSchemaResource[]> => {
      const { data, response } = await client.GET('/api/v1/importlist/schema');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The import list schema request failed.');
      }

      return data;
    },
  });
}

/** Adds an import list. */
export function useCreateImportList(): UseMutationResult<ImportListResource, Error, ImportListInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: ImportListInput): Promise<ImportListResource> => {
      const { data, error, response } = await client.POST('/api/v1/importlist', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The import list could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateImportLists(queryClient),
  });
}

/** Replaces an import list's settings. */
export function useUpdateImportList(): UseMutationResult<ImportListResource, Error, ImportListInput & { id: number }> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, ...input }: ImportListInput & { id: number }): Promise<ImportListResource> => {
      const { data, error, response } = await client.PUT('/api/v1/importlist/{id}', {
        params: { path: { id } },
        body: body(input),
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The import list could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateImportLists(queryClient),
  });
}

/** Deletes an import list and its items. */
export function useDeleteImportList(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/importlist/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The import list could not be deleted.');
      }
    },
    onSuccess: () => invalidateImportLists(queryClient),
  });
}

/**
 * Asks the server to fetch a list now, outside its interval. The work runs in a command, so the
 * caller polls it (`useCommand`) and refreshes the list when the command finishes.
 */
export function useSyncImportList(): UseMutationResult<ImportListSyncAcceptedResource, Error, number> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (id: number): Promise<ImportListSyncAcceptedResource> => {
      const { data, error, response } = await client.POST('/api/v1/importlist/{id}/sync', {
        params: { path: { id } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The sync could not be started.');
      }

      return data;
    },
  });
}

/**
 * Reads a CSV the way the provider will: which columns it found, the first rows and the problems
 * with the mapping. The editor shows the result before the list is saved.
 */
export function useCsvPreview(): UseMutationResult<CsvPreviewResource, Error, ImportListPreviewInput> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (input: ImportListPreviewInput): Promise<CsvPreviewResource> => {
      const { data, error, response } = await client.POST('/api/v1/importlist/csv/preview', {
        body: { sourceText: input.sourceText, settings: input.settings },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The CSV could not be read.');
      }

      return data;
    },
  });
}

/**
 * The request body. The document types the settings as a JSON node, which the generated client reads
 * as `unknown` — the same shape the form holds — and marks every member required, while `sourceText`
 * is only read when a new file was chosen: left out, the server keeps the text the list stores.
 */
function body(input: ImportListInput): components['schemas']['ImportListInputResource'] {
  return {
    type: input.type,
    name: input.name,
    settings: input.settings,
    policy: input.policy,
    qualityProfileId: input.qualityProfileId,
    libraryId: input.libraryId,
    enabled: input.enabled,
    syncIntervalHours: input.syncIntervalHours,
    plexPlaylist: input.plexPlaylist,
    m3uExport: input.m3uExport,
    ...(input.sourceText === undefined ? {} : { sourceText: input.sourceText }),
  } as components['schemas']['ImportListInputResource'];
}
