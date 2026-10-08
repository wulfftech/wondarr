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
import type { components } from './schema';

/**
 * The indexers Wondarr searches for torrents and usenet posts (DECISIONS build session 8 #1): one row
 * per Torznab or Newznab feed (or Prowlarr, or a Gazelle tracker), with its settings, its priority and
 * the download client its grabs go to. The editor is rendered from `GET /api/v1/indexer/schema`.
 */

export type IndexerResource = components['schemas']['IndexerResource'];
export type IndexerSchemaResource = components['schemas']['IndexerSchemaResource'];
export type ProviderTestResource = components['schemas']['ProviderTestResource'];

/** The query keys the indexer screens read and invalidate. */
export const INDEXERS_QUERY_KEY = ['indexers'] as const;
export const INDEXER_SCHEMA_QUERY_KEY = ['indexers', 'schema'] as const;

/** The protocols an indexer serves, as the API spells them. */
export type DownloadProtocol = 'torrent' | 'usenet';

/** The body of a create, an update or a test; `id` is only read by the test, to keep a masked secret. */
export interface IndexerInput {
  name: string;
  type: string;
  protocol: DownloadProtocol | null;
  enabled: boolean;
  priority: number;
  downloadClientId: number | null;
  settings: Record<string, unknown>;
  id?: number;
}

/** A failed response as the forms render it: the problem's detail, or its first field message. */
export function problemError(status: number, body: unknown, fallback: string): ValidationError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const fields = validationFields(body);
  const first = Object.values(fields)[0];
  const message = typeof detail === 'string' && detail !== '' ? detail : (first ?? fallback);

  return new ValidationError(status, message, fields);
}

function invalidateIndexers(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: INDEXERS_QUERY_KEY });
}

/** Every indexer. */
export function useIndexers(): UseQueryResult<IndexerResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: INDEXERS_QUERY_KEY,
    queryFn: async (): Promise<IndexerResource[]> => {
      const { data, response } = await client.GET('/api/v1/indexer');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The indexers request failed.');
      }

      return data;
    },
  });
}

/** Every indexer type and the settings form it wants; fixed for the life of the server. */
export function useIndexerSchema(): UseQueryResult<IndexerSchemaResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: INDEXER_SCHEMA_QUERY_KEY,
    staleTime: Infinity,
    queryFn: async (): Promise<IndexerSchemaResource[]> => {
      const { data, response } = await client.GET('/api/v1/indexer/schema');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The indexer schema request failed.');
      }

      return data;
    },
  });
}

/** Adds an indexer. */
export function useCreateIndexer(): UseMutationResult<IndexerResource, Error, IndexerInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: IndexerInput): Promise<IndexerResource> => {
      const { data, error, response } = await client.POST('/api/v1/indexer', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The indexer could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateIndexers(queryClient),
  });
}

/** Replaces an indexer's settings. */
export function useUpdateIndexer(): UseMutationResult<IndexerResource, Error, IndexerInput & { id: number }> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, ...input }: IndexerInput & { id: number }): Promise<IndexerResource> => {
      const { data, error, response } = await client.PUT('/api/v1/indexer/{id}', {
        params: { path: { id } },
        body: body(input),
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The indexer could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateIndexers(queryClient),
  });
}

/** Deletes an indexer. */
export function useDeleteIndexer(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/indexer/{id}', { params: { path: { id } } });

      if (!response.ok) {
        throw problemError(response.status, error, 'The indexer could not be deleted.');
      }
    },
    onSuccess: () => invalidateIndexers(queryClient),
  });
}

/** Asks the indexer for its capabilities through the draft; the answer says whether it worked. */
export function useTestIndexer(): UseMutationResult<ProviderTestResource, Error, IndexerInput> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (input: IndexerInput): Promise<ProviderTestResource> => {
      const { data, error, response } = await client.POST('/api/v1/indexer/test', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The indexer could not be tested.');
      }

      return data;
    },
  });
}

function body(input: IndexerInput): components['schemas']['IndexerInputResource'] {
  return {
    name: input.name,
    type: input.type,
    protocol: input.protocol,
    enabled: input.enabled,
    priority: input.priority,
    downloadClientId: input.downloadClientId,
    settings: input.settings,
    ...(input.id === undefined ? {} : { id: input.id }),
  };
}
