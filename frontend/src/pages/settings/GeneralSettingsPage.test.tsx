import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import {
  createTestConfig,
  installFetch,
  jsonResponse,
  renderApp,
  resetLocation,
  type FetchMock,
} from '../../test/helpers';

// Not a real key: it only ever reaches the rendered page and a mocked fetch.
const API_KEY = '0123456789abcdef0123456789abcdef';

beforeEach(() => {
  resetLocation('/settings/general');
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

function install(user: { configured: boolean; username: string | null }, put: () => Response): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/auth/user')) {
      return init?.method === 'PUT' ? put() : jsonResponse(user);
    }

    return new Response('not found', { status: 404 });
  });
}

const NO_CONTENT = () => new Response(null, { status: 204 });

describe('GeneralSettingsPage', () => {
  it('masks the API key until Show is pressed, and offers Copy', async () => {
    const user = userEvent.setup();
    install({ configured: false, username: null }, NO_CONTENT);

    renderApp(createTestConfig({ apiKey: API_KEY }));

    const key = await screen.findByTestId('api-key');
    expect(key).toHaveTextContent('••••••••cdef');
    expect(document.body).not.toHaveTextContent(API_KEY);
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show' }));
    expect(screen.getByTestId('api-key')).toHaveTextContent(API_KEY);

    await user.click(screen.getByRole('button', { name: 'Hide' }));
    expect(screen.getByTestId('api-key')).toHaveTextContent('••••••••cdef');
  });

  it('shows the configured username, or that there is none', async () => {
    install({ configured: true, username: 'alice' }, NO_CONTENT);

    renderApp();

    expect(await screen.findByText('alice')).toBeInTheDocument();
    expect(screen.getByText(/Signed in as/)).toBeInTheDocument();
  });

  it('says there is no login account yet', async () => {
    install({ configured: false, username: null }, NO_CONTENT);

    renderApp();

    expect(await screen.findByText('No login account yet')).toBeInTheDocument();
  });

  it('saves the login with a PUT and clears the password fields', async () => {
    const user = userEvent.setup();
    install({ configured: false, username: null }, NO_CONTENT);

    renderApp();

    await user.type(await screen.findByLabelText('Username'), 'bob');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.type(screen.getByLabelText('Confirm password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Login saved.')).toBeInTheDocument();

    const put = sent().find((call) => call.method === 'PUT' && call.url.includes('/api/v1/auth/user'));
    expect(put).toBeDefined();
    expect(JSON.parse(await (put?.body ?? Promise.resolve('{}')))).toEqual({
      username: 'bob',
      password: 'correct horse',
    });
    expect(screen.getByLabelText('Password')).toHaveValue('');
    expect(screen.getByLabelText('Confirm password')).toHaveValue('');
  });

  it('shows the API validation message under the field on a 400', async () => {
    const user = userEvent.setup();
    install({ configured: false, username: null }, () =>
      jsonResponse(
        { title: 'One or more validation errors occurred.', errors: { username: ['username is already taken'] } },
        400,
      ),
    );

    renderApp();

    await user.type(await screen.findByLabelText('Username'), 'bob');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.type(screen.getByLabelText('Confirm password'), 'correct horse');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('username is already taken')).toBeInTheDocument();
  });

  it('never sends mismatched passwords', async () => {
    const user = userEvent.setup();
    install({ configured: false, username: null }, NO_CONTENT);

    renderApp();

    await user.type(await screen.findByLabelText('Username'), 'bob');
    await user.type(screen.getByLabelText('Password'), 'correct horse');
    await user.type(screen.getByLabelText('Confirm password'), 'battery staple');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('The passwords do not match.')).toBeInTheDocument();
    expect(sent().some((call) => call.method === 'PUT')).toBe(false);
  });

  it('opens General at /settings', async () => {
    resetLocation('/settings');
    install({ configured: false, username: null }, NO_CONTENT);

    renderApp();

    expect(await screen.findByText('API key')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/Change these in/)).toBeInTheDocument());
  });
});

describe('GeneralSettingsPage metadata services card', () => {
  const LASTFM_KEY = 'typed-lastfm-key-0123456789';

  type Flags = { acoustIdKeySet: boolean; acoustIdLocked: boolean; lastFmKeySet: boolean; lastFmLocked: boolean };

  function installMetadata(
    flags: Flags,
    test: () => Response = () => jsonResponse({ ok: true, message: 'Last.fm accepted the key.' }),
  ): FetchMock {
    return installFetch((url, init) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/auth/user')) {
        return jsonResponse({ configured: false, username: null });
      }

      if (url.includes('/api/v1/metadata/settings/test')) {
        return test();
      }

      if (url.includes('/api/v1/metadata/settings')) {
        return init?.method === 'PUT' ? jsonResponse({ ...flags, lastFmKeySet: true }) : jsonResponse(flags);
      }

      return new Response('not found', { status: 404 });
    });
  }

  const NOTHING: Flags = { acoustIdKeySet: false, acoustIdLocked: false, lastFmKeySet: false, lastFmLocked: false };

  it('shows "Stored — type to replace" for a key that is set, and never the key', async () => {
    installMetadata({ ...NOTHING, lastFmKeySet: true });

    renderApp();

    const lastFm = await screen.findByLabelText('Last.fm API key');
    expect(lastFm).toHaveAttribute('placeholder', 'Stored — type to replace');
    expect(lastFm).toHaveValue('');
    expect(screen.getByLabelText('AcoustID client key')).toHaveAttribute('placeholder', 'Not set');
  });

  it('links to where each key is made and says what it is for', async () => {
    installMetadata(NOTHING);

    renderApp();

    await screen.findByLabelText('Last.fm API key');

    const links = screen.getAllByRole('link', { name: 'Get a key' });
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      'https://acoustid.org/new-application',
      'https://www.last.fm/api/account/create',
    ]);
    expect(screen.getByText(/Fingerprint verification of downloads/)).toBeInTheDocument();
    expect(screen.getByText(/Facts about a song on its page/)).toBeInTheDocument();
  });

  it('tests the stored key through the test endpoint and shows the answer', async () => {
    const user = userEvent.setup();
    installMetadata({ ...NOTHING, lastFmKeySet: true });

    renderApp();

    await screen.findByLabelText('Last.fm API key');
    await user.click(screen.getByRole('button', { name: 'Test Last.fm API key' }));

    expect(await screen.findByText('Last.fm accepted the key.')).toBeInTheDocument();

    const call = sent().find((entry) => entry.url.includes('/api/v1/metadata/settings/test'));
    expect(call?.method).toBe('POST');
    expect(JSON.parse(await (call?.body ?? Promise.resolve('{}')))).toEqual({ service: 'lastfm', key: null });
  });

  it('tests a typed key before it is saved, and shows a rejection', async () => {
    const user = userEvent.setup();
    installMetadata(NOTHING, () => jsonResponse({ ok: false, message: 'Last.fm rejected the key.' }));

    renderApp();

    await user.type(await screen.findByLabelText('Last.fm API key'), LASTFM_KEY);
    await user.click(screen.getByRole('button', { name: 'Test Last.fm API key' }));

    expect(await screen.findByText('Last.fm rejected the key.')).toBeInTheDocument();

    const call = sent().find((entry) => entry.url.includes('/api/v1/metadata/settings/test'));
    expect(JSON.parse(await (call?.body ?? Promise.resolve('{}')))).toEqual({ service: 'lastfm', key: LASTFM_KEY });
  });

  it('saves only the key that was typed, then clears the field', async () => {
    const user = userEvent.setup();
    installMetadata(NOTHING);

    renderApp();

    const field = await screen.findByLabelText('Last.fm API key');
    expect(screen.getByRole('button', { name: 'Save keys' })).toBeDisabled();

    await user.type(field, LASTFM_KEY);
    await user.click(screen.getByRole('button', { name: 'Save keys' }));

    expect(await screen.findByText('Keys saved.')).toBeInTheDocument();

    const put = sent().find((entry) => entry.method === 'PUT' && entry.url.includes('/api/v1/metadata/settings'));
    expect(JSON.parse(await (put?.body ?? Promise.resolve('{}')))).toEqual({ lastFmApiKey: LASTFM_KEY });
    expect(screen.getByLabelText('Last.fm API key')).toHaveValue('');
    expect(screen.getByLabelText('Last.fm API key')).toHaveAttribute('placeholder', 'Stored — type to replace');
  });

  it('removes a stored key by sending an empty string', async () => {
    const user = userEvent.setup();
    installMetadata({ ...NOTHING, lastFmKeySet: true });

    renderApp();

    await screen.findByLabelText('Last.fm API key');
    await user.click(screen.getByRole('button', { name: 'Remove Last.fm API key' }));

    await waitFor(() =>
      expect(sent().some((entry) => entry.method === 'PUT' && entry.url.includes('/api/v1/metadata/settings'))).toBe(
        true,
      ),
    );

    const put = sent().find((entry) => entry.method === 'PUT' && entry.url.includes('/api/v1/metadata/settings'));
    expect(JSON.parse(await (put?.body ?? Promise.resolve('{}')))).toEqual({ lastFmApiKey: '' });
  });

  it('shows a key the environment sets as read-only', async () => {
    installMetadata({ ...NOTHING, lastFmKeySet: true, lastFmLocked: true });

    renderApp();

    const field = await screen.findByLabelText('Last.fm API key');
    expect(field).toBeDisabled();
    expect(field).toHaveAttribute('placeholder', 'Set by the environment');
    expect(screen.queryByRole('button', { name: 'Remove Last.fm API key' })).not.toBeInTheDocument();
  });
});
