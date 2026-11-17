import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../../test/helpers';

/** The settings the server holds, as `GET /api/v1/musicvideo/settings` sends them. */
const SETTINGS = {
  enabled: true,
  rootPath: '/music-videos',
  layout: 'folder',
  namingTemplate: '{Artist}/{Artist} - {Title}',
  besideTrackSuffix: '-video',
  maxHeight: null as number | null,
  writeNfo: true,
  writeThumbnail: true,
  scheduledSearchLimit: 20,
  scheduledSearchEnabled: false,
  scheduledSearchIntervalHours: 24,
  readOnlyFields: [] as string[],
  rootStatus: 'ready' as string | null,
  rootNeedsConfirmation: false,
};

beforeEach(() => {
  resetLocation('/settings/musicvideos');
  // jsdom has no scrollIntoView, which Mantine's select calls as its list opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** The requests the page sent with a method, newest last, with their bodies read. */
async function sent(method: string): Promise<{ url: string; body: unknown }[]> {
  const requests = vi
    .mocked(globalThis.fetch)
    .mock.calls.map(([input, init]) => (input instanceof Request ? input : new Request(input, init)))
    .filter((request) => request.method === method);

  return Promise.all(
    requests.map(async (request) => {
      const text = await request.clone().text();

      return { url: request.url, body: text === '' ? null : (JSON.parse(text) as unknown) };
    }),
  );
}

function install(overrides: Partial<typeof SETTINGS> = {}) {
  const stored = { ...SETTINGS, ...overrides };

  installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.endsWith('/api/v1/musicvideo/settings/root/confirm')) {
      // The marker is written, so the next read finds the root ready.
      stored.rootStatus = 'ready';
      stored.rootNeedsConfirmation = false;

      return jsonResponse(stored);
    }

    if (url.endsWith('/api/v1/musicvideo/settings')) {
      return jsonResponse(stored);
    }

    return new Response('not found', { status: 404 });
  });
}

describe('MusicVideoSettingsPage', () => {
  it('shows the stored settings', async () => {
    install();

    renderApp();

    expect(await screen.findByLabelText('Video folder')).toHaveValue('/music-videos');
    expect(screen.getByLabelText('Naming template')).toHaveValue('{Artist}/{Artist} - {Title}');
    expect(screen.getByRole('combobox', { name: 'Maximum height' })).toHaveValue('Any');
    expect(screen.getByLabelText('Songs per run')).toHaveValue('20');
    expect(screen.getByLabelText(/Find missing videos on a schedule/)).not.toBeChecked();
  });

  it('shows the beside-track suffix only for that layout', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const layout = await screen.findByRole('combobox', { name: 'Layout' });

    expect(layout).toHaveValue('Separate folder (Jellyfin, Kodi, Plex Other Videos)');
    expect(screen.queryByLabelText('Beside-track suffix')).not.toBeInTheDocument();

    await user.click(layout);
    await user.click(await screen.findByRole('option', { name: 'Beside each track (Plex)' }));

    expect(screen.getByLabelText('Beside-track suffix')).toHaveValue('-video');
    expect(screen.getByText(/songs held only by a reference library use the separate folder/i)).toBeInTheDocument();

    await user.click(screen.getByRole('combobox', { name: 'Layout' }));
    await user.click(
      await screen.findByRole('option', { name: 'Separate folder (Jellyfin, Kodi, Plex Other Videos)' }),
    );

    expect(screen.queryByLabelText('Beside-track suffix')).not.toBeInTheDocument();
  });

  it('saves a partial update, with 0 for no height cap', async () => {
    install({ maxHeight: 1080 });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('combobox', { name: 'Maximum height' }));
    await user.click(await screen.findByRole('option', { name: 'Any' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await sent('PUT')).toHaveLength(1));

    const [put] = await sent('PUT');

    expect(put?.body).toMatchObject({ maxHeight: 0, layout: 'folder', enabled: true });
    // The folder did not change, so it is not written again.
    expect(put?.body).not.toHaveProperty('rootPath');
    expect(put?.body).not.toHaveProperty('besideTrackSuffix');
  });

  it('asks for confirmation when the root is missing or unmarked, and confirms it', async () => {
    install({ rootStatus: 'unmarked', rootNeedsConfirmation: true });
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Is this the right folder?')).toBeInTheDocument();
    expect(screen.getByText(/no Wondarr marker/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Yes, use this folder' }));

    await waitFor(async () => expect(await sent('POST')).toHaveLength(1));

    const [post] = await sent('POST');

    expect(post?.url).toMatch(/\/musicvideo\/settings\/root\/confirm$/);
    await waitFor(() => expect(screen.queryByText('Is this the right folder?')).not.toBeInTheDocument());
  });

  it('shows nothing about the root when it is ready', async () => {
    install();

    renderApp();

    await screen.findByLabelText('Video folder');

    expect(screen.queryByText('Is this the right folder?')).not.toBeInTheDocument();
  });

  it('locks a field the environment sets', async () => {
    install({ readOnlyFields: ['rootPath'] });

    renderApp();

    expect(await screen.findByLabelText('Video folder')).toBeDisabled();
    expect(screen.getByLabelText('Naming template')).toBeEnabled();
  });

  it('shows the server’s reason against the field', async () => {
    installFetch((url, init) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (init?.method === 'PUT') {
        return jsonResponse(
          { title: 'Validation', errors: { namingTemplate: ['namingTemplate must not hold {Genre}'] } },
          400,
        );
      }

      return jsonResponse(SETTINGS);
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByText('namingTemplate must not hold {Genre}')).toBeInTheDocument();
  });
});
