import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query';
import { useApiClient } from './context';
import { ApiError } from './errors';
import { validationFields, ValidationError } from './profiles';
import type { components } from './schema';

/**
 * The notifications Wondarr sends when something happens (ARCHITECTURE §5.7): one row per endpoint,
 * which provider it is, its settings and the events it wants. The editor is rendered from
 * `GET /api/v1/notification/schema`, so a provider added on the server needs no change here.
 */

export type NotificationResource = components['schemas']['NotificationResource'];
export type NotificationSchemaResource = components['schemas']['NotificationSchemaResource'];
export type NotificationFieldResource = components['schemas']['NotificationFieldResource'];

/** The query keys the notification screens read and invalidate. */
export const NOTIFICATIONS_QUERY_KEY = ['notifications'] as const;
export const NOTIFICATION_SCHEMA_QUERY_KEY = ['notifications', 'schema'] as const;

/** The value a stored secret reads back as, and the value that asks the server to keep it. */
export const SECRET_MASK = '********';

/**
 * The events a notification may subscribe to, with the name the *arr APIs use
 * (`Wondarr.Core.Notifications.NotificationEventNames`). The order is the order the form lists them.
 */
export const NOTIFICATION_EVENTS: readonly { value: string; label: string }[] = [
  { value: 'grab', label: 'On grab' },
  { value: 'import', label: 'On import' },
  { value: 'upgrade', label: 'On upgrade' },
  { value: 'failure', label: 'On download failure' },
  { value: 'health', label: 'On health issue' },
];

/** What a field's `type` tells the form to render. */
export type NotificationFieldType = 'text' | 'url' | 'password' | 'select' | 'number' | 'checkbox' | 'keyValueList';

/** One notification's settings, as the provider's fields name them. */
export type NotificationSettings = Record<string, unknown>;

/** One key/value row of a `keyValueList` field, at the wire shape `WebhookSettings` reads. */
export interface NotificationKeyValue {
  key: string;
  value: string;
}

/**
 * The body of a create, an update or a test. `id` is only read by the test endpoint: it names the row
 * a masked secret should be kept from.
 */
export interface NotificationInput {
  name: string;
  implementation: string;
  enabled: boolean;
  events: string[];
  settings: NotificationSettings;
  id?: number;
}

/**
 * Turns a failed response into the error the forms render, reusing the problem reader the other forms
 * share. The body is the one the client has already parsed: the response's own body is consumed by
 * then, so re-reading it would lose the field messages the server sent.
 */
function problemError(status: number, body: unknown, fallback: string): ValidationError {
  const problem = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const detail = problem.detail ?? problem.title;
  const message = typeof detail === 'string' && detail !== '' ? detail : fallback;

  return new ValidationError(status, message, validationFields(body));
}

/** Everything a create, an update or a delete changes. */
function invalidateNotifications(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: NOTIFICATIONS_QUERY_KEY });
}

/** Every notification, ordered by id. */
export function useNotifications(): UseQueryResult<NotificationResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: NOTIFICATIONS_QUERY_KEY,
    queryFn: async (): Promise<NotificationResource[]> => {
      const { data, response } = await client.GET('/api/v1/notification');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The notifications request failed.');
      }

      return data;
    },
  });
}

/**
 * Every provider and the settings form it wants. The schema is fixed for the life of the server, so
 * it is fetched once and never considered stale.
 */
export function useNotificationSchema(): UseQueryResult<NotificationSchemaResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: NOTIFICATION_SCHEMA_QUERY_KEY,
    staleTime: Infinity,
    queryFn: async (): Promise<NotificationSchemaResource[]> => {
      const { data, response } = await client.GET('/api/v1/notification/schema');

      if (!response.ok || data === undefined) {
        throw new ApiError(response.status, 'The notification schema request failed.');
      }

      return data;
    },
  });
}

/** Adds a notification. */
export function useCreateNotification(): UseMutationResult<NotificationResource, Error, NotificationInput> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (input: NotificationInput): Promise<NotificationResource> => {
      const { data, error, response } = await client.POST('/api/v1/notification', { body: body(input) });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The notification could not be added.');
      }

      return data;
    },
    onSuccess: () => invalidateNotifications(queryClient),
  });
}

/** Replaces a notification's settings and subscriptions. */
export function useUpdateNotification(): UseMutationResult<
  NotificationResource,
  Error,
  NotificationInput & { id: number }
> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ id, ...input }: NotificationInput & { id: number }): Promise<NotificationResource> => {
      const { data, error, response } = await client.PUT('/api/v1/notification/{id}', {
        params: { path: { id } },
        body: body(input),
      });

      if (!response.ok || data === undefined) {
        throw problemError(response.status, error, 'The notification could not be saved.');
      }

      return data;
    },
    onSuccess: () => invalidateNotifications(queryClient),
  });
}

/** Deletes a notification. */
export function useDeleteNotification(): UseMutationResult<void, Error, number> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: number): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/notification/{id}', {
        params: { path: { id } },
      });

      if (!response.ok) {
        throw problemError(response.status, error, 'The notification could not be deleted.');
      }
    },
    onSuccess: () => invalidateNotifications(queryClient),
  });
}

/**
 * Sends a test message through the draft, so the user can check an endpoint without waiting for
 * something to happen. A failed send comes back as a validation problem naming `settings`, which the
 * modal renders as its error.
 */
export function useTestNotification(): UseMutationResult<void, Error, NotificationInput> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (input: NotificationInput): Promise<void> => {
      const { error, response } = await client.POST('/api/v1/notification/test', { body: body(input) });

      if (!response.ok) {
        throw problemError(response.status, error, 'The test message could not be sent.');
      }
    },
  });
}

/**
 * The request body. The document types the settings as a JSON node, which the generated client reads
 * as `unknown` — the same shape the form holds — and leaves `id` optional, so it is left out rather
 * than sent as null.
 */
function body(input: NotificationInput): components['schemas']['NotificationInputResource'] {
  return {
    name: input.name,
    implementation: input.implementation,
    enabled: input.enabled,
    events: input.events,
    settings: input.settings,
    ...(input.id === undefined ? {} : { id: input.id }),
  };
}
