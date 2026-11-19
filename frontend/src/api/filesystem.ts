import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import type { components } from './schema';
import { problemError } from './songs';

/** The folder picker's backend: the mounted places a folder may be chosen, and a way to make a new one. */

export type FolderListingResource = components['schemas']['FolderListingResource'];
export type FolderResource = components['schemas']['FolderResource'];

export const FILESYSTEM_QUERY_KEY = ['filesystem'] as const;

/**
 * The roots when `path` is null, else the sub-folders of that folder. A folder that has gone, or is outside every
 * root, comes back as an error the picker shows.
 */
export function useFolderListing(path: string | null, enabled: boolean): UseQueryResult<FolderListingResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...FILESYSTEM_QUERY_KEY, path],
    enabled,
    retry: false,
    queryFn: async (): Promise<FolderListingResource> => {
      const { data, error, response } = await client.GET('/api/v1/filesystem', {
        params: { query: path === null ? {} : { path } },
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The folder list could not be read.');
      }

      return data;
    },
  });
}

/** Makes a folder named `name` inside `parent`. */
export function useCreateFolder(): UseMutationResult<FolderResource, Error, { parent: string; name: string }> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (folder: { parent: string; name: string }): Promise<FolderResource> => {
      const { data, error, response } = await client.POST('/api/v1/filesystem/folder', { body: folder });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The folder could not be created.');
      }

      return data;
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: FILESYSTEM_QUERY_KEY }),
  });
}
