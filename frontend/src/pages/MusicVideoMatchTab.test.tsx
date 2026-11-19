import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { notifications } from '@mantine/notifications';
import { HEALTH_ENTRIES, SYSTEM_STATUS, paged } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../test/helpers';

/** The settings the Match page reads to decide whether the tab exists. */
const SETTINGS = {
  enabled: true,
  rootPath: '/data/music-videos',
  layout: 'folder',
  namingTemplate: '{Artist}/{Artist} - {Title}',
  besideTrackSuffix: '-video',
  maxHeight: null,
  writeNfo: true,
  writeThumbnail: true,
  scheduledSearchLimit: 20,
  scheduledSearchEnabled: false,
  scheduledSearchIntervalHours: 24,
  readOnlyFields: [],
  rootStatus: 'ready',
  rootNeedsConfirmation: false,
  rootPathIsDefault: true,
  defaultRootPath: '/data/music-videos',
};

const SUMMARY = { pending: 0, adopted: 40, ambiguous: 2, unmatched: 1, ignored: 1, missing: 0, needsAttention: 3 };

/** A candidate as the API sends it. */
function candidate(songId: number, title: string, overrides: Record<string, unknown> = {}) {
  return {
    songId,
    title,
    artistCredit: 'Daft Punk',
    album: 'Random Access Memories',
    durationMs: 248000,
    score: 0.9,
    reason: 'File name',
    songHasVideo: false,
    ...overrides,
  };
}

/** A file in the video folder, as the Match API sends it. */
function row(id: number, overrides: Record<string, unknown> = {}) {
  return {
    id,
    state: 'ambiguous',
    path: `Daft Punk/Get Lucky ${id}.mkv`,
    size: 52428800,
    container: 'matroska,webm',
    durationMs: 249000,
    width: 1920,
    height: 1080,
    parsedArtist: 'Daft Punk',
    parsedTitle: 'Get Lucky',
    nfoArtist: null,
    nfoTitle: null,
    tagArtist: null,
    tagTitle: null,
    candidates: [candidate(12, 'Get Lucky'), candidate(13, 'Get Lucky (Radio Edit)', { score: 0.8 })],
    thumbnailUrl: null,
    ...overrides,
  };
}

const AMBIGUOUS = [row(1), row(2, { candidates: [candidate(20, 'Lose Yourself to Dance', { songHasVideo: true })] })];
const UNMATCHED = [row(3, { state: 'unmatched', candidates: [], parsedTitle: 'Mystery' })];
const IGNORED = [row(4, { state: 'ignored', path: 'Misc/Home movie.mkv', candidates: [] })];

interface Options {
  settings?: Partial<typeof SETTINGS>;
  files?: Record<string, unknown[]>;
  bulk?: { requested: number; adopted: number; ignored: number; skipped: number };
  songs?: unknown[];
  resolveStatus?: number;
}

/** The route table the page needs, with every call to the Match API recorded by the fetch mock. */
function install(options: Options = {}) {
  const files = options.files ?? { ambiguous: AMBIGUOUS, unmatched: UNMATCHED, ignored: IGNORED };

  installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.endsWith('/api/v1/musicvideo/settings')) {
      return jsonResponse({ ...SETTINGS, ...options.settings });
    }

    if (url.includes('/api/v1/musicvideo/match/summary')) {
      return jsonResponse(SUMMARY);
    }

    if (url.includes('/api/v1/musicvideo/match/songs')) {
      return jsonResponse(options.songs ?? []);
    }

    if (url.includes('/api/v1/musicvideo/match/bulk')) {
      return jsonResponse(options.bulk ?? { requested: 2, adopted: 1, ignored: 0, skipped: 1 });
    }

    if (url.includes('/resolve')) {
      return options.resolveStatus === undefined
        ? jsonResponse({ state: 'adopted', songId: 12 })
        : jsonResponse({ title: 'Download in progress', detail: 'A download is in progress' }, options.resolveStatus);
    }

    if (url.endsWith('/api/v1/musicvideo/scan') && method === 'POST') {
      return jsonResponse({ commandId: 5 }, 202);
    }

    if (url.includes('/api/v1/musicvideo/match')) {
      const state = new URL(url, 'http://localhost').searchParams.get('state') ?? 'ambiguous';
      const records = files[state] ?? [];

      return jsonResponse({ ...paged(records), records });
    }

    // The Music tab's own queue and libraries.
    if (url.includes('/api/v1/matchqueue') || url.includes('/api/v1/referencelibrary')) {
      return jsonResponse(url.includes('/matchqueue') ? paged([]) : []);
    }

    return new Response('not found', { status: 404 });
  });
}

/** The requests sent with a method to a URL containing `part`, with their JSON bodies read. */
async function sent(method: string, part: string): Promise<{ url: string; body: Record<string, unknown> | null }[]> {
  const requests = vi
    .mocked(globalThis.fetch)
    .mock.calls.map(([input, init]) => (input instanceof Request ? input : new Request(input, init)))
    .filter((request) => request.method === method && request.url.includes(part));

  return Promise.all(
    requests.map(async (request) => {
      const text = await request.clone().text();

      return { url: request.url, body: text === '' ? null : (JSON.parse(text) as Record<string, unknown>) };
    }),
  );
}

beforeEach(() => {
  resetLocation('/match?tab=videos');
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
  notifications.clean();
});

describe('the Music videos tab', () => {
  it('is not there while the feature is off', async () => {
    install({ settings: { enabled: false } });

    renderApp();

    expect(await screen.findByRole('heading', { name: 'Match' })).toBeInTheDocument();
    await waitFor(() => expect(sent('GET', '/musicvideo/settings')).resolves.toHaveLength(1));
    expect(screen.queryByRole('tab', { name: /Music videos/ })).not.toBeInTheDocument();
    // The address asks for it, but the Music queue is what shows, and nothing asks the video API for files.
    expect(await sent('GET', '/musicvideo/match')).toHaveLength(0);
  });

  it('shows the count of files that need attention on the tab', async () => {
    install();

    renderApp();

    expect(await screen.findByRole('tab', { name: /Music videos/ })).toBeInTheDocument();
    expect(await screen.findByLabelText('3 music videos need a pick')).toBeInTheDocument();
  });

  it('keeps the tab in the address', async () => {
    resetLocation('/match');
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: /Music videos/ }));

    expect(window.location.search).toBe('?tab=videos');
    expect(await screen.findByText('Daft Punk/Get Lucky 1.mkv')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Music' }));

    expect(window.location.search).toBe('');
  });

  it('shows each file with its candidates, the score and "has a video"', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Daft Punk/Get Lucky 1.mkv')).toBeInTheDocument();
    expect(screen.getAllByText('4:09 · MKV · 1920×1080')).toHaveLength(2);
    // Every source read the same name, so there is one line for it.
    expect(screen.getAllByText('File name: Daft Punk – Get Lucky')).toHaveLength(2);
    expect(screen.getByText('Daft Punk – Get Lucky (Radio Edit)')).toBeInTheDocument();
    expect(screen.getAllByRole('progressbar', { name: 'Score 90%' })[0]).toBeInTheDocument();
    expect(screen.getByText('has a video')).toBeInTheDocument();
  });

  it('shows only the names that differ', async () => {
    install({
      files: {
        ambiguous: [
          row(1, {
            nfoArtist: 'Daft Punk',
            nfoTitle: 'Get Lucky',
            tagArtist: 'Daft Punk',
            tagTitle: 'Get Lucky (feat. Pharrell)',
          }),
        ],
      },
    });

    renderApp();

    expect(await screen.findByText('File name, .nfo: Daft Punk – Get Lucky')).toBeInTheDocument();
    expect(screen.getByText('Tags: Daft Punk – Get Lucky (feat. Pharrell)')).toBeInTheDocument();
  });

  it('switches lists with the filter chips, and offers Unignore on the Ignored one', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Daft Punk/Get Lucky 1.mkv')).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'No match' }));

    expect(await screen.findByText('Daft Punk/Get Lucky 3.mkv')).toBeInTheDocument();
    expect(screen.queryByText('Daft Punk/Get Lucky 1.mkv')).not.toBeInTheDocument();
    expect((await sent('GET', '/musicvideo/match?')).some((request) => request.url.includes('state=unmatched'))).toBe(
      true,
    );

    await user.click(screen.getByRole('radio', { name: 'Ignored' }));

    expect(await screen.findByText('Misc/Home movie.mkv')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Unignore' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ignore' })).not.toBeInTheDocument();
  });

  it('links the file to the candidate with Use this', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Use Get Lucky for Daft Punk/Get Lucky 1.mkv' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/1/resolve')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/1/resolve'))[0]?.body).toEqual({
      songId: 12,
      replace: false,
      ignore: false,
      unignore: false,
    });
    expect(await screen.findByText('Matched to Get Lucky')).toBeInTheDocument();
  });

  it("asks before replacing a song's video, and resolves with replace after a yes", async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(
      await screen.findByRole('button', { name: 'Use Lose Yourself to Dance for Daft Punk/Get Lucky 2.mkv' }),
    );

    expect(await screen.findByText("Replace that song's video with this file?")).toBeInTheDocument();
    expect(await sent('POST', '/resolve')).toHaveLength(0);

    // Backing out sends nothing.
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(await sent('POST', '/resolve')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Use Lose Yourself to Dance for Daft Punk/Get Lucky 2.mkv' }));

    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Replace' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/2/resolve')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/2/resolve'))[0]?.body).toEqual({
      songId: 20,
      replace: true,
      ignore: false,
      unignore: false,
    });
  });

  it('searches the library and resolves to the song picked', async () => {
    install({
      songs: [
        { id: 99, title: 'Instant Crush', artistCredit: 'Daft Punk', album: null, durationMs: 337000, hasVideo: false },
      ],
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Search songs for Daft Punk/Get Lucky 1.mkv' }));

    const dialog = await screen.findByRole('dialog');
    const field = within(dialog).getByLabelText('Artist or title');

    expect(field).toHaveValue('Daft Punk – Get Lucky');

    await user.clear(field);
    await user.type(field, 'instant');
    await user.click(within(dialog).getByRole('button', { name: 'Search songs' }));

    await waitFor(async () =>
      expect(
        (await sent('GET', '/musicvideo/match/songs')).some((request) => request.url.includes('term=instant')),
      ).toBe(true),
    );

    await user.click(
      await within(dialog).findByRole('button', { name: 'Use the song Instant Crush for Daft Punk/Get Lucky 1.mkv' }),
    );

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/1/resolve')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/1/resolve'))[0]?.body).toMatchObject({ songId: 99, replace: false });
  });

  it('ignores a file, and puts an ignored one back', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const card = (await screen.findByText('Daft Punk/Get Lucky 1.mkv')).closest('.mantine-Card-root') as HTMLElement;

    await user.click(within(card).getByRole('button', { name: 'Ignore' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/1/resolve')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/1/resolve'))[0]?.body).toEqual({
      songId: null,
      replace: false,
      ignore: true,
      unignore: false,
    });

    await user.click(screen.getByRole('radio', { name: 'Ignored' }));
    await user.click(await screen.findByRole('button', { name: 'Unignore' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/4/resolve')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/4/resolve'))[0]?.body).toEqual({
      songId: null,
      replace: false,
      ignore: false,
      unignore: true,
    });
  });

  it('accepts the top match for the selected rows and says how many were skipped', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Daft Punk/Get Lucky 1.mkv')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Accept top match' })).toBeDisabled();

    await user.click(screen.getByRole('checkbox', { name: 'Select Daft Punk/Get Lucky 1.mkv' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select Daft Punk/Get Lucky 2.mkv' }));
    await user.click(screen.getByRole('button', { name: 'Accept top match' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/bulk')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/bulk'))[0]?.body).toEqual({ ids: [1, 2], action: 'acceptTop' });
    expect(await screen.findByText('Matched 1, skipped 1')).toBeInTheDocument();
  });

  it('ignores the selected rows in one call', async () => {
    install({ bulk: { requested: 2, adopted: 0, ignored: 2, skipped: 0 } });
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Daft Punk/Get Lucky 1.mkv')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Select all on this page' }));
    await user.click(screen.getByRole('button', { name: 'Ignore selected' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/match/bulk')).toHaveLength(1));
    expect((await sent('POST', '/musicvideo/match/bulk'))[0]?.body).toEqual({ ids: [1, 2], action: 'ignore' });
    expect(await screen.findByText('Ignored 2')).toBeInTheDocument();
  });

  it('shows the reason when the API refuses a pick', async () => {
    install({ resolveStatus: 409 });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Use Get Lucky for Daft Punk/Get Lucky 1.mkv' }));

    expect(await screen.findByText('A download is in progress')).toBeInTheDocument();
  });

  it('says every video is matched, and Scan now starts a scan', async () => {
    install({ files: { ambiguous: [] } });
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Every video in the folder is matched')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Scan now' }));

    await waitFor(async () => expect(await sent('POST', '/musicvideo/scan')).toHaveLength(1));
    expect(await screen.findByText('Scanning the video folder')).toBeInTheDocument();
  });
});
