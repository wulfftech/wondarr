import createClient, { type Middleware } from 'openapi-fetch';
import type { AppConfig } from './bootstrap';
import { ApiError } from './errors';
import type { paths } from './schema';

/** The openapi-fetch client, typed from the generated schema. */
export type ApiClient = ReturnType<typeof createClient<paths>>;

/** The absolute origin every API URL is built from: the page's origin plus the URL base. */
export function apiOrigin(config: AppConfig): string {
  return new URL(config.urlBase || '/', window.location.origin).toString().replace(/\/+$/, '');
}

/** Adds the API key the *arr convention expects to every request. */
function apiKeyMiddleware(config: AppConfig): Middleware {
  return {
    onRequest({ request }) {
      request.headers.set('X-Api-Key', config.apiKey);

      return request;
    },
  };
}

/**
 * Creates the client every hook uses. The base URL is the origin plus the URL base, so the same
 * build works at the site root and behind any URL base.
 */
export function createApiClient(config: AppConfig): ApiClient {
  const client = createClient<paths>({ baseUrl: apiOrigin(config) });
  client.use(apiKeyMiddleware(config));

  return client;
}

/**
 * Calls an API path that the committed `openapi.json` does not describe, using the same origin,
 * URL base and `X-Api-Key` header as the generated client.
 *
 * This exists only for the endpoints listed in `types.ts`; move each one onto the generated client
 * as soon as the API publishes it.
 */
export async function apiRequest<T>(config: AppConfig, path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiOrigin(config)}/${path.replace(/^\/+/, '')}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      'X-Api-Key': config.apiKey,
      ...init?.headers,
    },
    credentials: 'same-origin',
  });

  if (!response.ok) {
    throw new ApiError(response.status, `The request to ${path} failed.`);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const payload: unknown = await response.json();

  return payload as T;
}
