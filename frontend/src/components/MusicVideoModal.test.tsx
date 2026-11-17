import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import type { SongResource } from '../api/songs';
import { LIBRARY_SONGS } from '../test/fixtures';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { MusicVideoCell } from './MusicVideoCell';

afterEach(() => {
  vi.unstubAllGlobals();
  // The toast store outlives a test, so one test's toast would answer the next test's query.
  notifications.clean();
});

/** The Get Lucky row with the given video state, as the song list sends it. */
function song(musicVideo: SongResource['musicVideo']): SongResource {
  return { ...LIBRARY_SONGS[0], musicVideo } as unknown as SongResource;
}

const CANDIDATES = [
  {
    videoId: 'h5EofwRzit0',
    title: 'Get Lucky (Official Video)',
    artists: ['Daft Punk'],
    kind: 'officialVideo',
    durationMs: 248000,
    thumbnailUrl: 'https://i.ytimg.com/vi/h5EofwRzit0/hqdefault.jpg',
    confident: true,
    foundBy: 'musicBrainz',
    score: 90,
  },
  {
    videoId: 'ABCDEFGHIJK',
    title: 'Get Lucky (fan upload)',
    artists: [],
    kind: 'userUpload',
    durationMs: null,
    thumbnailUrl: 'https://i.ytimg.com/vi/ABCDEFGHIJK/hqdefault.jpg',
    confident: false,
    foundBy: 'youTubeSearch',
    score: 40,
  },
];

const PRESENT_VIDEO = {
  id: 3,
  songId: 12,
  status: 'present',
  youTubeVideoId: 'h5EofwRzit0',
  foundBy: 'musicBrainz',
  path: '/videos/Daft Punk/Daft Punk - Get Lucky.mkv',
  width: 1920,
  height: 1080,
  videoCodec: 'h264',
  audioCodec: 'opus',
  sizeBytes: 52428800,
  durationMs: 248000,
  failureReason: null,
  createdAt: '2026-11-17T00:00:00Z',
  updatedAt: '2026-11-17T00:00:00Z',
};

const FAILED_VIDEO = {
  ...PRESENT_VIDEO,
  status: 'failed',
  path: null,
  width: null,
  height: null,
  failureReason: 'The video folder has no marker file',
};

interface Routes {
  /** The answer to `GET musicvideo/song/12`; `null` is the 404 of a song with no video. */
  video?: unknown;
  search?: unknown;
}

/** Renders the cell with the providers `App` builds, over a route table for the music video API. */
function renderCell(musicVideo: SongResource['musicVideo'], routes: Routes = {}) {
  installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (/\/musicvideo\/song\/12$/.test(url) && method === 'GET') {
      return routes.video === undefined || routes.video === null
        ? new Response('', { status: 404 })
        : jsonResponse(routes.video);
    }

    if (url.endsWith('/musicvideo/song/12/search')) {
      return jsonResponse(routes.search ?? { queued: false, partial: false, candidates: CANDIDATES });
    }

    if (url.endsWith('/musicvideo/song/12/grab')) {
      return jsonResponse({ commandId: 77 }, 202);
    }

    if (/\/musicvideo\/song\/12(\?.*)?$/.test(url) && method === 'DELETE') {
      return jsonResponse({ path: PRESENT_VIDEO.path, recycledPaths: [], blocklisted: true });
    }

    return new Response('not found', { status: 404 });
  });

  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider env="test">
      <Notifications />
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={queryClient}>
          <MusicVideoCell song={song(musicVideo)} />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

/** The request the page sent with this method to a URL ending as given, if it did. */
function bodyOf(method: string, urlEnd: string): Request | undefined {
  return vi
    .mocked(globalThis.fetch)
    .mock.calls.map(([input, init]) => (input instanceof Request ? input : new Request(input, init)))
    .find((request) => request.method === method && request.url.endsWith(urlEnd));
}

describe('the music video icon', () => {
  it('offers a search when the song has no video', () => {
    renderCell(null);

    expect(screen.getByRole('button', { name: 'Find music video for Get Lucky' })).toBeEnabled();
  });

  it('shows a spinner for a queued video and opens the manage view', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'queued', height: null, failureReason: null },
      { video: { ...PRESENT_VIDEO, status: 'queued' } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky is downloading' }));

    expect(await screen.findByText('Waiting to download…')).toBeInTheDocument();
  });

  it('shows a filled video icon with the height for a present video', async () => {
    const user = userEvent.setup();

    renderCell({ status: 'present', height: 1080, failureReason: null }, { video: PRESENT_VIDEO });

    const icon = screen.getByRole('button', { name: 'Music video for Get Lucky' });

    await user.hover(icon);

    expect(await screen.findByText('1080p')).toBeInTheDocument();

    await user.click(icon);

    expect(await screen.findByText('/videos/Daft Punk/Daft Punk - Get Lucky.mkv')).toBeInTheDocument();
    expect(screen.getByText('1920×1080')).toBeInTheDocument();
    expect(screen.getByText('h264 / opus')).toBeInTheDocument();
    expect(screen.getByText('50 MB')).toBeInTheDocument();
    expect(screen.getByText('MusicBrainz')).toBeInTheDocument();
  });

  it('shows a red crossed-out icon for a failed video and opens the candidates with the reason', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'failed', height: null, failureReason: 'The video folder has no marker file' },
      { video: FAILED_VIDEO },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky failed' }));

    expect(await screen.findByText(/The video folder has no marker file/)).toBeInTheDocument();
    expect(await screen.findByText('Get Lucky (Official Video)')).toBeInTheDocument();
  });
});

describe('finding a music video', () => {
  it('says it is downloading, and opens nothing, when the match was confident', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: true, candidates: [] } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Downloading music video')).toBeInTheDocument();
    expect(screen.queryByText('Music video for Get Lucky')).not.toBeInTheDocument();
  });

  it('opens the candidates when nothing was confident, and downloads the one picked', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Music video for Get Lucky')).toBeInTheDocument();
    expect(screen.getByText('Get Lucky (Official Video)')).toBeInTheDocument();
    expect(screen.getByText('Official video')).toBeInTheDocument();
    expect(screen.getByText('Confident')).toBeInTheDocument();
    expect(screen.getByText('From MusicBrainz')).toBeInTheDocument();
    expect(screen.getByText('Upload')).toBeInTheDocument();
    expect(screen.getByText(/Daft Punk · 4:08/)).toBeInTheDocument();

    const link = screen.getByRole('link', { name: 'Open Get Lucky (Official Video) on YouTube' });

    expect(link).toHaveAttribute('href', 'https://www.youtube.com/watch?v=h5EofwRzit0');
    expect(link).toHaveAttribute('target', '_blank');

    await user.click(screen.getByRole('button', { name: 'Download Get Lucky (Official Video)' }));

    await waitFor(() => expect(bodyOf('POST', '/musicvideo/song/12/grab')).toBeDefined());

    const request = bodyOf('POST', '/musicvideo/song/12/grab');

    expect(await request?.clone().json()).toEqual({ youTubeVideoId: 'h5EofwRzit0' });
    expect(await screen.findByText('Downloading music video')).toBeInTheDocument();
  });

  it('says so when no candidate came back', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: [] } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('No music video candidate came back for this song.')).toBeInTheDocument();
  });
});

describe('partial searches and failure reasons', () => {
  it('says so when MusicBrainz did not answer', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, partial: true, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(
      await screen.findByText("MusicBrainz didn't answer, so these are YouTube results only."),
    ).toBeInTheDocument();
  });

  it('does not say it when MusicBrainz answered', async () => {
    const user = userEvent.setup();

    renderCell(null);

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));
    await screen.findByText('Get Lucky (Official Video)');

    expect(screen.queryByText(/MusicBrainz didn't answer/)).not.toBeInTheDocument();
  });

  it('shows the failure reason in the failed icon tooltip', async () => {
    const user = userEvent.setup();

    renderCell({ status: 'failed', height: null, failureReason: 'The video folder has no marker file' });

    await user.hover(screen.getByRole('button', { name: 'Music video for Get Lucky failed' }));

    expect(await screen.findByRole('tooltip')).toHaveTextContent('The video folder has no marker file');
  });

  it('falls back to a general tooltip when the list carries no reason', async () => {
    const user = userEvent.setup();

    renderCell({ status: 'failed', height: null, failureReason: null });

    await user.hover(screen.getByRole('button', { name: 'Music video for Get Lucky failed' }));

    expect(await screen.findByRole('tooltip')).toHaveTextContent('The download failed. Pick another video.');
  });
});

describe('removing a music video', () => {
  async function openRemove() {
    const user = userEvent.setup();

    renderCell({ status: 'present', height: 1080, failureReason: null }, { video: PRESENT_VIDEO });

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));
    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    return user;
  }

  it('asks first, with the blocklist ticked, and removes with it', async () => {
    const user = await openRemove();

    expect(screen.getByText("Remove and don't pick this video again?")).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: "Don't pick this video again for this song" })).toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(bodyOf('DELETE', '/musicvideo/song/12?blocklist=true')).toBeDefined());
    expect(await screen.findByText('Removed the music video')).toBeInTheDocument();
  });

  it('removes without the blocklist when it is unticked', async () => {
    const user = await openRemove();

    await user.click(screen.getByRole('checkbox', { name: "Don't pick this video again for this song" }));
    await user.click(screen.getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(bodyOf('DELETE', '/musicvideo/song/12?blocklist=false')).toBeDefined());
  });

  it('shows the reason when a download is in progress', async () => {
    // Replace the DELETE's answer with the API's 409.
    const user = userEvent.setup();

    installFetch((url, init) => {
      const method = init?.method ?? 'GET';

      if (method === 'DELETE') {
        return jsonResponse({ title: 'Download in progress', detail: 'A download is in progress' }, 409);
      }

      return /\/musicvideo\/song\/12$/.test(url) ? jsonResponse(PRESENT_VIDEO) : new Response('', { status: 404 });
    });

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider env="test">
        <ApiProvider config={createTestConfig()}>
          <QueryClientProvider client={queryClient}>
            <MusicVideoCell song={song({ status: 'present', height: 1080, failureReason: null })} />
          </QueryClientProvider>
        </ApiProvider>
      </MantineProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));
    await user.click(await screen.findByRole('button', { name: 'Remove' }));
    await user.click(screen.getByRole('button', { name: 'Remove' }));

    expect(await screen.findByText('A download is in progress')).toBeInTheDocument();
  });
});
