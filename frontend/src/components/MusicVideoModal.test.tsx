import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import type { SongResource } from '../api/songs';
import { LIBRARY_SONGS } from '../test/fixtures';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { MusicVideoCell, musicVideoColumn } from './MusicVideoCell';

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
    artistNamed: true,
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
    artistNamed: false,
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

    if (/\/musicvideo\/song\/12\/search(\?.*)?$/.test(url)) {
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

    expect(await screen.findByText('Waiting for another download to finish')).toBeInTheDocument();
  });

  it('names the video that is queued, with its thumbnail and a link, and says what it waits for', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'queued', height: null, failureReason: null },
      { video: { ...PRESENT_VIDEO, status: 'queued', videoTitle: 'Daft Punk - Get Lucky (Official Video)' } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky is downloading' }));

    expect(await screen.findByText('Daft Punk - Get Lucky (Official Video)')).toBeInTheDocument();
    expect(screen.getByText('Waiting for another download to finish')).toBeInTheDocument();
    expect(document.querySelector('img[src="https://i.ytimg.com/vi/h5EofwRzit0/hqdefault.jpg"]')).not.toBeNull();
    expect(screen.getByRole('link', { name: /Open on YouTube/ })).toHaveAttribute(
      'href',
      'https://www.youtube.com/watch?v=h5EofwRzit0',
    );
  });

  it('names the video that is downloading and says it is downloading', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'downloading', height: null, failureReason: null },
      { video: { ...PRESENT_VIDEO, status: 'downloading', videoTitle: 'Daft Punk - Get Lucky (Official Video)' } },
    );

    await user.hover(screen.getByRole('button', { name: 'Music video for Get Lucky is downloading' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Downloading');

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky is downloading' }));

    expect(await screen.findByText('Daft Punk - Get Lucky (Official Video)')).toBeInTheDocument();
    expect(screen.getByText('Downloading…')).toBeInTheDocument();
  });

  it('says in the tooltip that a queued video is waiting to download', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'queued', height: null, failureReason: null },
      { video: { ...PRESENT_VIDEO, status: 'queued' } },
    );

    await user.hover(screen.getByRole('button', { name: 'Music video for Get Lucky is downloading' }));

    expect(await screen.findByRole('tooltip')).toHaveTextContent('Waiting to download');
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
    expect(screen.queryByText(/Low quality/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Replace…' })).toBeInTheDocument();
  });

  it('shows the thumbnail, the title, the length and a YouTube link for a present video', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'present', height: 1080, failureReason: null },
      { video: { ...PRESENT_VIDEO, videoTitle: 'Daft Punk - Get Lucky (Official Video)' } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));

    expect(await screen.findByText('Daft Punk - Get Lucky (Official Video)')).toBeInTheDocument();
    expect(screen.getByText('4:08')).toBeInTheDocument();
    expect(document.querySelector('img[src="https://i.ytimg.com/vi/h5EofwRzit0/hqdefault.jpg"]')).not.toBeNull();
    expect(screen.getByRole('link', { name: /Open on YouTube/ })).toHaveAttribute(
      'href',
      'https://www.youtube.com/watch?v=h5EofwRzit0',
    );
  });

  it('leaves the codecs out when they are not known', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'present', height: 1080, failureReason: null },
      { video: { ...PRESENT_VIDEO, videoCodec: null, audioCodec: null } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));

    expect(await screen.findByText('1920×1080')).toBeInTheDocument();
    expect(screen.queryByText('Codecs')).not.toBeInTheDocument();
  });

  it('marks a video under 720p as low quality and offers a better one, which searches without downloading', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'present', height: 480, failureReason: null },
      { video: { ...PRESENT_VIDEO, width: 854, height: 480 } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));

    expect(await screen.findByText('Low quality (480p)')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Find a better one' }));

    expect(await screen.findByText('Get Lucky (Official Video)')).toBeInTheDocument();
    expect(bodyOf('POST', '/musicvideo/song/12/search?autoDownload=false')).toBeDefined();
  });
});

describe('a failed music video', () => {
  async function openFailed(video: unknown = { ...FAILED_VIDEO, videoTitle: 'Get Lucky (fan upload)' }) {
    const user = userEvent.setup();

    renderCell({ status: 'failed', height: null, failureReason: 'The video folder has no marker file' }, { video });

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky failed' }));

    return user;
  }

  it('opens on the reason and the video it tried, and starts nothing', async () => {
    await openFailed();

    // The icon's tooltip may still be up and says the same, so the reason is looked for in the dialog's alert.
    expect(
      await within(await screen.findByRole('alert')).findByText('The video folder has no marker file'),
    ).toBeInTheDocument();
    expect(screen.getByText('Get Lucky (fan upload)')).toBeInTheDocument();
    expect(document.querySelector('img[src="https://i.ytimg.com/vi/h5EofwRzit0/hqdefault.jpg"]')).not.toBeNull();
    expect(screen.getByRole('link', { name: /Open on YouTube/ })).toHaveAttribute(
      'href',
      'https://www.youtube.com/watch?v=h5EofwRzit0',
    );
    expect(screen.getByRole('button', { name: 'Search again' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pick another…' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeInTheDocument();

    // Nothing was searched, queued or grabbed by opening it.
    expect(bodyOf('POST', '/musicvideo/song/12/search')).toBeUndefined();
    expect(bodyOf('POST', '/musicvideo/song/12/search?autoDownload=false')).toBeUndefined();
    expect(bodyOf('POST', '/musicvideo/song/12/grab')).toBeUndefined();
  });

  it('searches again as a search does, so a confident match is downloaded', async () => {
    const user = await openFailed();

    await user.click(await screen.findByRole('button', { name: 'Search again' }));

    await waitFor(() => expect(bodyOf('POST', '/musicvideo/song/12/search')).toBeDefined());
    expect(bodyOf('POST', '/musicvideo/song/12/search?autoDownload=false')).toBeUndefined();
  });

  it('lists the candidates without downloading when asked to pick another', async () => {
    const user = await openFailed();

    await user.click(await screen.findByRole('button', { name: 'Pick another…' }));

    expect(await screen.findByText('Get Lucky (Official Video)')).toBeInTheDocument();
    expect(bodyOf('POST', '/musicvideo/song/12/search?autoDownload=false')).toBeDefined();
    expect(bodyOf('POST', '/musicvideo/song/12/grab')).toBeUndefined();
  });

  it('removes the failed row without blocklisting the video', async () => {
    const user = await openFailed();

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(bodyOf('DELETE', '/musicvideo/song/12?blocklist=false')).toBeDefined());
  });

  it('says so when no reason was recorded', async () => {
    await openFailed({ ...FAILED_VIDEO, failureReason: null });

    expect(await screen.findByText('No reason was given.')).toBeInTheDocument();
  });
});

describe('the Video column header', () => {
  it('has a visible text label beside the icon', () => {
    const column = musicVideoColumn();

    render(<MantineProvider env="test">{column.header}</MantineProvider>);

    expect(screen.getByText('Video')).toBeVisible();
    expect(screen.getByLabelText('Music video')).toBeInTheDocument();
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

  it('names the video in the toast when the match was confident', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: true, partial: false, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Downloading «Get Lucky (Official Video)»')).toBeInTheDocument();
    expect(screen.queryByText('Music video for Get Lucky')).not.toBeInTheDocument();
  });

  it('says how long the song is and how far each candidate is from it', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    // Get Lucky is 6:09 in the fixture; the first candidate is 4:08, which is 2:01 short.
    expect(await screen.findByText('Song length 6:09')).toBeInTheDocument();
    expect(screen.getByText(/Daft Punk · 4:08 \(-2:01\)/)).toBeInTheDocument();
    // A candidate with no length has no difference to show.
    expect(screen.getByText(/No artist credited · —$/)).toBeInTheDocument();
  });

  it('marks the top candidate: Confident match when confident, Best match when official or artist-named', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Confident match')).toBeInTheDocument();
    expect(screen.queryByText('Best match')).not.toBeInTheDocument();
  });

  it('calls an unconfident top candidate a best match when it is the official video or names the artist', async () => {
    const user = userEvent.setup();
    const [first] = CANDIDATES;

    renderCell(null, {
      search: { queued: false, candidates: [{ ...first, confident: false }, CANDIDATES[1]] },
    });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Best match')).toBeInTheDocument();
    expect(screen.queryByText('Confident match')).not.toBeInTheDocument();
  });

  it('gives no badge to a top candidate that is neither official nor artist-named', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: [CANDIDATES[1]] } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Get Lucky (fan upload)')).toBeInTheDocument();
    expect(screen.queryByText('Best match')).not.toBeInTheDocument();
    expect(screen.queryByText('Confident match')).not.toBeInTheDocument();
  });

  it('shows the first 8 candidates and the rest after Show more', async () => {
    const user = userEvent.setup();
    const many = Array.from({ length: 11 }, (_, index) => ({
      ...CANDIDATES[1],
      videoId: `VIDEOID${String(index).padStart(4, '0')}`,
      title: `Fan upload ${String(index + 1)}`,
      confident: false,
    }));

    renderCell(null, { search: { queued: false, candidates: many } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Fan upload 8')).toBeInTheDocument();
    expect(screen.queryByText('Fan upload 9')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show more (3)' }));

    expect(screen.getByText('Fan upload 11')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Show more/ })).not.toBeInTheDocument();
  });

  it('keeps Back to the current video in a sticky footer above the list', async () => {
    const user = userEvent.setup();

    renderCell(
      { status: 'present', height: 1080, failureReason: null },
      { video: PRESENT_VIDEO, search: { queued: false, candidates: CANDIDATES } },
    );

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));
    await user.click(await screen.findByRole('button', { name: 'Replace…' }));

    const back = await screen.findByRole('button', { name: 'Back to the current video' });

    expect(back.parentElement).toHaveStyle({ position: 'sticky' });

    await user.click(back);

    expect(await screen.findByText('1920×1080')).toBeInTheDocument();
  });

  it('opens the candidates when nothing was confident, and downloads the one picked', async () => {
    const user = userEvent.setup();

    renderCell(null, { search: { queued: false, candidates: CANDIDATES } });

    await user.click(screen.getByRole('button', { name: 'Find music video for Get Lucky' }));

    expect(await screen.findByText('Music video for Get Lucky')).toBeInTheDocument();
    expect(screen.getByText('Get Lucky (Official Video)')).toBeInTheDocument();
    expect(screen.getByText('Official video')).toBeInTheDocument();
    expect(screen.getByText('Confident match')).toBeInTheDocument();
    expect(screen.getByText('From MusicBrainz')).toBeInTheDocument();
    expect(screen.getByText('Upload')).toBeInTheDocument();
    expect(screen.getByText(/Daft Punk · 4:08/)).toBeInTheDocument();

    const link = screen.getByRole('link', { name: 'Open Get Lucky (Official Video) on YouTube' });

    expect(link).toHaveAttribute('href', 'https://www.youtube.com/watch?v=h5EofwRzit0');
    expect(link).toHaveAttribute('target', '_blank');

    await user.click(screen.getByRole('button', { name: 'Download Get Lucky (Official Video)' }));

    await waitFor(() => expect(bodyOf('POST', '/musicvideo/song/12/grab')).toBeDefined());

    const request = bodyOf('POST', '/musicvideo/song/12/grab');

    expect(await request?.clone().json()).toEqual({
      youTubeVideoId: 'h5EofwRzit0',
      videoTitle: 'Get Lucky (Official Video)',
    });
    expect(await screen.findByText('Downloading «Get Lucky (Official Video)»')).toBeInTheDocument();
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

describe('a video found in the folder', () => {
  // The scan adopts it where it is, so there is no YouTube video behind it.
  const ADOPTED_VIDEO = { ...PRESENT_VIDEO, youTubeVideoId: null, foundBy: 'existing' };

  async function openAdopted() {
    const user = userEvent.setup();

    renderCell({ status: 'present', height: 1080, failureReason: null }, { video: ADOPTED_VIDEO });

    await user.click(screen.getByRole('button', { name: 'Music video for Get Lucky' }));

    return user;
  }

  it('says it was found in the folder and builds no YouTube link', async () => {
    await openAdopted();

    expect(await screen.findByText('Found in your folder')).toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('explains that Remove recycles the file and the scan will not pick it up again', async () => {
    const user = await openAdopted();

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    expect(screen.getByText(/goes to the recycle bin/)).toBeInTheDocument();
    expect(screen.getByText(/won.t pick it up again/)).toBeInTheDocument();
    // There is no YouTube id to blocklist, so there is no box for it.
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(bodyOf('DELETE', '/musicvideo/song/12?blocklist=false')).toBeDefined());
  });
});
