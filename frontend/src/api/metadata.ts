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
 * The optional AcoustID and Last.fm keys. The server never returns a key: it says whether one is
 * stored (`…KeySet`) and whether the environment owns it (`…Locked`).
 */

export type MetadataSettingsResource = components['schemas']['MetadataSettingsResource'];
export type MetadataSettingsUpdateResource = components['schemas']['MetadataSettingsUpdateResource'];
export type MetadataKeyTestResource = components['schemas']['MetadataKeyTestResource'];

/** Which service a key belongs to, as the test endpoint names it. */
export type MetadataKeyService = 'lastfm' | 'acoustid';

export const METADATA_SETTINGS_QUERY_KEY = ['metadata', 'settings'] as const;

/** A refused write. The server gives every reason under `errors.settings`, carried here verbatim. */
export class MetadataUpdateError extends ApiError {
  /** The reasons the server gave, in the order it gave them. */
  readonly messages: string[];

  constructor(status: number, message: string, messages: string[]) {
    super(status, message);
    this.name = 'MetadataUpdateError';
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

/** Which of the optional keys are set, and which the environment owns. */
export function useMetadataSettings(): UseQueryResult<MetadataSettingsResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: METADATA_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<MetadataSettingsResource> => {
      const { data, response } = await client.GET('/api/v1/metadata/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The metadata settings request failed.');
      }

      return data;
    },
  });
}

/** Writes the keys in the update: an absent field is unchanged, an empty string removes the key. */
export function useUpdateMetadataSettings(): UseMutationResult<
  MetadataSettingsResource,
  Error,
  MetadataSettingsUpdateResource
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: MetadataSettingsUpdateResource): Promise<MetadataSettingsResource> => {
      const { data, error, response } = await client.PUT('/api/v1/metadata/settings', { body: update });

      if (!response.ok || data === undefined) {
        throw new MetadataUpdateError(response.status, 'The keys could not be saved.', errorMessages(error));
      }

      return data;
    },
    onSuccess: (result) => {
      queryClient.setQueryData(METADATA_SETTINGS_QUERY_KEY, result);
    },
  });
}

/** What the Test button sends: the typed key, or nothing to test the stored one. */
export interface MetadataKeyTestRequest {
  service: MetadataKeyService;
  key?: string;
}

/** Asks the service whether it accepts a key. Always resolves with `ok` and a message for a known service. */
export function useTestMetadataKey(): UseMutationResult<MetadataKeyTestResource, Error, MetadataKeyTestRequest> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (request: MetadataKeyTestRequest): Promise<MetadataKeyTestResource> => {
      const { data, response } = await client.POST('/api/v1/metadata/settings/test', {
        body: { service: request.service, key: request.key ?? null },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The key test request failed.');
      }

      return data;
    },
  });
}
