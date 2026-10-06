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

/** The YouTube source's settings, its health probe's answer, and the default output policy. */

export type YouTubeSettingsResource = components['schemas']['YouTubeSettingsResource'];
export type YouTubeSettingsUpdateResource = components['schemas']['YouTubeSettingsUpdateResource'];
export type YouTubeStatusResource = components['schemas']['YouTubeStatusResource'];

export const YOUTUBE_SETTINGS_QUERY_KEY = ['youtube', 'settings'] as const;
export const YOUTUBE_STATUS_QUERY_KEY = ['youtube', 'status'] as const;

/** The fields the page may edit, in the camelCase spelling the API and `readOnlyFields` use. */
export const YOUTUBE_FIELDS = [
  'enabled',
  'cookiesPath',
  'poTokenBaseUrl',
  'allowVideos',
  'searchLimit',
  'ytdlpSleepRequestsSeconds',
  'ytdlpSleepIntervalSeconds',
  'ytdlpMaxSleepIntervalSeconds',
  'ytdlpRetries',
] as const;

export type YouTubeFieldName = (typeof YOUTUBE_FIELDS)[number];

/**
 * A refused settings write. The server answers 400 with every reason under the single
 * `errors.settings` key, so this carries the messages verbatim rather than a single string.
 */
export class YouTubeUpdateError extends ApiError {
  /** The reasons the server gave, in the order it gave them. */
  readonly messages: string[];

  constructor(status: number, message: string, messages: string[]) {
    super(status, message);
    this.name = 'YouTubeUpdateError';
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
 * `youtube.search_limit: must be between 1 and 50`.
 */
const MESSAGE_KEYS: Record<string, YouTubeFieldName> = {
  enabled: 'enabled',
  cookies_path: 'cookiesPath',
  po_token_base_url: 'poTokenBaseUrl',
  allow_videos: 'allowVideos',
  search_limit: 'searchLimit',
  'ytdlp:sleep_requests_seconds': 'ytdlpSleepRequestsSeconds',
  'ytdlp:sleep_interval_seconds': 'ytdlpSleepIntervalSeconds',
  'ytdlp:max_sleep_interval_seconds': 'ytdlpMaxSleepIntervalSeconds',
  'ytdlp:retries': 'ytdlpRetries',
};

/** One server message, placed against the field it names when it names one. */
export interface YouTubeProblem {
  /** The field the message is about, or `null` when the message is about the settings as a whole. */
  field: YouTubeFieldName | null;
  /** The message with any `youtube.<key>:` prefix removed. */
  message: string;
}

/**
 * Sorts the server's messages into per-field and general ones. A message that starts with a field's
 * `config.yml` key belongs to that field; everything else — including a read-only field's "set by
 * the environment" refusal — is shown under the form.
 */
export function readYouTubeProblems(messages: string[]): YouTubeProblem[] {
  return messages.map((message) => {
    const match = /^youtube\.([a-z_:]+):\s*(.*)$/.exec(message);

    if (match === null) {
      return { field: null, message };
    }

    const key: string = match[1] ?? '';
    const text: string = match[2] ?? '';
    const field = MESSAGE_KEYS[key];

    return field === undefined ? { field: null, message } : { field, message: text };
  });
}

/** The YouTube settings as they are stored. */
export function useYouTubeSettings(): UseQueryResult<YouTubeSettingsResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: YOUTUBE_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<YouTubeSettingsResource> => {
      const { data, response } = await client.GET('/api/v1/youtube/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The YouTube settings request failed.');
      }

      return data;
    },
  });
}

/** What the health probe last found: the yt-dlp version and the JS-runtime flag. */
export function useYouTubeStatus(): UseQueryResult<YouTubeStatusResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: YOUTUBE_STATUS_QUERY_KEY,
    queryFn: async (): Promise<YouTubeStatusResource> => {
      const { data, response } = await client.GET('/api/v1/youtube/status');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The YouTube status request failed.');
      }

      return data;
    },
  });
}

/** Writes the changed settings; a refusal carries every reason the server gave. */
export function useUpdateYouTubeSettings(): UseMutationResult<
  YouTubeSettingsResource,
  Error,
  YouTubeSettingsUpdateResource
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (update: YouTubeSettingsUpdateResource): Promise<YouTubeSettingsResource> => {
      const { data, error, response } = await client.PUT('/api/v1/youtube/settings', { body: update });

      if (!response.ok || data === undefined) {
        const messages = errorMessages(error);

        throw new YouTubeUpdateError(
          response.status,
          problemText(error, 'detail') ?? problemText(error, 'title') ?? 'The YouTube settings could not be saved.',
          messages,
        );
      }

      return data;
    },
    onSuccess: (result) => {
      // The settings the server echoed back are what the form should show; the status card's
      // cached probe is untouched by a settings write, but a cheap refetch keeps them in step.
      queryClient.setQueryData(YOUTUBE_SETTINGS_QUERY_KEY, result);
    },
  });
}

/** Runs the health probe now, whatever it answered before. */
export function useTestYouTube(): UseMutationResult<YouTubeStatusResource, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<YouTubeStatusResource> => {
      const { data, response } = await client.POST('/api/v1/youtube/test');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The YouTube test request failed.');
      }

      return data;
    },
    onSuccess: (result) => {
      queryClient.setQueryData(YOUTUBE_STATUS_QUERY_KEY, result);
    },
  });
}
