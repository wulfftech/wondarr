import { useMutation, useQuery, type UseMutationResult, type UseQueryResult } from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import type { components } from './schema';
import { firstMessage } from './update';

/**
 * The feedback button. The server logs every submission and raises it as a GitHub issue: directly when
 * it has a token, otherwise by handing back GitHub's issue form, prefilled, for the person to submit.
 */

export type FeedbackStatus = components['schemas']['FeedbackStatusResource'];
export type FeedbackKind = components['schemas']['FeedbackKind'];
export type FeedbackSubmission = components['schemas']['FeedbackCreateResource'];
export type FeedbackResult = components['schemas']['FeedbackResultResource'];

export const FEEDBACK_QUERY_KEY = ['feedback'] as const;

/** The send's mutation key: the dialog stays open while one is in flight. */
export const FEEDBACK_SUBMIT_KEY = ['feedback-submit'] as const;

/** Whether a submission opens the issue directly. Asked when the dialog opens; it makes no request to GitHub. */
export function useFeedbackStatus(enabled: boolean): UseQueryResult<FeedbackStatus, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: FEEDBACK_QUERY_KEY,
    enabled,
    queryFn: async (): Promise<FeedbackStatus> => {
      const { data, response } = await client.GET('/api/v1/feedback');

      if (!response.ok || data === undefined) {
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
