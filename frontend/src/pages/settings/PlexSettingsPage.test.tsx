import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import {
  HEALTH_ENTRIES,
  PLEX_SERVERS,
  PLEX_STATE_SIGNED_IN,
  PLEX_STATE_SIGNED_OUT,
  PLEX_STATE_SIGNED_IN_UNSELECTED,
  SYSTEM_STATUS,
} from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

/** The PIN `POST /api/v1/plex/pin` answers with. */
const PIN = {
  id: 42,
  code: 'AB12CD',
  authUrl: 'https://app.plex.tv/auth#?clientID=wondarr-2f7c1a9b&code=AB12CD',
  expiresAt: '2026-09-30T12:10:00Z',
};

/** What the connection test reports when the server answers, and when it does not. */
const TEST_OK = { ok: true, serverName: 'Home NAS', version: '1.43.4.1000', musicSections: 2, error: null };
const TEST_FAILED = {
  ok: false,
  serverName: null,
  version: null,
  musicSections: 0,
  error: 'Plex did not answer within 10 s.',
};

/** The routes a test wants to answer differently from the default. */
interface Routes {
  state?: () => Response;
  pinStatus?: () => Response;
  token?: () => Response;
  server?: () => Response;
  test?: () => Response;
  signOut?: () => Response;
}

/**
 * Answers every Plex route the page can ask for. The connection-state route is matched last: it is
 * the prefix of all the others.
 */
function install(routes: Routes = {}): FetchMock {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/plex/pin/')) {
      return routes.pinStatus?.() ?? jsonResponse({ authorized: false, expired: false });
    }

    if (url.includes('/api/v1/plex/pin')) {
      return jsonResponse(PIN, 201);
    }

    if (url.includes('/api/v1/plex/token')) {
      return routes.token?.() ?? new Response(null, { status: 204 });
    }

    if (url.includes('/api/v1/plex/servers')) {
      return jsonResponse(PLEX_SERVERS);
    }

    if (url.includes('/api/v1/plex/server')) {
      return routes.server?.() ?? jsonResponse(PLEX_STATE_SIGNED_IN);
    }

    if (url.includes('/api/v1/plex/test')) {
      return routes.test?.() ?? jsonResponse(TEST_OK);
    }

    if (url.includes('/api/v1/plex/sections')) {
      return jsonResponse([]);
    }

    if (url.endsWith('/api/v1/plex')) {
      if (method === 'DELETE') {
        return routes.signOut?.() ?? new Response(null, { status: 204 });
      }

      return routes.state?.() ?? jsonResponse(PLEX_STATE_SIGNED_OUT);
    }

    return new Response('not found', { status: 404 });
  });
}

/** A radio's own text: Mantine keeps the label beside the input rather than around it. */
function labelOf(radio: HTMLElement | undefined): string {
  return radio?.closest('.mantine-Radio-root')?.textContent ?? '';
}

/** The requests the mocked `fetch` received, with the method and body the generated client sent. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** The `PUT` a test is asking about, parsed once it has arrived. */
async function sentBody(predicate: (request: { url: string; method: string }) => boolean): Promise<unknown> {
  const request = sent().find(predicate);

  return request === undefined ? null : JSON.parse(await request.body);
}

/** The spy on `window.open` every test in this file gets. */
let openTab: MockInstance<Window['open']>;

beforeEach(() => {
  resetLocation('/settings/plex');
  Element.prototype.scrollIntoView = vi.fn();
  // jsdom cannot open a tab, and the sign-in opens the approval page every time it starts.
  openTab = vi.spyOn(window, 'open').mockReturnValue(null);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('PlexSettingsPage sign-in', () => {
  it('creates a PIN, opens the approval page and waits for plex.tv', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Sign in with Plex' }));

    expect(openTab).toHaveBeenCalledWith(PIN.authUrl, '_blank', 'noopener');
    expect(await screen.findByText('AB12CD')).toBeInTheDocument();
    expect(screen.getByText(/Waiting for you to approve Wondarr on plex.tv/)).toBeInTheDocument();

    // The PIN was polled; the interval is the hook's own, so a pending answer just keeps waiting.
    const polls = sent().filter((request) => request.url.includes('/api/v1/plex/pin/'));
    expect(polls).toHaveLength(1);
    expect(polls[0]?.url).toContain('/api/v1/plex/pin/42');
  });

  it('shows the signed-in view and a notification once plex.tv approves the code', async () => {
    let approved = false;
    install({
      state: () => jsonResponse(approved ? PLEX_STATE_SIGNED_IN : PLEX_STATE_SIGNED_OUT),
      pinStatus: () => {
        approved = true;

        return jsonResponse({ authorized: true, expired: false });
      },
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Sign in with Plex' }));

    expect(await screen.findByText('Signed in to Plex')).toBeInTheDocument();
    expect(await screen.findByText('Home NAS · http://10.0.0.5:32400')).toBeInTheDocument();
    expect(screen.getByText(/This install's Plex client id: wondarr-2f7c1a9b/)).toBeInTheDocument();
  });

  it('offers a new code when the sign-in code expired', async () => {
    install({ pinStatus: () => jsonResponse({ authorized: false, expired: true }) });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Sign in with Plex' }));

    expect(await screen.findByText('The sign-in code expired')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Get a new code' }));

    expect(await screen.findByRole('button', { name: 'Sign in with Plex' })).toBeInTheDocument();
  });

  it('stores a pasted token, empties the field, and shows what a refusal said', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByText('Paste a token instead'));
    await user.type(await screen.findByLabelText('Plex token'), 'pasted-token');
    await user.click(screen.getByRole('button', { name: 'Save token' }));

    await waitFor(async () => {
      expect(
        await sentBody((request) => request.method === 'PUT' && request.url.includes('/api/v1/plex/token')),
      ).toEqual({ token: 'pasted-token' });
    });

    await waitFor(() => expect(screen.getByLabelText('Plex token')).toHaveValue(''));
  });

  it('shows the detail of a refused token', async () => {
    install({
      token: () => jsonResponse({ title: 'Plex rejected the token', detail: 'Plex did not accept that token' }, 400),
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByText('Paste a token instead'));
    await user.type(await screen.findByLabelText('Plex token'), 'wrong-token');
    await user.click(screen.getByRole('button', { name: 'Save token' }));

    expect(await screen.findByText('Plex did not accept that token')).toBeInTheDocument();
  });
});

describe('PlexSettingsPage server', () => {
  it('lists the servers with a relay connection last and selects the chosen connection', async () => {
    install({ state: () => jsonResponse(PLEX_STATE_SIGNED_IN_UNSELECTED) });
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('No server selected')).toBeInTheDocument();
    expect(await screen.findByText('owned')).toBeInTheDocument();
    expect(screen.getByText('shared')).toBeInTheDocument();

    // Mantine's SegmentedControl also renders radios, so only the connection ones are read.
    const radios = (await screen.findAllByRole('radio')).filter(
      (radio) => radio.closest('.mantine-Radio-root') !== null,
    );

    // Home NAS is listed first and offers its three connections before the shared server's one.
    expect(radios).toHaveLength(4);

    const homeNas = radios.slice(0, 3);

    // plex.tv gives the relay first; the page offers that slowest way in last.
    expect(labelOf(homeNas[2])).toContain('relay');
    expect(labelOf(homeNas[0])).toContain('local');
    expect(labelOf(homeNas[0])).toContain('http://10.0.0.5:32400');

    await user.click(screen.getByText('http://10.0.0.5:32400'));
    await user.click(screen.getByRole('button', { name: 'Use this server' }));

    await waitFor(async () => {
      expect(
        await sentBody((request) => request.method === 'PUT' && request.url.includes('/api/v1/plex/server')),
      ).toEqual({ serverUrl: 'http://10.0.0.5:32400' });
    });
  });

  it('takes a server URL typed by hand', async () => {
    install({ state: () => jsonResponse(PLEX_STATE_SIGNED_IN_UNSELECTED) });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('No server selected');
    await user.type(screen.getByLabelText('Server URL'), 'https://plex.example.com');
    await user.click(screen.getByRole('button', { name: 'Use this server' }));

    await waitFor(async () => {
      expect(
        await sentBody((request) => request.method === 'PUT' && request.url.includes('/api/v1/plex/server')),
      ).toEqual({ serverUrl: 'https://plex.example.com' });
    });
  });

  it('reports a connection test in one line', async () => {
    install({ state: () => jsonResponse(PLEX_STATE_SIGNED_IN) });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Home NAS · http://10.0.0.5:32400');
    await user.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText('Connected to Home NAS (Plex 1.43.4.1000), 2 music libraries')).toBeInTheDocument();
  });

  it('reports a failed connection test in red', async () => {
    install({ state: () => jsonResponse(PLEX_STATE_SIGNED_IN), test: () => jsonResponse(TEST_FAILED) });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Home NAS · http://10.0.0.5:32400');
    await user.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText('Plex did not answer within 10 s.')).toBeInTheDocument();
  });

  it('asks before signing out, then signs out', async () => {
    let signedIn = true;
    install({
      state: () => jsonResponse(signedIn ? PLEX_STATE_SIGNED_IN : PLEX_STATE_SIGNED_OUT),
      signOut: () => {
        signedIn = false;

        return new Response(null, { status: 204 });
      },
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Home NAS · http://10.0.0.5:32400');
    await user.click(screen.getByRole('button', { name: 'Sign out' }));

    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText('Sign out of Plex?')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Sign out of Plex' }));

    await waitFor(() => {
      expect(sent().some((request) => request.url.endsWith('/api/v1/plex') && request.method === 'DELETE')).toBe(true);
    });

    expect(await screen.findByRole('button', { name: 'Sign in with Plex' })).toBeInTheDocument();
  });
});
