import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

/** The settings the server holds, as `GET /api/v1/youtube/settings` sends them. */
const SETTINGS = {
  enabled: false,
  cookiesPath: null,
  poTokenBaseUrl: null,
  allowVideos: false,
  searchLimit: 20,
  outputPolicy: { codec: 'aac', mode: 'cbr', bitrateKbps: 256, vbrQuality: 0, sampleRate: 'keep' },
  ytdlp: { sleepRequestsSeconds: 0.75, sleepIntervalSeconds: 10, maxSleepIntervalSeconds: 20, retries: 5 },
  readOnlyFields: [] as string[],
};

/** What the health probe last found, as `GET /api/v1/youtube/status` sends it. */
const STATUS = { version: '2026.08.19', hasJsRuntime: true, binaryAvailable: true };

beforeEach(() => {
  resetLocation('/settings/youtube');
  window.localStorage.clear();
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
  put: () => Response = () => jsonResponse({ ...SETTINGS, ...overrides }),
): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/youtube/settings')) {
      return init?.method === 'PUT' ? put() : jsonResponse({ ...SETTINGS, ...overrides });
    }

    if (url.includes('/api/v1/youtube/status')) {
      return jsonResponse(STATUS);
    }

    if (url.includes('/api/v1/youtube/test')) {
      return jsonResponse({ ...STATUS, version: '2026.09.01' });
    }

    return new Response('not found', { status: 404 });
  });
}

describe('YouTubeSettingsPage', () => {
  it('shows the stored settings and the probe status', async () => {
    install();

    renderApp();

    expect(await screen.findByText('yt-dlp 2026.08.19')).toBeInTheDocument();
    expect(screen.getByText('The JS runtime (Deno) answered.')).toBeInTheDocument();
    expect(screen.getByLabelText('Search budget per song')).toHaveValue('20');
    expect(screen.getByLabelText('Cookies file')).toHaveValue('');
  });

  it('shows the ToS disclaimer on the first enable and enables after acknowledging', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('yt-dlp 2026.08.19');

    // The first enable opens the disclaimer instead of flipping the toggle. A Switch's label
    // nests its description, so the query matches on the label's leading text.
    const enable = () => screen.getByLabelText(/Enable the YouTube source/);

    await user.click(enable());

    expect(await screen.findByText(/YouTube's Terms of Service prohibit automated access/)).toBeInTheDocument();

    // "Keep it off" closes the modal and leaves the source off.
    await user.click(screen.getByRole('button', { name: 'Keep it off' }));
    expect(enable()).not.toBeChecked();

    // The second enable opens it again (nothing was acknowledged yet).
    await user.click(enable());
    await screen.findByText(/YouTube's Terms of Service prohibit automated access/);
    await user.click(screen.getByRole('button', { name: 'I understand' }));

    expect(enable()).toBeChecked();
    expect(window.localStorage.getItem('wondarr:youtube-tos-ack')).toBe('1');
  });

  it('does not show the disclaimer again after it was acknowledged once', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('yt-dlp 2026.08.19');

    // Acknowledge once.
    const enable = () => screen.getByLabelText(/Enable the YouTube source/);

    await user.click(enable());
    await screen.findByText(/YouTube's Terms of Service prohibit automated access/);
    await user.click(screen.getByRole('button', { name: 'I understand' }));

    // Disable and re-enable: no modal this time, the toggle flips straight.
    await user.click(enable());
    await user.click(enable());

    expect(enable()).toBeChecked();
    expect(screen.queryByText(/YouTube's Terms of Service prohibit automated access/)).toBeNull();
  });

  it('saves the form through PUT and shows the fresh probe after Test', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('yt-dlp 2026.08.19');

    await user.click(screen.getByRole('button', { name: 'Test' }));

    expect(await screen.findByText('yt-dlp 2026.09.01')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Cookies file'));
    await user.type(screen.getByLabelText('Cookies file'), '/config/cookies.txt');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const put = sent().find((request) => request.method === 'PUT');

      expect(put?.url).toContain('/api/v1/youtube/settings');

      const body: unknown = put === undefined ? null : JSON.parse(await put.body);

      expect(body).toMatchObject({
        enabled: false,
        cookiesPath: '/config/cookies.txt',
        searchLimit: 20,
        ytdlp: { sleepRequestsSeconds: 0.75, sleepIntervalSeconds: 10, maxSleepIntervalSeconds: 20, retries: 5 },
        outputPolicy: { codec: 'aac', mode: 'cbr', bitrateKbps: 256, vbrQuality: 0, sampleRate: 'keep' },
      });
    });

    expect(await screen.findByText('Saved')).toBeInTheDocument();
  });

  it('shows the server validation errors', async () => {
    install(
      {},
      () =>
        jsonResponse(
          {
            title: 'One or more validation errors occurred.',
            errors: { settings: ['youtube.search_limit: must be between 1 and 50.'] },
          },
          400,
        ),
    );
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('yt-dlp 2026.08.19');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('must be between 1 and 50.')).toBeInTheDocument();
  });

  it('disables a field the environment sets', async () => {
    install({ readOnlyFields: ['searchLimit'], searchLimit: 7 });

    renderApp();

    const budget = await screen.findByLabelText('Search budget per song');

    expect(budget).toBeDisabled();
    expect(budget).toHaveValue('7');
    expect(screen.getByLabelText('Cookies file')).toBeEnabled();
  });
});
