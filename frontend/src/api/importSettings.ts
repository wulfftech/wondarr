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
import { firstMessage } from './update';

/**
 * The import setting on Settings → Download clients: whether Wondarr removes what a download left behind
 * after an import (`import.clean_leftover_folders` in `config.yml`).
 */

export type ImportSettings = components['schemas']['ImportSettingsResource'];

export const IMPORT_SETTINGS_QUERY_KEY = ['import-settings'] as const;

/** Whether leftover folders are cleaned up, and whether the environment owns the setting. */
export function useImportSettings(): UseQueryResult<ImportSettings, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: IMPORT_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<ImportSettings> => {
      const { data, response } = await client.GET('/api/v1/config/import');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The import settings request failed.');
      }

      return data;
    },
  });
}

/** A refused write: the server names the environment variable that owns the setting. */
export class ImportSettingsError extends ApiError {
  constructor(status: number, message: string) {
    super(status, message);
    this.name = 'ImportSettingsError';
  }
}

/** Switches the cleanup of leftover folders on or off. */
export function useSaveImportSettings(): UseMutationResult<ImportSettings, Error, boolean> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (cleanLeftoverFolders: boolean): Promise<ImportSettings> => {
      const { data, error, response } = await client.PUT('/api/v1/config/import', {
        body: { cleanLeftoverFolders },
      });

      if (!response.ok || data === undefined) {
        throw new ImportSettingsError(response.status, firstMessage(error) ?? 'The setting could not be saved.');
      }

      return data;
    },
    onSuccess: (settings) => {
      queryClient.setQueryData(IMPORT_SETTINGS_QUERY_KEY, settings);
    },
  });
}
