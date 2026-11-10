import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { ApiProvider } from './ApiProvider';
import { useMatchQueueTotal } from './references';

function wrapper({ children }: { children: ReactNode }): ReactNode {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <ApiProvider config={createTestConfig()}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </ApiProvider>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useMatchQueueTotal', () => {
  it('reads a total sent as a string', async () => {
    installFetch(() => jsonResponse({ page: 1, pageSize: 1, totalRecords: '7', records: [] }));

    const { result } = renderHook(() => useMatchQueueTotal(), { wrapper });

    await waitFor(() => expect(result.current).toBe(7));
  });

  // React Query 5.105.2 sets an observer's options before it subscribes. A cached NaN then
  // differs from itself on every render, and the header looped until React gave up.
  it('stays unknown, and settles, when the response has no total', async () => {
    let fetches = 0;
    installFetch(() => {
      fetches += 1;

      return jsonResponse([]);
    });

    let renders = 0;
    const { result } = renderHook(
      () => {
        renders += 1;

        return useMatchQueueTotal();
      },
      { wrapper },
    );

    await waitFor(() => expect(fetches).toBe(1));
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(result.current).toBeNull();
    expect(renders).toBeLessThan(10);
  });
});
