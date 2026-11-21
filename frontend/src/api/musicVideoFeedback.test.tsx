import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { ApiProvider } from './ApiProvider';
import {
  BEST_AVAILABLE_FRESH_MS,
  downloadMessage,
  nothingBetterOnYouTube,
  useDeleteMusicVideo,
  useGrabMusicVideo,
  useSearchMusicVideo,
  type MusicVideoResource,
} from './musicVideos';

afterEach(() => {
  vi.unstubAllGlobals();
  // The toast store outlives a test, so one test's toast would answer the next test's query.
  notifications.clean();
});

/** A button that grabs a video. Changing `epoch` remounts it, which drops a per-call callback but not a hook's own. */
function GrabButton({ epoch }: { epoch: number }) {
  const grab = useGrabMusicVideo();

  return (
    <button
      type="button"
      data-epoch={epoch}
      onClick={() =>
        grab.mutate({ songId: 672, videoId: 'VMnjF1O4eH0', videoTitle: 'Queen - Fat Bottomed Girls (Official Video)' })
      }
    >
      Grab
    </button>
  );
}

function SearchAndDeleteButtons({ epoch }: { epoch: number }) {
  const search = useSearchMusicVideo();
  const remove = useDeleteMusicVideo();

  return (
    <>
      <button type="button" data-epoch={epoch} onClick={() => search.mutate(672)}>
        Search
      </button>
      <button type="button" onClick={() => remove.mutate({ songId: 672, blocklist: true })}>
        Remove
      </button>
    </>
  );
}

/** Renders `Child` and a button that remounts it (a new `key`), the way a view swap does while a request is out. */
function Harness({ child }: { child: (epoch: number) => ReactNode }) {
  const [epoch, setEpoch] = useState(0);

  return (
    <>
      <button type="button" onClick={() => setEpoch((current) => current + 1)}>
        Remount
      </button>
      <div key={epoch}>{child(epoch)}</div>
    </>
  );
}

function renderHarness(child: (epoch: number) => ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider env="test">
      <Notifications />
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={queryClient}>
          <Harness child={child} />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

/** A request that stays open until the test lets it answer. */
function held() {
  let release: (response: Response) => void = () => undefined;
  const response = new Promise<Response>((resolve) => {
    release = resolve;
  });

  return { response, release: (answer: Response) => release(answer) };
}

describe('the feedback of a music video action', () => {
  it('still appears when the view that asked is remounted before the request settles', async () => {
    const user = userEvent.setup();
    const gate = held();

    installFetch((url) =>
      url.endsWith('/musicvideo/song/672/grab') ? gate.response : new Response('', { status: 404 }),
    );
    renderHarness((epoch) => <GrabButton epoch={epoch} />);

    await user.click(screen.getByRole('button', { name: 'Grab' }));
    // The hook's own onSuccess invalidates queries, which swaps the view: here the view is swapped by hand.
    await user.click(screen.getByRole('button', { name: 'Remount' }));
    gate.release(jsonResponse({ commandId: 9, status: 'queued', aheadInQueue: 1 }, 202));

    expect(
      await screen.findByText('Queued «Queen - Fat Bottomed Girls (Official Video)» behind 1 other download'),
    ).toBeInTheDocument();
  });

  it('shows a 409 with its message, even after a remount', async () => {
    const user = userEvent.setup();
    const gate = held();

    installFetch((url) =>
      url.endsWith('/musicvideo/song/672/grab') ? gate.response : new Response('', { status: 404 }),
    );
    renderHarness((epoch) => <GrabButton epoch={epoch} />);

    await user.click(screen.getByRole('button', { name: 'Grab' }));
    await user.click(screen.getByRole('button', { name: 'Remount' }));
    gate.release(jsonResponse({ title: 'Download in progress', detail: 'A download is in progress' }, 409));

    expect(await screen.findByText('A download is in progress')).toBeInTheDocument();
  });

  it('shows the toast of a search that queued a download, after a remount', async () => {
    const user = userEvent.setup();
    const gate = held();

    installFetch((url) =>
      url.includes('/musicvideo/song/672/search') ? gate.response : new Response('', { status: 404 }),
    );
    renderHarness((epoch) => <SearchAndDeleteButtons epoch={epoch} />);

    await user.click(screen.getByRole('button', { name: 'Search' }));
    await user.click(screen.getByRole('button', { name: 'Remount' }));
    gate.release(
      jsonResponse({
        queued: true,
        partial: false,
        status: 'downloading',
        aheadInQueue: 0,
        candidates: [{ videoId: 'u7K72X4eo_s', title: 'Into the Unknown', confident: true }],
      }),
    );

    expect(await screen.findByText('Downloading «Into the Unknown»')).toBeInTheDocument();
  });

  it('shows a failed removal and a removal, after a remount', async () => {
    const user = userEvent.setup();
    const gate = held();

    installFetch((url, init) =>
      url.includes('/musicvideo/song/672') && init?.method === 'DELETE'
        ? gate.response
        : new Response('', { status: 404 }),
    );
    renderHarness((epoch) => <SearchAndDeleteButtons epoch={epoch} />);

    await user.click(screen.getByRole('button', { name: 'Remove' }));
    await user.click(screen.getByRole('button', { name: 'Remount' }));
    gate.release(jsonResponse({ title: 'Download in progress', detail: 'A download is in progress' }, 409));

    expect(await screen.findByText('A download is in progress')).toBeInTheDocument();
  });
});

describe('the toast for a queued download', () => {
  it('says Downloading when nothing is ahead of it, whatever the status was', () => {
    expect(downloadMessage('Into the Unknown', 'queued', 0)).toBe('Downloading «Into the Unknown»');
    expect(downloadMessage('Into the Unknown', 'queued', '0')).toBe('Downloading «Into the Unknown»');
  });

  it('says how many other downloads it waits behind', () => {
    expect(downloadMessage('Into the Unknown', 'queued', 1)).toBe('Queued «Into the Unknown» behind 1 other download');
    expect(downloadMessage('Into the Unknown', 'queued', 3)).toBe('Queued «Into the Unknown» behind 3 other downloads');
    expect(downloadMessage(undefined, 'queued', 2)).toBe('Queued music video behind 2 other downloads');
  });

  it('falls back to the status when the server does not say', () => {
    expect(downloadMessage('Into the Unknown', 'queued')).toBe('Queued «Into the Unknown»');
    expect(downloadMessage('Into the Unknown', 'downloading')).toBe('Downloading «Into the Unknown»');
  });
});

describe('whether YouTube has a better copy', () => {
  const NOW = Date.parse('2026-11-21T12:00:00Z');
  const video = (best: number | null, checkedAt: string | null, height = 360) =>
    ({ height, bestAvailableHeight: best, bestAvailableCheckedAt: checkedAt }) as unknown as MusicVideoResource;

  it('is settled by a fresh check that found nothing taller than the filed copy', () => {
    expect(nothingBetterOnYouTube(video(360, '2026-11-20T12:00:00Z'), NOW)).toBe(true);
    expect(nothingBetterOnYouTube(video(240, '2026-11-20T12:00:00Z'), NOW)).toBe(true);
  });

  it('is open again after 30 days, and when the check found something taller or never ran', () => {
    expect(BEST_AVAILABLE_FRESH_MS).toBe(30 * 24 * 60 * 60 * 1000);
    expect(nothingBetterOnYouTube(video(360, '2026-10-21T11:59:00Z'), NOW)).toBe(false);
    expect(nothingBetterOnYouTube(video(360, '2026-10-23T12:00:00Z'), NOW)).toBe(true);
    expect(nothingBetterOnYouTube(video(1080, '2026-11-20T12:00:00Z'), NOW)).toBe(false);
    expect(nothingBetterOnYouTube(video(null, null), NOW)).toBe(false);
  });
});
