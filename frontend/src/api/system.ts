import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import type { AppConfig } from './bootstrap';
import { apiOrigin } from './client';
import { useApiClient, useAppConfig } from './context';
import { ApiError } from './errors';
import { pagingValues, type Paging } from './paging';
import { readEnum } from './profiles';
import type { components } from './schema';
import { COMMANDS_QUERY_KEY, type CommandStatusName } from './songs';

/**
 * The System section's own data: the backups, the app log and the recent commands. Every download
 * URL is built through {@link apiOrigin} so it follows the URL base, and the cookie — not the API
 * key — authenticates it, because a plain link cannot carry a header.
 */

export type BackupResource = components['schemas']['BackupResource'];
export type RestoreResource = components['schemas']['RestoreResource'];
export type LogResource = components['schemas']['LogResource'];
export type LogPage = components['schemas']['PagingResourceOfLogResource'];
export type LogFileResource = components['schemas']['LogFileResource'];
export type CommandResource = components['schemas']['CommandResource'];

/** The query keys the System pages read and the mutations refresh. */
export const BACKUPS_QUERY_KEY = ['backups'] as const;
export const LOG_QUERY_KEY = ['logs'] as const;
export const LOG_FILES_QUERY_KEY = ['log-files'] as const;

/** The recent commands. Nested under `['commands']`, so a `command` event refreshes it too. */
export const COMMAND_HISTORY_QUERY_KEY = [...COMMANDS_QUERY_KEY, 'history'] as const;

/** How often the command history is polled while a command is queued or running, in milliseconds. */
export const COMMAND_POLL_INTERVAL_MS = 5000;

/** How often `/ping` is asked whether the API host is back, in milliseconds. */
export const PING_POLL_INTERVAL_MS = 2000;

/** The path the container health check uses; it answers 200 once the host is listening again. */
const PING_PATH = 'ping';

/** The response header that says the log scan stopped at its byte bound. */
const TRUNCATED_HEADER = 'X-Wondarr-Log-Truncated';

/** What `GET /api/v1/log` filters on. */
export interface LogFilters {
  /** The minimum level to show, spelled as the API reads it (`Information`, `Debug`, …). */
  level?: string;
  /** A case-insensitive substring of the message. */
  filter?: string;
}

/** One answer of the log list: the page, plus whether older entries were left out. */
export interface LogsResult {
  /** The page the API sent. */
  page: LogPage;
  /** True when the scan stopped at its byte bound before reaching the oldest entry. */
  truncated: boolean;
}

/**
 * The paging and filter query as something the typed client accepts. The document describes no query
 * for `/api/v1/log` — the controller reads the *arr parameters and the two filters off the request by
 * hand — so the generated query type is narrower than what actually travels. The assertion belongs
 * here rather than at every call site.
 */
function logQuery(values: Record<string, string | number>): never {
  return values as never;
}

/** The backups on disk, newest first. */
export function useBackups(): UseQueryResult<BackupResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: BACKUPS_QUERY_KEY,
    queryFn: async (): Promise<BackupResource[]> => {
      const { data, response } = await client.GET('/api/v1/system/backup');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The backup list request failed.');
      }

      return data;
    },
  });
}

/** Makes a manual backup now, and refreshes the list. */
export function useCreateBackup(): UseMutationResult<BackupResource, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<BackupResource> => {
      const { data, response } = await client.POST('/api/v1/system/backup');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The backup could not be made.');
      }

      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: BACKUPS_QUERY_KEY }),
  });
}

/** Deletes one backup, and refreshes the list. */
export function useDeleteBackup(): UseMutationResult<void, Error, string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string): Promise<void> => {
      const { response } = await client.DELETE('/api/v1/system/backup/{id}', { params: { path: { id } } });

      if (!response.ok) {
        throw new ApiError(response.status, 'The backup could not be deleted.');
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: BACKUPS_QUERY_KEY }),
  });
}

/** The message a failed restore answered with, or a fallback when the body said nothing useful. */
function restoreError(status: number, body: unknown): ApiError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const message = typeof detail === 'string' && detail !== '' ? detail : 'The backup could not be restored.';

  return new ApiError(status, message);
}

/** Stages a restore from a backup the app already holds. The app stops and applies it on the next start. */
export function useRestoreBackup(): UseMutationResult<RestoreResource, Error, string> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (id: string): Promise<RestoreResource> => {
      const { data, error, response } = await client.POST('/api/v1/system/backup/restore/{id}', {
        params: { path: { id } },
      });

      if (!response.ok || data === undefined) {
        throw restoreError(response.status, error);
      }

      return data;
    },
  });
}

/**
 * Stages a restore from an uploaded zip. The document describes the endpoint without a body, so the
 * generated client types it as taking none and the form goes out through `fetch` directly; `fetch`
 * sets the multipart boundary itself, which is why no `Content-Type` is named here.
 */
export function useUploadRestore(): UseMutationResult<RestoreResource, Error, File> {
  const config = useAppConfig();

  return useMutation({
    mutationFn: async (file: File): Promise<RestoreResource> => {
      const form = new FormData();

      form.append('file', file, file.name);

      const response = await fetch(`${apiOrigin(config)}/api/v1/system/backup/restore/upload`, {
        method: 'POST',
        headers: { Accept: 'application/json', 'X-Api-Key': config.apiKey },
        body: form,
        credentials: 'same-origin',
      });

      if (!response.ok) {
        throw restoreError(response.status, await response.json().catch(() => undefined));
      }

      return (await response.json()) as RestoreResource;
    },
  });
}

/** The URL that downloads one backup, through the URL base, so the session cookie travels with it. */
export function backupDownloadUrl(config: AppConfig, id: string): string {
  return `${apiOrigin(config)}/api/v1/system/backup/${encodeURIComponent(id)}/download`;
}

/** The URL that downloads one log file, through the URL base. */
export function logFileDownloadUrl(config: AppConfig, filename: string): string {
  return `${apiOrigin(config)}/api/v1/log/file/${encodeURIComponent(filename)}`;
}

/** The log entries, newest first, filtered and paged. */
export function useLogs(paging: Paging, filters: LogFilters = {}): UseQueryResult<LogsResult, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: [...LOG_QUERY_KEY, paging, filters],
    queryFn: async (): Promise<LogsResult> => {
      const query: Record<string, string | number> = pagingValues(paging);

      if (filters.level !== undefined && filters.level !== '') {
        query.level = filters.level;
      }

      if (filters.filter !== undefined && filters.filter !== '') {
        query.filter = filters.filter;
      }

      const { data, response } = await client.GET('/api/v1/log', { params: { query: logQuery(query) } });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The log request failed.');
      }

      return { page: data, truncated: response.headers.get(TRUNCATED_HEADER) === 'true' };
    },
  });
}

/** The log files on disk, newest first. */
export function useLogFiles(): UseQueryResult<LogFileResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: LOG_FILES_QUERY_KEY,
    queryFn: async (): Promise<LogFileResource[]> => {
      const { data, response } = await client.GET('/api/v1/log/file');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The log file list request failed.');
      }

      return data;
    },
  });
}

/** True while a command is queued or running, so the history keeps itself fresh. */
function isRunning(status: unknown): boolean {
  const name = readEnum<CommandStatusName>(status);

  return name === 'queued' || name === 'started';
}

/** The recent commands, newest first, for the Tasks page. */
export function useCommands(): UseQueryResult<CommandResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: COMMAND_HISTORY_QUERY_KEY,
    refetchInterval: (query) =>
      (query.state.data ?? []).some((command) => isRunning(command.status)) ? COMMAND_POLL_INTERVAL_MS : false,
    queryFn: async (): Promise<CommandResource[]> => {
      const { data, response } = await client.GET('/api/v1/command');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The command history request failed.');
      }

      return data;
    },
  });
}

/** Asks `/ping` whether the API host is back up, for the wait after a restore. */
export function usePing(enabled: boolean): UseQueryResult<boolean, Error> {
  const config = useAppConfig();

  return useQuery({
    queryKey: ['ping'],
    enabled,
    refetchInterval: PING_POLL_INTERVAL_MS,
    retry: false,
    queryFn: async (): Promise<boolean> => {
      const response = await fetch(`${apiOrigin(config)}/${PING_PATH}`, { credentials: 'same-origin' });

      return response.ok;
    },
  });
}

/** Reloads the whole app: a restore replaced the settings, so the SPA has to re-bootstrap. */
export function reloadAfterRestore(): void {
  window.location.reload();
}
