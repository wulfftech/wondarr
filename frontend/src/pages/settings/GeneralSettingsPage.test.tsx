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
