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

/** The bundled slskd's account, sharing and transfer settings, and what it is doing right now. */

export type SoulseekSettingsResource = components['schemas']['SoulseekSettingsResource'];
export type SoulseekSettingsUpdateResource = components['schemas']['SoulseekSettingsUpdateResource'];
export type SoulseekSettingsUpdateResponseResource = components['schemas']['SoulseekSettingsUpdateResponseResource'];
export type SoulseekStatusResource = components['schemas']['SoulseekStatusResource'];

export const SOULSEEK_SETTINGS_QUERY_KEY = ['soulseek', 'settings'] as const;
export const SOULSEEK_STATUS_QUERY_KEY = ['soulseek', 'status'] as const;

/** How often the status card refreshes while the Soulseek settings page is open. */
export const SOULSEEK_STATUS_REFETCH_MS = 15_000;

/** The fields the page may edit, in the camelCase spelling the API and `readOnlyFields` use. */
export const SOULSEEK_FIELDS = [
  'username',
  'password',
  'listenPort',
  'shareLibrary',
  'sharedFolders',
  'uploadSlots',
  'uploadSpeedLimitKib',
  'distributedNetwork',
  'downloadsDir',
  'incompleteDir',
] as const;

export type SoulseekFieldName = (typeof SOULSEEK_FIELDS)[number];

/**
 * A refused settings write. The server answers 400 with every reason under the single `errors.settings`
 * key, so this carries the messages verbatim rather than a single per-field string.
 */
export class SoulseekUpdateError extends ApiError {
  /** The reasons the server gave, in the order it gave them. */
  readonly messages: string[];

  constructor(status: number, message: string, messages: string[]) {
    super(status, message);
    this.name = 'SoulseekUpdateError';
    this.messages = messages;
  }
}

/** Reads the `errors` member of an RFC 7807 problem, whatever shape its values arrived in. */
function errorMessages(body: unknown): string[] {
  if (typeof body !== 'object' || body === null) {
    return [];
  }

  const errors = (body as Record<string, unknown>).errors;

  if (typeof errors !== 'object' || errors === null) {
    return [];
  }

  const messages: string[] = [];

  for (const value of Object.values(errors as Record<string, unknown>)) {
    const list: unknown[] = Array.isArray(value) ? value : [value];

    for (const entry of list) {
      if (typeof entry === 'string' && entry !== '') {
        messages.push(entry);
      }
    }
  }

  return messages;
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
 * The `config.yml` key each field is named by in the server's validation messages, e.g. the message
 * `soulseek.listen_port: must be between 1024 and 65535`.
 */
const MESSAGE_KEYS: Record<string, SoulseekFieldName> = {
  username: 'username',
  password: 'password',
  listen_port: 'listenPort',
  share_library: 'shareLibrary',
  shared_folders: 'sharedFolders',
  upload_slots: 'uploadSlots',
  upload_speed_limit_kib: 'uploadSpeedLimitKib',
  distributed_network: 'distributedNetwork',
  downloads_dir: 'downloadsDir',
  incomplete_dir: 'incompleteDir',
};

/** One server message, placed against the field it names when it names one. */
export interface SoulseekProblem {
  /** The field the message is about, or `null` when the message is about the settings as a whole. */
  field: SoulseekFieldName | null;
  /** The message with any `soulseek.<key>:` prefix removed. */
  message: string;
}

/**
 * Sorts the server's messages into per-field and general ones. A message that starts with a field's
 * `config.yml` key belongs to that field; everything else — including a read-only field's "set by the
 * environment" refusal — is shown under the form.
 */
export function readSoulseekProblems(messages: string[]): SoulseekProblem[] {
  return messages.map((message) => {
    const match = /^soulseek\.([a-z_]+):\s*(.*)$/.exec(message);

    if (match === null) {
      return { field: null, message };
    }

    const key: string = match[1] ?? '';
    const text: string = match[2] ?? '';
    const field = MESSAGE_KEYS[key];

    return field === undefined ? { field: null, message } : { field, message: text };
  });
}

/** The Soulseek settings as they are stored. */
export function useSoulseekSettings(): UseQueryResult<SoulseekSettingsResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: SOULSEEK_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<SoulseekSettingsResource> => {
      const { data, response } = await client.GET('/api/v1/soulseek/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The Soulseek settings request failed.');
      }

      return data;
    },
  });
}

/** What slskd is doing: login, sharing and the search allowance. */
export function useSoulseekStatus(): UseQueryResult<SoulseekStatusResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: SOULSEEK_STATUS_QUERY_KEY,
    queryFn: async (): Promise<SoulseekStatusResource> => {
      const { data, response } = await client.GET('/api/v1/soulseek/status');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The Soulseek status request failed.');
      }

      return data;
    },
    refetchInterval: SOULSEEK_STATUS_REFETCH_MS,
  });
}

/** Writes the changed settings; a refusal carries every reason the server gave. */
export function useUpdateSoulseekSettings(): UseMutationResult<
  SoulseekSettingsUpdateResponseResource,
  Error,
  SoulseekSettingsUpdateResource
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: SoulseekSettingsUpdateResource): Promise<SoulseekSettingsUpdateResponseResource> => {
      const { data, error, response } = await client.PUT('/api/v1/soulseek/settings', { body: update });

      if (!response.ok || data === undefined) {
        const messages = errorMessages(error);

        throw new SoulseekUpdateError(
          response.status,
          problemText(error, 'detail') ?? problemText(error, 'title') ?? 'The Soulseek settings could not be saved.',
          messages,
        );
      }

      return data;
    },
    onSuccess: (result) => {
      // The settings the server echoed back are what the form should show, and the status card
      // (state, sharing, search budget) has probably moved on with the write.
      queryClient.setQueryData(SOULSEEK_SETTINGS_QUERY_KEY, result.settings);
      void queryClient.invalidateQueries({ queryKey: SOULSEEK_STATUS_QUERY_KEY });
    },
  });
}
