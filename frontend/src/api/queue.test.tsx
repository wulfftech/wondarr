import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { ApiProvider } from './ApiProvider';
import { QUEUE_QUERY_KEY, useGrabRelease, useInteractiveSearch, useRemoveQueueItem } from './queue';

/** A search answer; only its presence matters to these tests. */
const SEARCH_RESULT = { searchRunId: 1, outcome: 'noAcceptableCandidate', message: '', releases: [] };

/** A route table where the search counts its runs, and everything else succeeds. */
function install(): { searches: () => number } {
  let searches = 0;

  installFetch((url, init) => {
    if (url.includes('/api/v1/release') && init?.method !== 'POST') {
      searches += 1;

      return jsonResponse(SEARCH_RESULT);
    }

    return new Response(null, { status: init?.method === 'DELETE' ? 200 : 201 });
  });

  return { searches: () => searches };
}

function setup(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <ApiProvider config={createTestConfig()}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </ApiProvider>
  );

  return { queryClient, wrapper };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useInteractiveSearch', () => {
  it('does not search again when the queue is invalidated', async () => {
    const { searches } = install();
    const { queryClient, wrapper } = setup();
    const { result } = renderHook(() => useInteractiveSearch(1, true), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(searches()).toBe(1);

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: QUEUE_QUERY_KEY });
    });

    expect(searches()).toBe(1);
  });
});

describe('useGrabRelease', () => {
  it('settles without a new search and refreshes the history', async () => {
    const { searches } = install();
    const { queryClient, wrapper } = setup();
    const search = renderHook(() => useInteractiveSearch(1, true), { wrapper });

    await waitFor(() => expect(search.result.current.isSuccess).toBe(true));

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const grab = renderHook(() => useGrabRelease(), { wrapper });

    await act(async () => {
      await grab.result.current.mutateAsync(9001);
    });

    expect(searches()).toBe(1);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['history'] });
  });
});

describe('useRemoveQueueItem', () => {
  it('refreshes the blocklist, history and wanted lists when it blocklisted the item', async () => {
    install();
    const { queryClient, wrapper } = setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const { result } = renderHook(() => useRemoveQueueItem(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({ id: 31, blocklist: true });
    });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['blocklist'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['history'] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['wanted'] });
  });

  it('leaves the blocklist alone when the item was only removed', async () => {
    install();
    const { queryClient, wrapper } = setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const { result } = renderHook(() => useRemoveQueueItem(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({ id: 31 });
    });

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['queue'] });
    expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['blocklist'] });
  });
});
