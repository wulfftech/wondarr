import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { AppConfig } from '../api/bootstrap';
import { HEALTH_QUERY_KEY, TASKS_QUERY_KEY } from '../api/hooks';
import { QUEUE_QUERY_KEY } from '../api/queue';
import { SONGS_QUERY_KEY } from '../api/songs';
import { asHealthEntries } from '../api/types';
import { HISTORY_QUERY_KEY, WANTED_QUERY_KEY } from '../api/wanted';

/** The hub the API broadcasts on. Guarded by the `SignalR` policy, hence the API key. */
const HUB_PATH = 'signalr/events';

/** The event name the server sends every message under. */
const RECEIVE_MESSAGE = 'receiveMessage';

/** A broadcast from the API host. */
interface EventMessage {
  name: string;
  body: unknown;
}

function isEventMessage(value: unknown): value is EventMessage {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { name?: unknown }).name === 'string' &&
    'body' in value
  );
}

/** The absolute hub URL: the page origin plus the URL base, so it follows the base. */
export function eventStreamUrl(config: AppConfig): string {
  return new URL(`${config.urlBase.replace(/\/+$/, '')}/${HUB_PATH}`, window.location.origin).toString();
}

/**
 * Folds one server push into the query cache: a `health` message becomes the `['health']` data, a
 * `command` message invalidates `['tasks']`, a `queue` message invalidates the queue list and its
 * status, and a `song` message invalidates the lists an import changes.
 *
 * Pure, and separate from the connection, so the mapping can be tested without a hub: the push
 * carries one little resource per change, and re-reading the affected lists is cheaper to keep
 * correct than patching each one in place.
 */
export function applyEventMessage(queryClient: QueryClient, message: unknown): void {
  if (!isEventMessage(message)) {
    return;
  }

  if (message.name === 'health') {
    queryClient.setQueryData(HEALTH_QUERY_KEY, asHealthEntries(message.body));
  } else if (message.name === 'command') {
    void queryClient.invalidateQueries({ queryKey: TASKS_QUERY_KEY });
  } else if (message.name === 'queue') {
    // One key covers the list and the status: both hang off `['queue']`.
    void queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY });
  } else if (message.name === 'song') {
    void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: WANTED_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: HISTORY_QUERY_KEY });
  }
}

/** Opens the event stream and folds every push into the query cache (see {@link applyEventMessage}). */
export function connectEventStream(config: AppConfig, queryClient: QueryClient): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl(eventStreamUrl(config), { accessTokenFactory: () => config.apiKey })
    .withAutomaticReconnect()
    .build();

  connection.on(RECEIVE_MESSAGE, (message: unknown) => applyEventMessage(queryClient, message));

  // The hub may not be running yet; live update is an enhancement, not a requirement to render.
  void connection.start().catch(() => undefined);

  return connection;
}

/** Keeps one event-stream connection alive for the lifetime of the component. */
export function useEventStream(config: AppConfig, queryClient: QueryClient): void {
  useEffect(() => {
    const connection = connectEventStream(config, queryClient);

    return () => {
      void connection.stop().catch(() => undefined);
    };
  }, [config, queryClient]);
}
