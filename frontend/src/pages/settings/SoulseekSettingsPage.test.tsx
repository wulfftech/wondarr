import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

/** The settings the server holds, as `GET /api/v1/soulseek/settings` sends them. */
const SETTINGS = {
  username: 'wondarr-user',
  passwordSet: true,
  listenPort: 50300,
  shareLibrary: true,
  sharedFolders: ['/data/music'],
  uploadSlots: 2,
  uploadSpeedLimitKib: 0,
  distributedNetwork: false,
  downloadsDir: '/data/downloads/slskd',
  incompleteDir: '/data/downloads/slskd/incomplete',
  readOnlyFields: [] as string[],
};

/** What slskd is doing, as `GET /api/v1/soulseek/status` sends it. */
const STATUS = {
  mode: 'bundled',
  state: 'connected',
  version: '0.26.0',
  loggedIn: true,
  username: 'wondarr-user',
  loginProblem: null,
  lastError: null,
  pendingRestart: false,
  sharing: { enabled: true, folders: ['/data/music'], directories: 12, files: 340 },
  searchBudget: {
    submittedInWindow: 3,
    maxSearches: 30,
    outstanding: 1,
    maxOutstanding: 2,
    nextAllowedAt: null,
  },
};

beforeEach(() => {
  resetLocation('/settings/soulseek');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

function install(
  overrides: Partial<typeof SETTINGS> = {},
  put: () => Response = () => jsonResponse({ settings: SETTINGS, restartsSlskd: false }),
): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/soulseek/settings')) {
      return init?.method === 'PUT' ? put() : jsonResponse({ ...SETTINGS, ...overrides });
    }

    if (url.includes('/api/v1/soulseek/status')) {
      return jsonResponse(STATUS);
    }

    return new Response('not found', { status: 404 });
  });
}

describe('SoulseekSettingsPage', () => {
  it('shows the stored settings and what slskd is doing', async () => {
    install();

    renderApp();

    expect(await screen.findByDisplayValue('wondarr-user')).toBeInTheDocument();
    expect(screen.getByLabelText('Listen port')).toHaveValue('50300');
    expect(screen.getByText(/Logged in as wondarr-user/)).toBeInTheDocument();
    expect(screen.getByText(/12 folders, 340 files/)).toBeInTheDocument();
    expect(screen.getByText(/3\/30 searches in the last 4 minutes/)).toBeInTheDocument();
  });

  it('disables a field the environment sets', async () => {
    install({ readOnlyFields: ['listenPort'], listenPort: 50301 });

    renderApp();

    const port = await screen.findByLabelText('Listen port');

    expect(port).toBeDisabled();
    expect(screen.getByLabelText('Username')).toBeEnabled();
  });

  it('warns when sharing is turned off', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('wondarr-user');

    expect(screen.queryByText(/often ban peers who share nothing/)).toBeNull();

    await user.click(screen.getByLabelText('Share my library'));

    expect(screen.getByText(/often ban peers who share nothing/)).toBeInTheDocument();
  });

  it('omits the password while it is untouched and sends an empty one after Clear password', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('wondarr-user');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const put = sent().find((request) => request.method === 'PUT');

      expect(put?.url).toContain('/api/v1/soulseek/settings');

      const body: unknown = put === undefined ? null : JSON.parse(await put.body);

      expect(body).toMatchObject({ username: 'wondarr-user', listenPort: 50300, sharedFolders: ['/data/music'] });
      expect(body).not.toHaveProperty('password');
    });

    await user.click(screen.getByRole('button', { name: 'Clear password' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const puts = sent().filter((request) => request.method === 'PUT');
      const last = puts[puts.length - 1];

      expect(last).toBeDefined();

      const body: unknown = last === undefined ? null : JSON.parse(await last.body);

      expect(body).toHaveProperty('password', '');
    });
  });

  it('shows the server validation errors', async () => {
    install({}, () =>
      jsonResponse(
        {
          title: 'One or more validation errors occurred.',
          errors: { settings: ['soulseek.listen_port: must be between 1024 and 65535.'] },
        },
        400,
      ),
    );
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('wondarr-user');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('must be between 1024 and 65535.')).toBeInTheDocument();
  });

  it('tells the user when slskd restarts to apply a change', async () => {
    install({}, () => jsonResponse({ settings: SETTINGS, restartsSlskd: true }));
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('wondarr-user');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Saved — slskd restarts to apply it')).toBeInTheDocument();
  });
});