import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { AppConfig } from '../api/bootstrap';
import { HEALTH_QUERY_KEY, TASKS_QUERY_KEY } from '../api/hooks';
import { asHealthEntries } from '../api/types';

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
 * Opens the event stream and folds server pushes into the query cache: a `health` message becomes
 * the `['health']` data, a `command` message invalidates `['tasks']`.
 */
export function connectEventStream(config: AppConfig, queryClient: QueryClient): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl(eventStreamUrl(config), { accessTokenFactory: () => config.apiKey })
    .withAutomaticReconnect()
    .build();

  connection.on(RECEIVE_MESSAGE, (message: unknown) => {
    if (!isEventMessage(message)) {
      return;
    }

    if (message.name === 'health') {
      queryClient.setQueryData(HEALTH_QUERY_KEY, asHealthEntries(message.body));
    } else if (message.name === 'command') {
      void queryClient.invalidateQueries({ queryKey: TASKS_QUERY_KEY });
    }
  });

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
