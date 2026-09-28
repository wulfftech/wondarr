import { ApiError } from './errors';

/** What `initialize.json` hands the SPA (see `InitializeController` in the API host). */
export interface AppConfig {
  /** The API root, including the URL base, e.g. `/compilarr/api/v1`. */
  apiRoot: string;
  /** The API key. Never log or render this. */
  apiKey: string;
  /** The configured URL base, or an empty string for the site root. */
  urlBase: string;
  /** The display name of this instance. */
  instanceName: string;
  /** The running build's informational version. */
  version: string;
}

/** The relative URL of the bootstrap payload: relative, so it follows the document's URL base. */
export const INITIALIZE_PATH = 'initialize.json';

/** The relative URL of the server-rendered login page. */
export const LOGIN_PATH = 'login';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function readString(source: Record<string, unknown>, key: string): string {
  const value = source[key];

  if (typeof value !== 'string') {
    throw new TypeError(`initialize.json is missing "${key}".`);
  }

  return value;
}

/** Sends the browser to the login page, keeping the URL base, then reports that it did. */
export function redirectToLogin(): void {
  window.location.assign(new URL(LOGIN_PATH, document.baseURI).toString());
}

/**
 * Fetches and validates `initialize.json`.
 *
 * The payload is guarded by the UI policy, so a session that has expired comes back as a 401 or as
 * a redirect to the login page; both send the browser to `login` rather than rendering a broken
 * shell.
 *
 * @throws {ApiError} when the payload is missing or the request failed.
 */
export async function loadAppConfig(fetchImpl: typeof fetch = fetch): Promise<AppConfig> {
  const response = await fetchImpl(new URL(INITIALIZE_PATH, document.baseURI).toString(), {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
  });

  if (response.status === 401 || (response.redirected && isLoginUrl(response.url))) {
    redirectToLogin();
    throw new ApiError(response.status, 'Signing in is required.');
  }

  if (!response.ok) {
    throw new ApiError(response.status, 'The web UI could not load its configuration.');
  }

  const payload: unknown = await response.json();

  if (!isRecord(payload)) {
    throw new TypeError('initialize.json is not an object.');
  }

  return {
    apiRoot: readString(payload, 'apiRoot'),
    apiKey: readString(payload, 'apiKey'),
    urlBase: readString(payload, 'urlBase'),
    instanceName: readString(payload, 'instanceName'),
    version: readString(payload, 'version'),
  };
}

function isLoginUrl(url: string): boolean {
  try {
    return new URL(url).pathname.replace(/\/+$/, '').endsWith(`/${LOGIN_PATH}`);
  } catch {
    return false;
  }
}
