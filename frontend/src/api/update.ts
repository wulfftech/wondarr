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

/**
 * Whether a newer Wondarr has been released. Wondarr runs in a container and never updates itself:
 * the server asks GitHub, and these hooks show what it found.
 */

export type UpdateStatus = components['schemas']['UpdateResource'];
export type UpdateSettings = components['schemas']['UpdateSettingsResource'];

/** The update status. `['update']` is invalidated when the server broadcasts an `update` event. */
export const UPDATE_QUERY_KEY = ['update'] as const;

export const UPDATE_SETTINGS_QUERY_KEY = ['update-settings'] as const;

/** Where the setting lives, for the "Update checks are off" link. */
export const UPDATE_SETTINGS_PATH = '/settings/general';

/**
 * Whether a response is an update status. A page behind the shell is rendered by tests that answer
 * every unknown URL with something else, and a badge must never break the layout over it.
 */
function isUpdateStatus(value: unknown): value is UpdateStatus {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { currentVersion?: unknown }).currentVersion === 'string' &&
    typeof (value as { updateAvailable?: unknown }).updateAvailable === 'boolean'
  );
}

/** The last answer from the server; it makes no request to GitHub. */
export function useUpdateStatus(): UseQueryResult<UpdateStatus, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: UPDATE_QUERY_KEY,
    queryFn: async (): Promise<UpdateStatus> => {
      const { data, response } = await client.GET('/api/v1/update');

      if (!response.ok || !isUpdateStatus(data)) {
        throw new ApiError(response.status, 'The update status request failed.');
      }

      return data;
    },
  });
}

/** Asks GitHub now (the server reuses a check made less than a minute ago). */
export function useCheckForUpdate(): UseMutationResult<UpdateStatus, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<UpdateStatus> => {
      const { data, response } = await client.POST('/api/v1/update/check');

      if (!response.ok || !isUpdateStatus(data)) {
        throw new ApiError(response.status, 'The update check request failed.');
      }

      return data;
    },
    onSuccess: (status) => {
      queryClient.setQueryData(UPDATE_QUERY_KEY, status);
    },
  });
}

/** Whether update checks are on, and whether the environment owns the setting. */
export function useUpdateSettings(): UseQueryResult<UpdateSettings, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: UPDATE_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<UpdateSettings> => {
      const { data, response } = await client.GET('/api/v1/update/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The update settings request failed.');
      }

      return data;
    },
  });
}

/** A refused write: the server names the environment variable that owns the setting. */
export class UpdateSettingsError extends ApiError {
  constructor(status: number, message: string) {
    super(status, message);
    this.name = 'UpdateSettingsError';
  }
}

function firstMessage(body: unknown): string | undefined {
  const errors = (body as { errors?: Record<string, unknown> } | undefined)?.errors;

  if (typeof errors !== 'object' || errors === null) {
    return undefined;
  }

  for (const value of Object.values(errors)) {
    const entry: unknown = Array.isArray(value) ? value[0] : value;

    if (typeof entry === 'string' && entry !== '') {
      return entry;
    }
  }

  return undefined;
}

/** Switches update checks on or off. */
export function useSaveUpdateSettings(): UseMutationResult<UpdateSettings, Error, boolean> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (checkEnabled: boolean): Promise<UpdateSettings> => {
      const { data, error, response } = await client.PUT('/api/v1/update/settings', { body: { checkEnabled } });

      if (!response.ok || data === undefined) {
        throw new UpdateSettingsError(response.status, firstMessage(error) ?? 'The setting could not be saved.');
      }

      return data;
    },
    onSuccess: (settings) => {
      queryClient.setQueryData(UPDATE_SETTINGS_QUERY_KEY, settings);
      void queryClient.invalidateQueries({ queryKey: UPDATE_QUERY_KEY });
    },
  });
}
