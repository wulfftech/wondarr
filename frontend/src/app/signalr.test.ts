import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { applyEventMessage } from './signalr';

/**
 * What a server push does to the query cache. The messages are the ones the API broadcasts
 * (`QueueEventsRelay`); the connection itself is not opened here.
 */

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

    applyEventMessage(queryClient, {
      name: 'queue',
      body: { id: 31, songId: 1, state: 'downloading', progress: 0.42, message: null },
    });

    expect(isInvalidated(queryClient, ['queue', 'list', { page: 1 }, false])).toBe(true);
    expect(isInvalidated(queryClient, ['queue', 'status'])).toBe(true);
    // Nothing else moves: a download in flight does not change the library.
    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });

  it('invalidates the songs, wanted and history lists when a song is imported', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, { name: 'song', body: { id: 1 } });

    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['wanted', 'missing', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['history', { page: 1 }])).toBe(true);
    expect(isInvalidated(queryClient, ['blocklist', { page: 1 }])).toBe(false);
  });

  it('ignores a message it does not know and one that is not a message at all', () => {
    const queryClient = cacheWithEntries();

    applyEventMessage(queryClient, { name: 'somethingElse', body: {} });
    applyEventMessage(queryClient, 'not a message');

    expect(isInvalidated(queryClient, ['queue', 'status'])).toBe(false);
    expect(isInvalidated(queryClient, ['songs', { page: 1 }])).toBe(false);
  });
});
