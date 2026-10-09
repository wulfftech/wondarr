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

/** The local login account the Forms authentication checks, as `/api/v1/auth/user` carries it. */

export type AuthUserResource = components['schemas']['AuthUserResource'];
export type UpdateAuthUserResource = components['schemas']['UpdateAuthUserResource'];

export const AUTH_USER_QUERY_KEY = ['auth', 'user'] as const;

/** A refused login-account write. Carries the problem's `errors`, keyed by the field they name. */
export class AuthUserUpdateError extends ApiError {
  /** The messages the server gave for each field, keyed by its lower-cased name. */
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, message: string, fieldErrors: Record<string, string[]>) {
    super(status, message);
    this.name = 'AuthUserUpdateError';
    this.fieldErrors = fieldErrors;
  }
}

/** Reads the `errors` member of an RFC 7807 problem into lower-cased field keys. */
function readFieldErrors(body: unknown): Record<string, string[]> {
  const result: Record<string, string[]> = {};

  if (typeof body !== 'object' || body === null) {
    return result;
  }

  const errors = (body as Record<string, unknown>).errors;

  if (typeof errors !== 'object' || errors === null) {
    return result;
  }

  for (const [key, value] of Object.entries(errors as Record<string, unknown>)) {
    const list: unknown[] = Array.isArray(value) ? value : [value];
    const messages = list.filter((entry): entry is string => typeof entry === 'string' && entry !== '');

    if (messages.length > 0) {
      result[key.toLowerCase()] = messages;
    }
  }

  return result;
}

/** Whether a login account exists, and its username. */
export function useAuthUser(): UseQueryResult<AuthUserResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: AUTH_USER_QUERY_KEY,
    queryFn: async (): Promise<AuthUserResource> => {
      const { data, response } = await client.GET('/api/v1/auth/user');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The login account request failed.');
      }

      return data;
    },
  });
}

/** Creates or replaces the login account (the API answers 204). */
export function useSetAuthUser(): UseMutationResult<void, Error, UpdateAuthUserResource> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (body: UpdateAuthUserResource): Promise<void> => {
      const { error, response } = await client.PUT('/api/v1/auth/user', { body });

      if (!response.ok) {
        const problem = typeof error === 'object' && error !== null ? (error as Record<string, unknown>) : {};
        const detail = typeof problem.detail === 'string' && problem.detail !== '' ? problem.detail : null;
        const title = typeof problem.title === 'string' && problem.title !== '' ? problem.title : null;

        throw new AuthUserUpdateError(
          response.status,
          detail ?? title ?? 'The login account could not be saved.',
          readFieldErrors(error),
        );
      }
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: AUTH_USER_QUERY_KEY });
    },
  });
}
