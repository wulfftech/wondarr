import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ADDED_SONG,
  HEALTH_ENTRIES,
  LIBRARIES,
  LOOKUP_RESULTS,
  QUALITY_PROFILES,
  SONGS,
  SYSTEM_STATUS,
} from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../test/helpers';

beforeEach(() => {
  resetLocation('/discover?mix=42');
  // jsdom has no scrollIntoView, which Mantine's select and menu call as they open.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const MB_ID = '0e1c3a7e-5a43-4d77-8f3c-6b2e1c9d4a11';

/** A suggestion with an MBID, and one with no id at all. */
const SUGGESTIONS = [
  {
    title: 'Angel',
    artist: 'Massive Attack',
    album: 'Mezzanine',
    coverUrl: null,
    mbRecordingId: MB_ID,
    isrc: null,
    deezerId: null,
    durationMs: 379000,
    sources: ['listenbrainz', 'lastfm'],
    score: 0.9,
  },
  {
    title: 'Roads',
    artist: 'Portishead',
    album: null,
    coverUrl: null,
    mbRecordingId: null,
    isrc: null,
    deezerId: null,
    durationMs: null,
    sources: ['deezer'],
    score: 0.4,
  },
];

const ALL_ANSWERED = [
  { name: 'listenbrainz', status: 'answered' },
  { name: 'lastfm', status: 'answered' },
  { name: 'deezer', status: 'skipped' },
];

function discoverBody(overrides: Record<string, unknown> = {}) {
  return {
    mode: 'mix',
    mixSeed: 42,
    limit: 50,
    totalBeforeLimit: SUGGESTIONS.length,
    seeds: [{ songId: 1, title: 'Teardrop', artist: 'Massive Attack' }],
    sources: ALL_ANSWERED,
    suggestions: SUGGESTIONS,
    ...overrides,
  };
}

const DISMISSAL = {
  id: 7,
  mbRecordingId: MB_ID,
  artist: 'Massive Attack',
  title: 'Angel',
  artistKey: 'massive attack',
  titleKey: 'angel',
  created: '2026-11-17T00:00:00Z',
};

/** The route table the page needs; `discover` is the answer to `GET /api/v1/discover`. */
function install(discover: () => Response = () => jsonResponse(discoverBody()), dismissed: unknown[] = []) {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/qualityprofile')) {
      return jsonResponse(QUALITY_PROFILES);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (url.includes('/api/v1/discover/dismissals')) {
      if (method === 'POST') {
        return jsonResponse(DISMISSAL);
      }

      return method === 'DELETE' ? new Response(null, { status: 200 }) : jsonResponse(dismissed);
    }

    if (url.includes('/api/v1/discover')) {
      return discover();
    }

    if (url.includes('/api/v1/song/lookup')) {
      return jsonResponse(LOOKUP_RESULTS);
    }

    if (/\/api\/v1\/song\/1(\?|$)/.test(url)) {
      return jsonResponse(SONGS[0]);
    }

    if (url.includes('/api/v1/song')) {
      return jsonResponse(ADDED_SONG, 201);
    }

    return new Response('not found', { status: 404 });
  });
}

/** What the mocked `fetch` received; the generated client puts the method and body on the `Request`. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

function row(title: string): HTMLElement {
  const card = screen.getByText(title).closest('.mantine-Card-root');

  if (!(card instanceof HTMLElement)) {
    throw new Error(`No row for ${title}`);
  }

  return card;
}

describe('DiscoverPage', () => {
  it('shows the mix for the seed in the URL, with each row and its sources', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Angel')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Mix from your library' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();

    const angel = within(row('Angel'));
    expect(angel.getByText('Massive Attack')).toBeInTheDocument();
    expect(angel.getByText('Mezzanine')).toBeInTheDocument();
    // 379000 ms reads as 6:19.
    expect(angel.getByText('6:19')).toBeInTheDocument();
    expect(angel.getByText('ListenBrainz')).toBeInTheDocument();
    expect(angel.getByText('Last.fm')).toBeInTheDocument();

    const request = sent().find((call) => call.url.includes('/api/v1/discover?'));
    expect(request?.url).toContain('mixSeed=42');
    expect(request?.url).not.toContain('songId');
    // The seed stays in the URL, so a reload shows the same mix.
    expect(window.location.search).toBe('?mix=42');
  });

  it('draws a seed into the URL when the page opens without one', async () => {
    resetLocation('/discover');
    install();

    renderApp();

    await screen.findByText('Angel');
    expect(window.location.search).toMatch(/^\?mix=\d+$/);
  });

  it('shows the radio header with the seed, its cover slot and a way back to the mix', async () => {
    resetLocation('/discover/1');
    install(() => jsonResponse(discoverBody({ mode: 'radio', mixSeed: null })));

    renderApp();

    expect(await screen.findByRole('heading', { name: 'Radio: Teardrop – Massive Attack' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to mix' })).toHaveAttribute('href', '/discover');
    expect(screen.queryByRole('button', { name: 'Refresh' })).not.toBeInTheDocument();

    const request = sent().find((call) => call.url.includes('/api/v1/discover?'));
    expect(request?.url).toContain('songId=1');
  });

  it('removes a dismissed row and brings it back on Undo', async () => {
    install();
    const user = userEvent.setup();

    renderApp();
    await screen.findByText('Angel');

    await user.click(within(row('Angel')).getByRole('button', { name: /Not interested/ }));

    await waitFor(() => expect(screen.queryByText('Angel')).not.toBeInTheDocument());
    const post = sent().find((call) => call.method === 'POST' && call.url.includes('/discover/dismissals'));
    expect(JSON.parse(await (post?.body ?? Promise.resolve('null')))).toEqual({
      mbRecordingId: MB_ID,
      artist: 'Massive Attack',
      title: 'Angel',
    });

    await user.click(await screen.findByRole('button', { name: 'Undo' }));

    expect(await screen.findByText('Angel')).toBeInTheDocument();
    expect(sent().some((call) => call.method === 'DELETE' && call.url.endsWith('/discover/dismissals/7'))).toBe(true);
  });

  it('adds a suggestion that has an MBID straight through the add endpoint', async () => {
    install();
    const user = userEvent.setup();

    renderApp();
    await screen.findByText('Angel');

    await user.click(within(row('Angel')).getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(within(row('Angel')).getByText('Added')).toBeInTheDocument());
    const post = sent().find(
      (call) => call.method === 'POST' && call.url.endsWith('/api/v1/song') && !call.url.includes('lookup'),
    );
    expect(JSON.parse(await (post?.body ?? Promise.resolve('null')))).toMatchObject({ mbRecordingId: MB_ID });
    expect(sent().some((call) => call.url.includes('/api/v1/song/lookup'))).toBe(false);

    expect(within(row('Angel')).getByRole('link', { name: 'Open song' })).toHaveAttribute('href', '/song/41');
    expect(within(row('Angel')).getByRole('link', { name: 'Radio from this' })).toHaveAttribute('href', '/discover/41');
  });

  it('looks a suggestion with no ids up by name and lets the user pick the result', async () => {
    install();
    const user = userEvent.setup();

    renderApp();
    await screen.findByText('Roads');

    await user.click(within(row('Roads')).getByRole('button', { name: 'Add' }));

    const lookup = sent().find((call) => call.url.includes('/api/v1/song/lookup'));
    expect(JSON.parse(await (lookup?.body ?? Promise.resolve('null')))).toEqual({ term: 'Portishead - Roads' });

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('Get Lucky')).toBeInTheDocument();
    // Nothing is added until a result is picked.
    expect(sent().some((call) => call.method === 'POST' && call.url.endsWith('/api/v1/song'))).toBe(false);

    await user.click(within(dialog).getAllByRole('button', { name: 'Add' })[0]);

    await waitFor(() => expect(within(row('Roads')).getByText('Added')).toBeInTheDocument());
    const post = sent().find((call) => call.method === 'POST' && call.url.endsWith('/api/v1/song'));
    expect(JSON.parse(await (post?.body ?? Promise.resolve('null')))).toMatchObject({
      mbRecordingId: LOOKUP_RESULTS[0].mbRecordingId,
    });
  });

  it('says which sources did not answer, and where to set the Last.fm key', async () => {
    install(() =>
      jsonResponse(
        discoverBody({
          sources: [
            { name: 'listenbrainz', status: 'failed' },
            { name: 'lastfm', status: 'skipped' },
            { name: 'deezer', status: 'skipped' },
          ],
        }),
      ),
    );

    renderApp();

    expect(await screen.findByText(/Last\.fm off: set a key in/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Settings → General' })).toHaveAttribute('href', '/settings/general');
    expect(screen.getByText(/ListenBrainz didn't answer/)).toBeInTheDocument();
  });

  it('gives the reason when there is nothing to suggest', async () => {
    install(() => jsonResponse(discoverBody({ suggestions: [], totalBeforeLimit: 0 })));

    renderApp();

    expect(await screen.findByText(/Nothing new in this mix/)).toBeInTheDocument();
  });

  it('lists the dismissed suggestions in a drawer, each with an Undo', async () => {
    install(() => jsonResponse(discoverBody()), [DISMISSAL]);
    const user = userEvent.setup();

    renderApp();
    await screen.findByText('Angel');

    await user.click(await screen.findByRole('button', { name: 'Not interested (1)' }));

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Angel')).toBeInTheDocument();
    expect(within(drawer).getByText('Massive Attack')).toBeInTheDocument();

    await user.click(within(drawer).getByRole('button', { name: 'Undo' }));

    await waitFor(() =>
      expect(sent().some((call) => call.method === 'DELETE' && call.url.endsWith('/discover/dismissals/7'))).toBe(true),
    );
  });
});
