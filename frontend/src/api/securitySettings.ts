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
 * The security switch on Settings → General: with `auth: none`, whether Wondarr answers only a request
 * addressed to a local host name (`server.require_local_host` in `config.yml`).
 */

export type SecuritySettings = components['schemas']['SecuritySettingsResource'];

export const SECURITY_SETTINGS_QUERY_KEY = ['security-settings'] as const;

/** Whether only local host names are answered, and whether the environment owns the setting. */
export function useSecuritySettings(): UseQueryResult<SecuritySettings, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: SECURITY_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<SecuritySettings> => {
      const { data, response } = await client.GET('/api/v1/config/security');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The security setting could not be read.');
      }

      return data;
    },
  });
}

/** Switches the check on or off. It applies to the next request, without a restart. */
export function useSaveSecuritySettings(): UseMutationResult<SecuritySettings, Error, boolean> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (requireLocalHost: boolean): Promise<SecuritySettings> => {
      const { data, error, response } = await client.PUT('/api/v1/config/security', {
        body: { requireLocalHost },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, firstMessage(error) ?? 'The setting could not be saved.');
      }

      return data;
    },
    onSuccess: (settings) => {
      queryClient.setQueryData(SECURITY_SETTINGS_QUERY_KEY, settings);
    },
  });
}
