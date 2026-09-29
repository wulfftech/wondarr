import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS, paged } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

/** Three grabs: one downloading, one waiting in a peer's queue and one that failed to import. */
const QUEUE_ITEMS = [
  {
    id: 31,
    songId: 1,
    songTitle: 'Teardrop',
    artistCredit: 'Massive Attack',
    sourceType: 'soulseek',
    provider: 'peer-one',
    displayName: 'Massive Attack - Teardrop.flac',
    remotePath: 'Music\\Massive Attack\\Mezzanine\\01 - Teardrop.flac',
    state: 'downloading',
    progress: 0.42,
    bytesTransferred: 12582912,
    sizeBytes: 31457280,
    placeInQueue: null,
    message: null,
    attempt: 1,
    qualityId: 20,
    qualityName: 'FLAC',
    createdAt: '2026-01-08T00:00:00Z',
    stateChangedAt: '2026-01-08T00:00:05Z',
    finishedAt: null,
  },
  {
    id: 32,
    songId: 2,
    songTitle: 'Xtal',
    artistCredit: 'Aphex Twin',
    sourceType: 'soulseek',
    provider: 'peer-two',
    displayName: 'Aphex Twin - Xtal.mp3',
    remotePath: 'Music\\Aphex Twin\\Xtal.mp3',
    state: 'remotelyQueued',
    progress: 0,
    bytesTransferred: 0,
    sizeBytes: 10485760,
    placeInQueue: 3,
    message: null,
    attempt: 2,
    qualityId: 10,
    qualityName: 'MP3-256',
    createdAt: '2026-01-08T00:01:00Z',
    stateChangedAt: '2026-01-08T00:01:05Z',
    finishedAt: null,
  },
  {
    id: 33,
    songId: 3,
    songTitle: 'Angel',
    artistCredit: 'Massive Attack',
    sourceType: 'soulseek',
    provider: 'peer-three',
    displayName: 'Massive Attack - Angel.flac',
    remotePath: 'Music\\Massive Attack\\Mezzanine\\02 - Angel.flac',
    state: 'failed',
    progress: 1,
    bytesTransferred: 31457280,
    sizeBytes: 31457280,
    placeInQueue: null,
    message: 'The duration did not match the song.',
    attempt: 3,
    qualityId: 20,
    qualityName: 'FLAC',
    createdAt: '2026-01-08T00:02:00Z',
    stateChangedAt: '2026-01-08T00:03:00Z',
    finishedAt: '2026-01-08T00:03:00Z',
  },
];

beforeEach(() => {
  resetLocation('/activity/queue');
  // jsdom has no scrollIntoView, which Mantine's menu calls as it opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** The route table the queue needs: the shell, the list and the removals. */
function install(items: unknown = paged(QUEUE_ITEMS)): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/queue/')) {
      return jsonResponse({});
    }

    if (url.includes('/api/v1/queue')) {
      return jsonResponse(items);
    }

    return new Response('not found', { status: 404 });
  });
}

/** The URL of the DELETE the page sent, once it has sent one. */
async function removalUrl(mock: FetchMock): Promise<string | undefined> {
  await waitFor(() => {
    expect(mock.calls.some((call) => call.init?.method === 'DELETE')).toBe(true);
  });

  return mock.calls.find((call) => call.init?.method === 'DELETE')?.url;
}

describe('QueuePage', () => {
  it('lists the grabs with their state, progress, peer position and failure message', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Teardrop')).toBeInTheDocument();
    expect(screen.getByText('Downloading')).toBeInTheDocument();
    // The peer's own queue position is part of the badge.
    expect(screen.getByText('Queued by peer (#3)')).toBeInTheDocument();
    expect(screen.getByText('Failed')).toBeInTheDocument();
    // 12582912 of 31457280 bytes is 42 %.
    expect(screen.getByText('42% · 12 MB of 30 MB')).toBeInTheDocument();
    expect(screen.getAllByText('FLAC').length).toBeGreaterThan(0);
    expect(screen.getByText('The duration did not match the song.')).toBeInTheDocument();
  });

  it('removes an item straight away when Remove is picked', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Actions for Teardrop'));
    await user.click(await screen.findByRole('menuitem', { name: 'Remove' }));

    expect(await removalUrl(mock)).toContain('/api/v1/queue/31');
    expect(await removalUrl(mock)).toContain('blocklist=false');
  });

  it('asks before blocklisting, and sends the blocklist-skip query', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Actions for Teardrop'));
    await user.click(await screen.findByRole('menuitem', { name: 'Remove and blocklist' }));

    // Nothing is sent until the dialog's own button is pressed.
    expect(mock.calls.some((call) => call.init?.method === 'DELETE')).toBe(false);

    const dialog = await screen.findByRole('dialog', { name: 'Remove and blocklist' });

    await user.click(within(dialog).getByRole('button', { name: 'Remove and blocklist' }));

    expect(await removalUrl(mock)).toContain('/api/v1/queue/31');
    expect(await removalUrl(mock)).toContain('blocklist=true');
    expect(await removalUrl(mock)).toContain('skipRedownload=true');
  });

  it('replaces a blocklisted file at once when the search-again action is picked', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Actions for Xtal'));
    await user.click(await screen.findByRole('menuitem', { name: 'Blocklist and search again' }));

    const dialog = await screen.findByRole('dialog', { name: 'Blocklist and search again' });

    await user.click(within(dialog).getByRole('button', { name: 'Blocklist and search again' }));

    expect(await removalUrl(mock)).toContain('/api/v1/queue/32');
    expect(await removalUrl(mock)).toContain('skipRedownload=false');
  });

  it('shows the empty state when nothing is in flight', async () => {
    install(paged([]));

    renderApp();

    expect(await screen.findByText('Nothing is downloading.')).toBeInTheDocument();
  });

  it('asks for the finished items too when Show finished is turned on', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Teardrop');
    await user.click(screen.getByRole('switch', { name: 'Show finished' }));

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('includeFinished=true'))).toBe(true);
    });
  });

  it('shows the failure when the queue cannot be read', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      return jsonResponse({ title: 'Server error' }, 500);
    });

    renderApp();

    expect(await screen.findByText('The queue request failed.')).toBeInTheDocument();
  });
});
