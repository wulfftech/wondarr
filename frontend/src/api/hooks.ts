import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import type { components } from './schema';
import { apiRequest } from './client';
import { useApiClient, useAppConfig } from './context';
import { ApiError } from './errors';
import { asHealthEntries, type CommandRequest, type HealthEntry, type TaskResource } from './types';

/** `GET /api/v1/system/status`, typed from the generated schema. */
export type SystemStatus = components['schemas']['SystemResource'];

/** The health list. `['health']` is also the key the SignalR event stream writes into. */
export const HEALTH_QUERY_KEY = ['health'] as const;

/** The task list. `['tasks']` is invalidated when the server broadcasts a `command` event. */
export const TASKS_QUERY_KEY = ['tasks'] as const;

/** The app identity, version, host and database facts. */
export function useSystemStatus(): UseQueryResult<SystemStatus, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: ['system', 'status'],
    queryFn: async (): Promise<SystemStatus> => {
      const { data, response } = await client.GET('/api/v1/system/status');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The status request failed.');
      }

      return data;
    },
  });
}

/** Every health check result, `ok` ones included. */
export function useHealth(): UseQueryResult<HealthEntry[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: HEALTH_QUERY_KEY,
    queryFn: async (): Promise<HealthEntry[]> => {
      const { data, response } = await client.GET('/api/v1/health');

      if (!response.ok) {
        throw new ApiError(response.status, 'The health request failed.');
      }

      return asHealthEntries(data);
    },
  });
}

/** The scheduled tasks the System section lists. */
export function useTasks(): UseQueryResult<TaskResource[], Error> {
  const config = useAppConfig();

  return useQuery({
    queryKey: TASKS_QUERY_KEY,
    queryFn: () => apiRequest<TaskResource[]>(config, 'api/v1/system/task'),
  });
}

/** Runs a scheduled task by name. */
export function useRunCommand(): UseMutationResult<void, Error, CommandRequest> {
  const config = useAppConfig();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (command: CommandRequest) =>
      apiRequest<void>(config, 'api/v1/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(command),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: TASKS_QUERY_KEY }),
  });
}
