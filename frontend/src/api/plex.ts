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
 * The Plex connection: the PIN sign-in, the account's servers, the selected one and its music
 * sections. No response of any of these carries a token, and none of these hooks ever puts one in a
 * cache: `useSetPlexToken` hands it to the API and forgets it.
 */

export type PlexStateResource = components['schemas']['PlexStateResource'];
export type PlexPinResource = components['schemas']['PlexPinResource'];
export type PlexPinStatusResource = components['schemas']['PlexPinStatusResource'];
export type PlexServerResource = components['schemas']['PlexServerResource'];
export type PlexConnectionResource = components['schemas']['PlexConnectionResource'];
export type PlexSectionResource = components['schemas']['PlexSectionResource'];
export type PlexTestResource = components['schemas']['PlexTestResource'];

export const PLEX_STATE_QUERY_KEY = ['plex', 'state'] as const;
export const PLEX_SERVERS_QUERY_KEY = ['plex', 'servers'] as const;
export const PLEX_SECTIONS_QUERY_KEY = ['plex', 'sections'] as const;

/** The key one sign-in PIN is polled under; it is separate so a retry starts from a clean entry. */
export function plexPinQueryKey(id: number | string | null): readonly unknown[] {
  return ['plex', 'pin', id === null ? null : Number(id)];
}

/**
 * How long the UI waits between sign-in polls, in milliseconds. Plex's own guidance is about once a
 * second (research §5.9); at 2 s the code is still approved promptly without hammering plex.tv.
 */
export const PLEX_PIN_POLL_MS = 2000;

/** Reads a problem member the API may have left out. */
function problemText(body: unknown, member: 'detail' | 'title'): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const value = (body as Record<string, unknown>)[member];

  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * The error a failed Plex call throws: an `ApiError` carrying the problem's `detail` — the server's
 * own words, which the controller guarantees hold no token — and its `title` as the fallback.
 */
function plexProblem(status: number, body: unknown, fallback: string): ApiError {
  return new ApiError(status, problemText(body, 'detail') ?? problemText(body, 'title') ?? fallback);
}

/** What the UI shows about the connection: whether there is a sign-in, and which server it reaches. */
export function usePlexState(): UseQueryResult<PlexStateResource, Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: PLEX_STATE_QUERY_KEY,
    queryFn: async (): Promise<PlexStateResource> => {
      const { data, error, response } = await client.GET('/api/v1/plex');

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex connection could not be read.');
      }

      return data;
    },
  });
}

/** Starts a sign-in: the PIN the user approves on plex.tv, and the page that carries the code there. */
export function useCreatePlexPin(): UseMutationResult<PlexPinResource, Error, void> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (): Promise<PlexPinResource> => {
      const { data, error, response } = await client.POST('/api/v1/plex/pin');

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'A Plex sign-in code could not be created.');
      }

      return data;
    },
  });
}

/**
 * Polls one sign-in PIN while `enabled`, every {@link PLEX_PIN_POLL_MS}, and stops itself as soon as
 * plex.tv says the code was approved or has expired. The poll that approves it also refreshes the
 * connection state, because that is the moment the install became signed in.
 */
export function usePlexPinStatus(
  id: number | string | null,
  enabled: boolean,
): UseQueryResult<PlexPinStatusResource, Error> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useQuery({
    queryKey: plexPinQueryKey(id),
    enabled: enabled && id !== null,
    queryFn: async (): Promise<PlexPinStatusResource> => {
      const { data, error, response } = await client.GET('/api/v1/plex/pin/{id}', {
        // The hook is disabled without an id, so the path never carries this placeholder.
        params: { path: { id: Number(id ?? 0) } },
      });

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex sign-in code could not be checked.');
      }

      if (data.authorized) {
        await queryClient.invalidateQueries({ queryKey: PLEX_STATE_QUERY_KEY });
      }

      return data;
    },
    refetchInterval: (query) => {
      const status = query.state.data;

      // Both answers are final: an approved PIN is spent, and an expired one cannot be approved.
      return status !== undefined && (status.authorized || status.expired) ? false : PLEX_PIN_POLL_MS;
    },
  });
}

/**
 * Stores a token the user pasted, so an install without a browser can sign in. The token is passed
 * straight through: it is not cached, echoed back or kept anywhere the UI can read it again.
 */
export function useSetPlexToken(): UseMutationResult<void, Error, string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (token: string): Promise<void> => {
      const { error, response } = await client.PUT('/api/v1/plex/token', { body: { token } });

      if (!response.ok) {
        throw plexProblem(response.status, error, 'The Plex token could not be saved.');
      }
    },
    onSuccess: () => {
      // A new sign-in is a new account, so the servers and the sections may all be different.
      void queryClient.invalidateQueries({ queryKey: PLEX_STATE_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: PLEX_SERVERS_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: PLEX_SECTIONS_QUERY_KEY });
    },
  });
}

/** The Plex Media Servers the signed-in account can reach, and every way to reach each one. */
export function usePlexServers(enabled: boolean): UseQueryResult<PlexServerResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: PLEX_SERVERS_QUERY_KEY,
    enabled,
    queryFn: async (): Promise<PlexServerResource[]> => {
      const { data, error, response } = await client.GET('/api/v1/plex/servers');

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex servers could not be listed.');
      }

      return data;
    },
  });
}

/** Selects the server Wondarr talks to and confirms it is reachable. */
export function useSelectPlexServer(): UseMutationResult<PlexStateResource, Error, string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (serverUrl: string): Promise<PlexStateResource> => {
      const { data, error, response } = await client.PUT('/api/v1/plex/server', {
        body: { serverUrl },
      });

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex server could not be selected.');
      }

      return data;
    },
    onSuccess: (state) => {
      // The write already answered with the new state, so the page shows the chosen server at once;
      // the sections belong to the server that is now selected.
      queryClient.setQueryData(PLEX_STATE_QUERY_KEY, state);
      void queryClient.invalidateQueries({ queryKey: PLEX_SECTIONS_QUERY_KEY });
    },
  });
}

/**
 * `PUT /api/v1/plex/server/connect`: selects one of the account's servers by its machine
 * identifier. The server tries every connection plex.tv lists and keeps the best one that answers,
 * which is what the user means by "connect to this server".
 */
export function useConnectPlexServer(): UseMutationResult<PlexStateResource, Error, string> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (machineIdentifier: string): Promise<PlexStateResource> => {
      const { data, error, response } = await client.PUT('/api/v1/plex/server/connect', {
        body: { machineIdentifier },
      });

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'Wondarr could not connect to the Plex server.');
      }

      return data;
    },
    onSuccess: (state) => {
      queryClient.setQueryData(PLEX_STATE_QUERY_KEY, state);
      void queryClient.invalidateQueries({ queryKey: PLEX_SECTIONS_QUERY_KEY });
    },
  });
}

/**
 * Tests the selected server. Whether the connection works is the answer rather than a status code,
 * so a refusal arrives here as `ok: false` with an error in words that carry no token.
 */
export function useTestPlex(): UseMutationResult<PlexTestResource, Error, void> {
  const client = useApiClient();

  return useMutation({
    mutationFn: async (): Promise<PlexTestResource> => {
      const { data, error, response } = await client.POST('/api/v1/plex/test');

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex server could not be tested.');
      }

      return data;
    },
  });
}

/** The music sections of the selected server, which a library is linked to. */
export function usePlexSections(enabled: boolean): UseQueryResult<PlexSectionResource[], Error> {
  const client = useApiClient();

  return useQuery({
    queryKey: PLEX_SECTIONS_QUERY_KEY,
    enabled,
    queryFn: async (): Promise<PlexSectionResource[]> => {
      const { data, error, response } = await client.GET('/api/v1/plex/sections');

      if (!response.ok || data === undefined) {
        throw plexProblem(response.status, error, 'The Plex music sections could not be listed.');
      }

      return data;
    },
  });
}

/** Signs out. The selected server is kept, so the page can still show what was chosen. */
export function useSignOutPlex(): UseMutationResult<void, Error, void> {
  const client = useApiClient();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<void> => {
      const { error, response } = await client.DELETE('/api/v1/plex');

      if (!response.ok) {
        throw plexProblem(response.status, error, 'The Plex sign-in could not be cleared.');
      }
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: PLEX_STATE_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: PLEX_SERVERS_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: PLEX_SECTIONS_QUERY_KEY });
    },
  });
}
