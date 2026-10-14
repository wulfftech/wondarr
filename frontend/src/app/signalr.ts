import { HubConnectionBuilder, type HubConnection, type IRetryPolicy } from '@microsoft/signalr';
import type { QueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useState } from 'react';
import type { AppConfig } from '../api/bootstrap';
import { HEALTH_QUERY_KEY, TASKS_QUERY_KEY } from '../api/hooks';
import { QUEUE_QUERY_KEY } from '../api/queue';
import { MATCH_QUEUE_QUERY_KEY } from '../api/references';
import { COMMANDS_QUERY_KEY, invalidateLibrary, isFinished, SONGS_QUERY_KEY } from '../api/songs';
import { asHealthEntries } from '../api/types';
import { UPDATE_QUERY_KEY } from '../api/update';
import { HISTORY_QUERY_KEY, WANTED_QUERY_KEY } from '../api/wanted';

/** The hub the API broadcasts on. Guarded by the `SignalR` policy, hence the API key. */
const HUB_PATH = 'signalr/events';

/** The event name the server sends every message under. */
const RECEIVE_MESSAGE = 'receiveMessage';

/** The longest wait between two connection attempts. */
const MAX_RETRY_DELAY_MS = 30_000;

/** The wait before the first retry; each one after it doubles, up to the cap. */
const BASE_RETRY_DELAY_MS = 1_000;

/** A broadcast from the API host: `SignalRMessage` is `{ name, body: { action, resource } }`. */
interface EventMessage {
  name: string;
  body: { action: string; resource: unknown };
}

function isEventMessage(value: unknown): value is EventMessage {
  if (typeof value !== 'object' || value === null || typeof (value as { name?: unknown }).name !== 'string') {
    return false;
  }

  const body = (value as { body?: unknown }).body;

  return typeof body === 'object' && body !== null && 'resource' in body;
}

/**
 * How long to wait before reconnect attempt number `attempt` (0-based): exponential from one second,
 * capped at 30 s, plus up to a fifth of that again as jitter so a crowd of tabs does not retry in step.
 */
export function reconnectDelay(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(MAX_RETRY_DELAY_MS, BASE_RETRY_DELAY_MS * 2 ** Math.min(attempt, 30));

  return Math.round(base + base * 0.2 * random());
}

/** A retry policy that never gives up: a restart of any length is waited out. */
export const neverGiveUpRetryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds: (context) => reconnectDelay(context.previousRetryCount),
};

/** The absolute hub URL: the page origin plus the URL base, so it follows the base. */
export function eventStreamUrl(config: AppConfig): string {
  return new URL(`${config.urlBase.replace(/\/+$/, '')}/${HUB_PATH}`, window.location.origin).toString();
}

/** The commands whose end changes songs or their files; the lists they feed are stale once one finishes. */
const SONG_CHANGING_COMMANDS: ReadonlySet<string> = new Set([
  'MoveSongs',
  'ConvertFiles',
  'RefileSong',
  'CompactLibrary',
  'ApplyReplayGain',
  'AddAlbum',
  'BulkAddSongs',
  'ImportListSync',
  'ReferenceAdopt',
]);

/** The commands that put files on (or take them off) the Match queue. */
const MATCH_COMMANDS: ReadonlySet<string> = new Set(['ReferenceLibraryScan', 'ReferenceAdopt']);

/**
 * What a command push changes beyond the command lists. Progress ticks change nothing else; the
 * song lists and the Match queue are only stale once a command that touches them has ended. This
 * also covers a dialog that was closed before its command finished (its own polling is gone).
 */
function applyCommandOutcome(queryClient: QueryClient, resource: unknown): void {
  if (typeof resource !== 'object' || resource === null) {
    return;
  }

  // `name` is the queued name (`MoveSongs`); `commandName` is only its display form (`Move Songs`).
  const command = resource as { name?: unknown; status?: unknown };
  const status = typeof command.status === 'string' ? command.status.toLowerCase() : '';

  if (!isFinished(status)) {
    return;
  }

  const name = typeof command.name === 'string' ? command.name : '';

  if (SONG_CHANGING_COMMANDS.has(name)) {
    invalidateLibrary(queryClient);
  }

  // The header badge only needs the total; the open Match page is refetched only by a command that fills it.
  void queryClient.invalidateQueries({
    queryKey: MATCH_COMMANDS.has(name) ? MATCH_QUEUE_QUERY_KEY : [...MATCH_QUEUE_QUERY_KEY, 'total'],
  });
}

/**
 * Folds one server push into the query cache: a `health` message becomes the `['health']` data, a
 * `command` message invalidates `['tasks']`, an `update` message invalidates the update status, a `queue` message invalidates the queue list and its
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
    try {
      queryClient.setQueryData(HEALTH_QUERY_KEY, asHealthEntries(message.body.resource));
    } catch (error) {
      // A push this client cannot read must not take the stream down; the poll catches up.
      console.error('Ignoring a malformed health push.', error);
    }
  } else if (message.name === 'command') {
    // `['commands']` covers the single command a page polls and the Tasks page's history.
    void queryClient.invalidateQueries({ queryKey: TASKS_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: COMMANDS_QUERY_KEY });
    applyCommandOutcome(queryClient, message.body.resource);
  } else if (message.name === 'queue') {
    // One key covers the list and the status: both hang off `['queue']`.
    void queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY });
  } else if (message.name === 'update') {
    // A check found something (or failed): the badge and the Updates page read the status again.
    void queryClient.invalidateQueries({ queryKey: UPDATE_QUERY_KEY });
  } else if (message.name === 'song') {
    void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: WANTED_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: HISTORY_QUERY_KEY });
  }
}

/** Called with `false` while the stream is down and `true` once it is up again. */
type StatusListener = (connected: boolean) => void;

/** What {@link connectEventStream} hands back: the hub, and a way to shut it down for good. */
export interface EventStream {
  connection: HubConnection;
  /** Stops the connection and any pending retry. */
  stop: () => Promise<void>;
}

/**
 * Opens the event stream and folds every push into the query cache (see {@link applyEventMessage}).
 *
 * It keeps trying for as long as it is open: the first `start()` is retried with the same back-off as
 * a dropped connection, and every successful (re)connect invalidates all queries, since pushes
 * missed in the gap are gone.
 */
export function connectEventStream(
  config: AppConfig,
  queryClient: QueryClient,
  onStatus: StatusListener = () => undefined,
): EventStream {
  const connection = new HubConnectionBuilder()
    .withUrl(eventStreamUrl(config), { accessTokenFactory: () => config.apiKey })
    .withAutomaticReconnect(neverGiveUpRetryPolicy)
    .build();

  let stopped = false;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;

  connection.on(RECEIVE_MESSAGE, (message: unknown) => applyEventMessage(queryClient, message));

  const catchUp = () => {
    onStatus(true);
    void queryClient.invalidateQueries();
  };

  connection.onreconnecting(() => onStatus(false));
  connection.onreconnected(catchUp);
  // Only reached if automatic reconnect ends (it never gives up, so in practice a stop): start over.
  connection.onclose(() => {
    if (!stopped) {
      onStatus(false);
      void startWithRetry(0, true);
    }
  });

  /** `resync`: the page has been live before, so a start that works must also catch up on the gap. */
  async function startWithRetry(failures: number, resync = false): Promise<void> {
    if (stopped) {
      return;
    }

    try {
      await connection.start();
    } catch {
      if (stopped) {
        return;
      }

      onStatus(false);
      retryTimer = setTimeout(() => void startWithRetry(failures + 1, resync), reconnectDelay(failures));

      return;
    }

    // A start that needed a retry, or that follows a closed connection, missed what was pushed
    // meanwhile; the page-load start that works first time did not.
    if (failures > 0 || resync) {
      catchUp();
    }
  }

  void startWithRetry(0);

  return {
    connection,
    stop: () => {
      stopped = true;
      clearTimeout(retryTimer);

      return connection.stop().catch(() => undefined);
    },
  };
}

/** Whether the live-update stream is up. `true` outside the provider, so nothing shows then. */
export const EventStreamStatusContext = createContext(true);

/** Whether live updates are currently connected, for the header's indicator. */
export function useEventStreamConnected(): boolean {
  return useContext(EventStreamStatusContext);
}

/** Keeps one event-stream connection alive for the lifetime of the component; returns whether it is up. */
export function useEventStream(config: AppConfig, queryClient: QueryClient): boolean {
  const [connected, setConnected] = useState(true);

  useEffect(() => {
    const stream = connectEventStream(config, queryClient, setConnected);

    return () => {
      void stream.stop();
    };
  }, [config, queryClient]);

  return connected;
}
