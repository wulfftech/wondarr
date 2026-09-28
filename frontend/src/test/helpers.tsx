import { render, type RenderResult } from '@testing-library/react';
import { vi } from 'vitest';
import type { AppConfig } from '../api/bootstrap';
import { App } from '../app/App';

/** Helpers shared by the UI tests. Every test mocks `fetch`; none of them touch the network. */

export function createTestConfig(overrides: Partial<AppConfig> = {}): AppConfig {
  return {
    apiRoot: '/api/v1',
    // Not a real key: it only ever reaches a mocked fetch.
    apiKey: 'test-api-key',
    urlBase: '',
    instanceName: 'Compilarr',
    version: '0.1.0-test',
    ...overrides,
  };
}

export type FetchHandler = (url: string, init?: RequestInit) => Response | Promise<Response>;

/** The recorded calls, so a test can assert what the UI actually sent. */
export interface FetchMock {
  calls: { url: string; init: RequestInit | undefined }[];
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }

  return input instanceof URL ? input.toString() : input.url;
}

/** Replaces `fetch` with a route table, recording every call. */
export function installFetch(handler: FetchHandler): FetchMock {
  const mock: FetchMock = { calls: [] };

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      // openapi-fetch passes a Request and no init: the method and headers live on the Request, so
      // surface them where tests (and handlers) look for them. Bodies stay on the Request.
      const effective: RequestInit | undefined =
        init ?? (input instanceof Request ? { method: input.method, headers: input.headers } : undefined);
      mock.calls.push({ url, init: effective });

      return Promise.resolve(handler(url, effective));
    }),
  );

  return mock;
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Puts the browser back at `path` so a test starts from a known route. */
export function resetLocation(path = '/'): void {
  window.history.pushState({}, '', path);
}

export function renderApp(config: AppConfig = createTestConfig()): RenderResult {
  return render(<App config={config} />);
}
