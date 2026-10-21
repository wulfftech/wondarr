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
 * The feedback button and its settings card. The header shows the button only once a GitHub token is set
 * (Settings → General → Contribute with feedback). The server logs every submission and opens the issue
 * with that token; when GitHub refuses, it hands back GitHub's issue form, prefilled, instead.
 */

export type FeedbackStatus = components['schemas']['FeedbackStatusResource'];
export type FeedbackKind = components['schemas']['FeedbackKind'];
export type FeedbackSubmission = components['schemas']['FeedbackCreateResource'];
export type FeedbackResult = components['schemas']['FeedbackResultResource'];
export type FeedbackSettings = components['schemas']['FeedbackSettingsResource'];
export type FeedbackTokenTest = components['schemas']['FeedbackTokenTestResource'];

export const FEEDBACK_QUERY_KEY = ['feedback'] as const;

export const FEEDBACK_SETTINGS_QUERY_KEY = ['feedback-settings'] as const;

/** The send's mutation key: the dialog stays open while one is in flight. */
export const FEEDBACK_SUBMIT_KEY = ['feedback-submit'] as const;

/** Where the card lives, for links to it. */
export const FEEDBACK_SETTINGS_PATH = '/settings/general';

/**
 * Whether a response is a feedback status. The shell is rendered by tests that answer every unknown URL
 * with something else, and the header must never break over it.
 */
function isFeedbackStatus(value: unknown): value is FeedbackStatus {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { tokenConfigured?: unknown }).tokenConfigured === 'boolean' &&
    typeof (value as { autoRaise?: unknown }).autoRaise === 'boolean'
  );
}

/** Whether a token is set and a submission opens the issue directly. Makes no request to GitHub. */
export function useFeedbackStatus(): UseQueryResult<FeedbackStatus, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: FEEDBACK_QUERY_KEY,
    queryFn: async (): Promise<FeedbackStatus> => {
      const { data, response } = await client.GET('/api/v1/feedback');

      if (!response.ok || !isFeedbackStatus(data)) {
        throw new ApiError(response.status, 'The feedback settings could not be read.');
      }

      return data;
    },
  });
}

/** Sends the feedback. A GitHub that refuses is still a success: the result carries the form link and why. */
export function useSubmitFeedback(): UseMutationResult<FeedbackResult, Error, FeedbackSubmission> {
  const client = useApiClient();

  return useMutation({
    mutationKey: FEEDBACK_SUBMIT_KEY,
    mutationFn: async (body: FeedbackSubmission): Promise<FeedbackResult> => {
      const { data, error, response } = await client.POST('/api/v1/feedback', { body });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, firstMessage(error) ?? 'The feedback could not be sent.');
      }

      return data;
    },
  });
}

/** Whether a token is stored and whether the environment owns it. Never the token. */
export function useFeedbackSettings(): UseQueryResult<FeedbackSettings, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: FEEDBACK_SETTINGS_QUERY_KEY,
    queryFn: async (): Promise<FeedbackSettings> => {
      const { data, response } = await client.GET('/api/v1/feedback/settings');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The feedback settings could not be read.');
      }

      return data;
    },
  });
}

/** Stores a token, or removes it with an empty one. The header's button follows at once. */
export function useSaveFeedbackToken(): UseMutationResult<FeedbackSettings, Error, string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (token: string): Promise<FeedbackSettings> => {
      const { data, error, response } = await client.PUT('/api/v1/feedback/settings', { body: { token } });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, firstMessage(error) ?? 'The token could not be saved.');
      }

      return data;
    },
    onSuccess: (settings) => {
      queryClient.setQueryData(FEEDBACK_SETTINGS_QUERY_KEY, settings);
      void queryClient.invalidateQueries({ queryKey: FEEDBACK_QUERY_KEY });
    },
  });
}

/** Asks GitHub about the typed token, or the stored one when nothing is typed. Opens nothing. */
export function useTestFeedbackToken(): UseMutationResult<FeedbackTokenTest, Error, string | undefined> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (token: string | undefined): Promise<FeedbackTokenTest> => {
      const { data, response } = await client.POST('/api/v1/feedback/settings/test', {
        body: { token: token ?? null },
      });

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The token test request failed.');
      }

      return data;
    },
  });
}
