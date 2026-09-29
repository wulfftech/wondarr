import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { installFetch, jsonResponse, createTestConfig, type FetchMock } from '../test/helpers';
import { InteractiveSearchModal, SongSearchButtons } from './InteractiveSearchModal';

/** The modal's own providers, as `App` builds them: Mantine, the API context and the query cache. */
function renderModal(props: Partial<Parameters<typeof InteractiveSearchModal>[0]> = {}): { mock: FetchMock } {
  const mock = installFetch((url, init) => handler(url, init));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <Notifications />
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={queryClient}>
          <InteractiveSearchModal
            songId={1}
            songTitle="Teardrop"
            artistCredit="Massive Attack"
            opened
            onClose={() => undefined}
            {...props}
          />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );

  return { mock };
}

/** The candidate the engine would grab: the best one, and the first the API returns. */
const ACCEPTED = {
  candidateId: 9001,
  sourceType: 'soulseek',
  provider: 'peer-one',
  displayName: 'Massive Attack - Teardrop.flac',
  remotePath: 'Music\\Massive Attack\\Mezzanine\\01 - Teardrop.flac',
  extension: 'flac',
  qualityId: 20,
  qualityName: 'FLAC',
  sizeBytes: 31457280,
  durationMs: 369000,
  bitrateKbps: 900,
  sampleRate: 44100,
  bitDepth: 16,
  freeUploadSlot: true,
  queueLength: 0,
  uploadSpeed: 2097152,
  score: 910,
  scoreBreakdown: {
    title: 180,
    artist: 100,
    duration: 100,
    identity: 380,
    quality: 300,
    availability: 150,
    sourcePreference: 100,
    adjustments: [{ name: 'bracketCheck', points: 15 }],
    adjustmentTotal: 15,
    total: 910,
    cappedForUnknownDuration: false,
  },
  rejections: [],
  accepted: true,
  parsed: { artist: 'Massive Attack', title: 'Teardrop', album: 'Mezzanine', trackNo: 1, flags: [], pathFlags: [] },
  query: 'Massive Attack Teardrop',
};

/** A candidate the engine refused: its length is 41 s out, and its identity is worse. */
const REJECTED = {
  ...ACCEPTED,
  candidateId: 9002,
  provider: 'peer-two',
  displayName: 'Massive Attack - Teardrop (live).mp3',
  remotePath: 'Music\\Massive Attack\\Live\\Teardrop (live).mp3',
  extension: 'mp3',
  qualityId: 10,
  qualityName: 'MP3-256',
  sizeBytes: 10485760,
  durationMs: 410000,
  freeUploadSlot: false,
  queueLength: 7,
  uploadSpeed: 51200,
  score: 420,
  scoreBreakdown: {
    ...ACCEPTED.scoreBreakdown,
    duration: 12,
    identity: 120,
    quality: 120,
    adjustments: [],
    adjustmentTotal: 0,
    total: 420,
    cappedForUnknownDuration: true,
  },
  rejections: [
    { reason: 'durationOutOfTolerance', message: 'The candidate is 41 s longer than the song.' },
    { reason: 'versionMismatch', message: 'The candidate is live, the song is not.' },
  ],
  accepted: false,
};

const SEARCH_RESULT = {
  searchRunId: 4,
  outcome: 'noAcceptableCandidate',
  message: 'Every candidate failed a rule.',
  releases: [ACCEPTED, REJECTED],
};

/** The route table: the search and the grab. */
function handler(url: string, init?: RequestInit): Response | Promise<Response> {
  if (url.includes('/api/v1/release') && init?.method === 'POST') {
    return POST_STATUS.status === 200
      ? jsonResponse({ queueItemId: 31 }, 201)
      : jsonResponse({ title: 'Already downloading', detail: 'Song is already downloading' }, POST_STATUS.status);
  }

  if (url.includes('/api/v1/release')) {
    return jsonResponse(SEARCH_RESULT);
  }

  return new Response('not found', { status: 404 });
}

/** What the next grab answers with; a test flips it to 409. */
const POST_STATUS = { status: 200 };

afterEach(() => {
  POST_STATUS.status = 200;
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` actually received: the generated client passes a `Request`. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

describe('InteractiveSearchModal', () => {
  it('lists the candidates in the order the API returned them, with their score badges', async () => {
    renderModal();

    expect(await screen.findByText('Massive Attack - Teardrop.flac')).toBeInTheDocument();
    expect(screen.getByText('Massive Attack - Teardrop (live).mp3')).toBeInTheDocument();

    const rows = screen.getAllByRole('row').slice(1);

    expect(within(rows[0]).getByText('Massive Attack - Teardrop.flac')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Massive Attack - Teardrop (live).mp3')).toBeInTheDocument();
    expect(screen.getByLabelText('Score 910')).toBeInTheDocument();
    expect(screen.getByLabelText('Score 420')).toBeInTheDocument();
  });

  it('shows the rejection reasons and why the run accepted nothing', async () => {
    renderModal();

    await screen.findByText('Massive Attack - Teardrop.flac');

    expect(screen.getByText('durationOutOfTolerance')).toBeInTheDocument();
    expect(screen.getByText('versionMismatch')).toBeInTheDocument();
    expect(screen.getByText('2 candidates, none acceptable: Every candidate failed a rule.')).toBeInTheDocument();
  });

  it('expands the score breakdown when a row is clicked', async () => {
    renderModal();
    const user = userEvent.setup();

    await user.click(await screen.findByText('Massive Attack - Teardrop.flac'));

    expect(screen.getByText('Title: 180')).toBeInTheDocument();
    expect(screen.getByText('Identity: 380')).toBeInTheDocument();
    expect(screen.getByText('bracketCheck: 15')).toBeInTheDocument();
    expect(screen.getByText('Total: 910')).toBeInTheDocument();
  });

  it('flags a candidate whose length is not the song’s', async () => {
    renderModal({ songDurationMs: 369000 });

    await screen.findByText('Massive Attack - Teardrop.flac');

    // 410 s against 369 s is 41 s out; 369 s against itself is not.
    expect(screen.getByText('6:50')).toBeInTheDocument();
    expect(screen.getByText('6:09')).toBeInTheDocument();
  });

  it('grabs the candidate an accepted row names', async () => {
    const { mock } = renderModal();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText('Grab Massive Attack - Teardrop.flac'));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST' && request.url.endsWith('/api/v1/release'))).toBe(true);
    });

    const post = sent().find((request) => request.method === 'POST');

    expect(JSON.parse(await (post?.body ?? Promise.resolve('{}')))).toEqual({ candidateId: 9001 });
    expect(mock.calls.some((call) => call.url.includes('/api/v1/release'))).toBe(true);
    expect(await screen.findByText('Grabbed Massive Attack - Teardrop.flac')).toBeInTheDocument();
  });

  it('asks before grabbing a rejected candidate', async () => {
    renderModal();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText('Grab Massive Attack - Teardrop (live).mp3'));

    const dialog = await screen.findByRole('dialog', { name: 'Grab a rejected candidate' });
    const post = sent().find((request) => request.method === 'POST');

    expect(post).toBeUndefined();

    await user.click(within(dialog).getByRole('button', { name: 'Grab anyway' }));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST')).toBe(true);
    });
  });

  it('says the song is already downloading when the grab conflicts', async () => {
    POST_STATUS.status = 409;
    renderModal();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText('Grab Massive Attack - Teardrop.flac'));

    expect(await screen.findByText('Already downloading')).toBeInTheDocument();
  });
});

describe('SongSearchButtons', () => {
  it('posts the SongSearch command for the song', async () => {
    installFetch((url, init) =>
      url.includes('/api/v1/command') && init?.method === 'POST'
        ? jsonResponse({}, 201)
        : new Response('not found', { status: 404 }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const user = userEvent.setup();

    render(
      <MantineProvider>
        <Notifications />
        <ApiProvider config={createTestConfig()}>
          <QueryClientProvider client={queryClient}>
            <SongSearchButtons songId={1} title="Teardrop" artistCredit="Massive Attack" durationMs={369000} />
          </QueryClientProvider>
        </ApiProvider>
      </MantineProvider>,
    );

    await user.click(screen.getByLabelText('Search for Teardrop'));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST' && request.url.endsWith('/api/v1/command'))).toBe(true);
    });

    const post = sent().find((request) => request.method === 'POST');
    const body = JSON.parse(await (post?.body ?? Promise.resolve('{}'))) as { name: string; body: string };

    expect(body.name).toBe('SongSearch');
    expect(JSON.parse(body.body)).toEqual({ songId: 1 });
    expect(await screen.findByText('Searching for Teardrop')).toBeInTheDocument();
  });
});
