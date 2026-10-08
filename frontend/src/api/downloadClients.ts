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
import { problemError, type ProviderTestResource } from './indexers';
import type { components } from './schema';

/**
 * The download clients Wondarr hands torrents and usenet posts to (DECISIONS build session 8 #1, #8):
 * one row per qBittorrent or SABnzbd, with its settings and its remote path mappings. The editor is
 * rendered from `GET /api/v1/downloadclient/schema`.
 */

export type DownloadClientResource = components['schemas']['DownloadClientResource'];
export type DownloadClientSchemaResource = components['schemas']['DownloadClientSchemaResource'];

/** The query keys the download-client screens read and invalidate. */
export const DOWNLOAD_CLIENTS_QUERY_KEY = ['downloadClients'] as const;
export const DOWNLOAD_CLIENT_SCHEMA_QUERY_KEY = ['downloadClients', 'schema'] as const;

/** The body of a create, an update or a test; `id` is only read by the test, to keep a masked secret. */
export interface DownloadClientInput {
  name: string;
  type: string;
  enabled: boolean;
  priority: number;
  settings: Record<string, unknown>;
  id?: number;
}

function invalidateClients(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: DOWNLOAD_CLIENTS_QUERY_KEY });
}

/** Every download client. */
export function useDownloadClients(): UseQueryResult<DownloadClientResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: DOWNLOAD_CLIENTS_QUERY_KEY,
    queryFn: async (): Promise<DownloadClientResource[]> => {
      const { data, response } = await client.GET('/api/v1/downloadclient');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The download clients request failed.');
      }

      return data;
    },
  });
}

/** Every download client type and the settings form it wants; fixed for the life of the server. */
export function useDownloadClientSchema(): UseQueryResult<DownloadClientSchemaResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: DOWNLOAD_CLIENT_SCHEMA_QUERY_KEY,
    staleTime: Infinity,
    queryFn: async (): Promise<DownloadClientSchemaResource[]> => {
      const { data, response } = await client.GET('/api/v1/downloadclient/schema');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The download client schema request failed.');
      }

      return data;
    },
  });
}

/** Adds a download client. */
export function useCreateDownloadClient(): UseMutationResult<DownloadClientResource, Error, DownloadClientInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: DownloadClientInput): Promise<DownloadClientResource> => {
      const { data, error, response } = await client.POST('/api/v1/downloadclient', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The download client could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateClients(queryClient),
  });
}

/** Replaces a download client's settings. */
export function useUpdateDownloadClient(): UseMutationResult<
  DownloadClientResource,
  Error,
  DownloadClientInput & { id: number }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, ...input }: DownloadClientInput & { id: number }): Promise<DownloadClientResource> => {
      const { data, error, response } = await client.PUT('/api/v1/downloadclient/{id}', {
        params: { path: { id } },
        body: body(input),
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The download client could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateClients(queryClient),
  });
}

/** Deletes a download client; a 409 names the indexers that still use it. */
export function useDeleteDownloadClient(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/downloadclient/{id}', { params: { path: { id } } });

      if (!response.ok) {
        throw problemError(response.status, error, 'The download client could not be deleted.');
      }
    },
    onSuccess: () => invalidateClients(queryClient),
  });
}

/** Connects to the client through the draft; the answer says whether it worked. */
export function useTestDownloadClient(): UseMutationResult<ProviderTestResource, Error, DownloadClientInput> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (input: DownloadClientInput): Promise<ProviderTestResource> => {
      const { data, error, response } = await client.POST('/api/v1/downloadclient/test', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The download client could not be tested.');
      }

      return data;
    },
  });
}

function body(input: DownloadClientInput): components['schemas']['DownloadClientInputResource'] {
  return {
    name: input.name,
    type: input.type,
    enabled: input.enabled,
    priority: input.priority,
    settings: input.settings,
    ...(input.id === undefined ? {} : { id: input.id }),
  };
}
