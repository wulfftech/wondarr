import type { IRetryPolicy } from '@microsoft/signalr';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createTestConfig } from '../test/helpers';
import { applyEventMessage, connectEventStream, neverGiveUpRetryPolicy, reconnectDelay } from './signalr';

/**
 * What a server push does to the query cache, and how the connection behaves when the server goes
 * away. The messages are the ones the API broadcasts (`SignalRMessage`); no socket is opened here.
 */

/** A stand-in hub: records the handlers the code registers, and lets a test fail or pass each start. */
const hub = vi.hoisted(() => ({
  start: vi.fn<() => Promise<void>>(),
  stop: vi.fn<() => Promise<void>>(),
  reconnecting: undefined as (() => void) | undefined,
  reconnected: undefined as (() => void) | undefined,
  closed: undefined as (() => void) | undefined,
  retryPolicy: null as IRetryPolicy | null,
}));

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl(): this {
      return this;
    }

    withAutomaticReconnect(policy: IRetryPolicy): this {
      hub.retryPolicy = policy;

      return this;
    }

    build() {
      return {
        on: () => undefined,
        onreconnecting: (handler: () => void) => {
          hub.reconnecting = handler;
        },
        onreconnected: (handler: () => void) => {
          hub.reconnected = handler;
        },
        onclose: (handler: () => void) => {
          hub.closed = handler;
        },
        start: () => hub.start(),
        stop: () => hub.stop(),
      };
    }
  }

  return { HubConnectionBuilder };
});

/** A server push, built the way `SignalRMessage` is: `{ name, body: { action, resource } }`. */
function serverMessage(name: string, action: string, resource: unknown): unknown {
  return { name, body: { action, resource } };
}

/** A command push as `CommandResource` serialises it: `name` as queued, `commandName` as display text. */
function commandPush(id: number, name: string, status: string): unknown {
  return serverMessage('command', 'updated', {
    id,
    name,
    commandName: name.replace(/([a-z])([A-Z])/g, '$1 $2'),
    status,
  });
}

/** A cache holding one entry for each list the messages touch. */
function cacheWithEntries(): QueryClient {
  const queryClient = new QueryClient();

  queryClient.setQueryData(['queue', 'list', { page: 1 }, false], { records: [] });
  queryClient.setQueryData(['queue', 'status'], { count: 0 });
  queryClient.setQueryData(['songs', { page: 1 }], { records: [] });
  queryClient.setQueryData(['wanted', 'missing', { page: 1 }], { records: [] });
  queryClient.setQueryData(['history', { page: 1 }], { records: [] });
  queryClient.setQueryData(['blocklist', { page: 1 }], { records: [] });

  return queryClient;
}

/** Whether the entry under this key is stale and due a refetch. */
function isInvalidated(queryClient: QueryClient, key: unknown[]): boolean {
  return queryClient.getQueryState(key)?.isInvalidated === true;
}

describe('applyEventMessage', () => {
  it('invalidates the queue list and its status when the queue changes', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(
      queryClient,
      serverMessage('queue', 'updated', { id: 31, songId: 1, state: 'downloading', progress: 0.42, message: null }),
    );

    expect(isInvalidated(queryClient, ['queue', 'list', { page: 1 }, false])).toBe(true);
    expect(isInvalidated(queryClient, ['queue', 'status'])).toBe(true);
    // Nothing else moves: a download in flight does not change the library.
    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });

  it('invalidates the songs, wanted and history lists when a song is imported', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, serverMessage('song', 'updated', { id: 1 }));

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['wanted', 'missing', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['history', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['blocklist', { page: 1 }])).toBe(false);
  });

  it('invalidates the update status when a check finishes', () => {
    const queryClient = cacheWithEntries();
    queryClient.setQueryData(['update'], { currentVersion: '0.1.0', updateAvailable: false });

    applyEventMessage(
      queryClient,
      serverMessage('update', 'updated', { latestVersion: '0.2.0', updateAvailable: true }),
    );

    expect(isInvalidated(queryClient, ['update'])).toBe(true);
    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });

  it('ignores a message it does not know and one that is not a message at all', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, serverMessage('somethingElse', 'updated', {}));
    applyEventMessage(queryClient, 'not a message');

    expect(isInvalidated(queryClient, ['queue', 'status'])).toBe(false);
    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });

  it('puts a health push, read from body.resource, into the health query', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(
      queryClient,
      serverMessage('health', 'sync', [
        { source: 'Slskd', type: 'error', message: 'slskd is down', wikiUrl: null },
        { source: 'Disk', type: 'ok', message: 'Fine', wikiUrl: null },
      ]),
    );

    expect(queryClient.getQueryData(['health'])).toEqual([
      { source: 'Slskd', type: 'error', message: 'slskd is down', wikiUrl: null },
      { source: 'Disk', type: 'ok', message: 'Fine', wikiUrl: null },
    ]);
  });

  it('logs a malformed health push instead of throwing, and leaves the cached health alone', () => {
    const queryClient = cacheWithEntries();
    queryClient.setQueryData(['health'], []);
    const log = vi.spyOn(console, 'error').mockImplementation(() => undefined);

    expect(() => applyEventMessage(queryClient, serverMessage('health', 'sync', { not: 'a list' }))).not.toThrow();

    expect(log).toHaveBeenCalled();
    expect(queryClient.getQueryData(['health'])).toEqual([]);
    log.mockRestore();
  });

  it('refreshes the Match badge total, and not the Match page, when a command ends', () => {
    const queryClient = cacheWithEntries();
    queryClient.setQueryData(['match-queue', 'total'], 3);
    queryClient.setQueryData(['match-queue', 'list', { page: 1 }], { records: [] });

    applyEventMessage(queryClient, commandPush(5, 'Backup', 'completed'));

    expect(isInvalidated(queryClient, ['match-queue', 'total'])).toBe(true);
    expect(isInvalidated(queryClient, ['match-queue', 'list', { page: 1 }])).toBe(false);
  });

  it('refreshes the whole Match queue when a reference scan ends, but not on its progress', () => {
    const queryClient = cacheWithEntries();
    queryClient.setQueryData(['match-queue', 'total'], 3);
    queryClient.setQueryData(['match-queue', 'list', { page: 1 }], { records: [] });

    applyEventMessage(queryClient, commandPush(6, 'ReferenceLibraryScan', 'started'));

    expect(isInvalidated(queryClient, ['match-queue', 'total'])).toBe(false);
    expect(isInvalidated(queryClient, ['match-queue', 'list', { page: 1 }])).toBe(false);

    applyEventMessage(queryClient, commandPush(6, 'ReferenceLibraryScan', 'completed'));

    expect(isInvalidated(queryClient, ['match-queue', 'total'])).toBe(true);
    expect(isInvalidated(queryClient, ['match-queue', 'list', { page: 1 }])).toBe(true);
  });

  it('refreshes the song lists when a song-changing command ends, not while it runs', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, commandPush(7, 'MoveSongs', 'started'));

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);

    applyEventMessage(queryClient, commandPush(7, 'MoveSongs', 'completed'));

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['wanted', 'missing', { page: 1 }])).toBe(true);
  });

  it('matches a song-changing command by its queued name, not its display name', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(
      queryClient,
      serverMessage('command', 'updated', { id: 9, name: 'MoveSongs', commandName: 'Move Songs', status: 'completed' }),
    );

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(true);
  });

  it('leaves the song lists alone when a command that does not change songs ends', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, commandPush(8, 'Backup', 'completed'));

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });
});

describe('reconnectDelay', () => {
  it('backs off to a cap of about 30 seconds and keeps returning delays past any attempt count', () => {
    const delays = Array.from({ length: 40 }, (_, attempt) => reconnectDelay(attempt, () => 0));

    expect(delays[0]).toBe(1_000);
    expect(delays[1]).toBe(2_000);
    expect(Math.max(...delays)).toBe(30_000);
    expect(delays.at(-1)).toBe(30_000);
    // Jitter adds at most a fifth.
    expect(reconnectDelay(39, () => 1)).toBe(36_000);
  });

  it('is the policy the connection is built with, and never returns null', () => {
    for (const previousRetryCount of [0, 4, 5, 6, 50, 5_000]) {
      const delay = neverGiveUpRetryPolicy.nextRetryDelayInMilliseconds({
        previousRetryCount,
        elapsedMilliseconds: previousRetryCount * 30_000,
        retryReason: new Error('down'),
      });

      expect(delay).toBeGreaterThan(0);
    }
  });
});

describe('connectEventStream', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    hub.start.mockReset();
    hub.stop.mockReset().mockResolvedValue(undefined);
    hub.reconnecting = undefined;
    hub.reconnected = undefined;
    hub.closed = undefined;
    hub.retryPolicy = null;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('builds the connection with the never-give-up policy', async () => {
    hub.start.mockResolvedValue(undefined);

    const stream = connectEventStream(createTestConfig(), new QueryClient());

    expect(hub.retryPolicy).toBe(neverGiveUpRetryPolicy);
    await stream.stop();
  });

  it('retries a first start that fails, reports the gap, and catches up once it connects', async () => {
    hub.start.mockRejectedValueOnce(new Error('refused')).mockRejectedValueOnce(new Error('refused'));
    hub.start.mockResolvedValue(undefined);
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const status = vi.fn();

    const stream = connectEventStream(createTestConfig(), queryClient, status);
    await vi.advanceTimersByTimeAsync(0);

    expect(hub.start).toHaveBeenCalledTimes(1);
    expect(status).toHaveBeenLastCalledWith(false);

    await vi.advanceTimersByTimeAsync(40_000);

    expect(hub.start).toHaveBeenCalledTimes(3);
    expect(status).toHaveBeenLastCalledWith(true);
    expect(invalidate).toHaveBeenCalledWith();

    await stream.stop();
  });

  it('does not keep retrying once stopped', async () => {
    hub.start.mockRejectedValue(new Error('refused'));

    const stream = connectEventStream(createTestConfig(), new QueryClient());
    await vi.advanceTimersByTimeAsync(0);
    await stream.stop();
    await vi.advanceTimersByTimeAsync(120_000);

    expect(hub.start).toHaveBeenCalledTimes(1);
  });

  it('invalidates every query when the connection comes back, and flags the gap meanwhile', async () => {
    hub.start.mockResolvedValue(undefined);
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const status = vi.fn();

    const stream = connectEventStream(createTestConfig(), queryClient, status);
    await vi.advanceTimersByTimeAsync(0);

    hub.reconnecting?.();
    expect(status).toHaveBeenLastCalledWith(false);
    expect(invalidate).not.toHaveBeenCalled();

    hub.reconnected?.();
    expect(status).toHaveBeenLastCalledWith(true);
    expect(invalidate).toHaveBeenCalledWith();

    await stream.stop();
  });

  it('catches up after the connection closes and the restart works first time', async () => {
    hub.start.mockResolvedValue(undefined);
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const status = vi.fn();

    const stream = connectEventStream(createTestConfig(), queryClient, status);
    await vi.advanceTimersByTimeAsync(0);
    // The page-load start worked first time: nothing to catch up on.
    expect(invalidate).not.toHaveBeenCalled();

    hub.closed?.();
    expect(status).toHaveBeenLastCalledWith(false);
    await vi.advanceTimersByTimeAsync(0);

    expect(hub.start).toHaveBeenCalledTimes(2);
    expect(status).toHaveBeenLastCalledWith(true);
    expect(invalidate).toHaveBeenCalledWith();

    await stream.stop();
  });
});
