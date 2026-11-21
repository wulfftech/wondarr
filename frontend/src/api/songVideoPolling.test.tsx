import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MusicVideoCell } from '../components/MusicVideoCell';
import { LIBRARY_SONGS } from '../test/fixtures';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { ApiProvider } from './ApiProvider';
import { useSongs, type SongPage, type SongResource } from './songs';
import { pollWhileVideosBusy, SONG_VIDEO_POLL_INTERVAL_MS, SONG_VIDEO_STALE_AFTER_MS } from './wanted';

afterEach(() => {
  vi.unstubAllGlobals();
});

function page(video: unknown): SongPage {
  const record = { ...LIBRARY_SONGS[0], musicVideo: video } as unknown as SongResource;

  return {
    page: 1,
    pageSize: 25,
    sortKey: 'title',
    sortDirection: 'ascending',
    totalRecords: 1,
    records: [record],
  };
}

describe('polling a song list while a video is on its way', () => {
  it('asks again every 5 seconds while a row is queued or downloading', () => {
    expect(SONG_VIDEO_POLL_INTERVAL_MS).toBe(5000);
    expect(
      pollWhileVideosBusy({ state: { data: page({ status: 'queued', height: null, failureReason: null }) } }),
    ).toBe(5000);
    expect(
      pollWhileVideosBusy({ state: { data: page({ status: 'downloading', height: null, failureReason: null }) } }),
    ).toBe(5000);
  });

  it('stops once every row has settled, and when no row has a video', () => {
    expect(
      pollWhileVideosBusy({ state: { data: page({ status: 'present', height: 1080, failureReason: null }) } }),
    ).toBe(false);
    expect(pollWhileVideosBusy({ state: { data: page({ status: 'failed', height: null, failureReason: 'x' }) } })).toBe(
      false,
    );
    expect(pollWhileVideosBusy({ state: { data: page(null) } })).toBe(false);
    expect(pollWhileVideosBusy({ state: { data: undefined } })).toBe(false);
  });

  it('does not keep polling for a row that has not changed in over 30 minutes', () => {
    const now = Date.parse('2026-11-21T12:00:00Z');
    const stale = { status: 'queued', height: null, failureReason: null, updatedAt: '2026-11-21T11:20:00Z' };
    const fresh = { status: 'queued', height: null, failureReason: null, updatedAt: '2026-11-21T11:45:00Z' };

    expect(SONG_VIDEO_STALE_AFTER_MS).toBe(30 * 60 * 1000);
    expect(pollWhileVideosBusy({ state: { data: page(stale) } }, now)).toBe(false);
    expect(pollWhileVideosBusy({ state: { data: page({ ...stale, status: 'downloading' }) } }, now)).toBe(false);
    expect(pollWhileVideosBusy({ state: { data: page(fresh) } }, now)).toBe(5000);
  });

  it('moves a row from queued to present by itself, with no dialog open', async () => {
    let calls = 0;

    installFetch((url) => {
      if (url.includes('/api/v1/song')) {
        calls += 1;

        return jsonResponse(
          calls === 1
            ? page({ status: 'queued', height: null, failureReason: null })
            : page({ status: 'present', height: 1080, failureReason: null }),
        );
      }

      return new Response('not found', { status: 404 });
    });

    function Rows() {
      const songs = useSongs({ page: 1, pageSize: 25, sortKey: 'title', sortDirection: 'ascending' });

      return (
        <>
          {songs.data?.records.map((song) => (
            <MusicVideoCell key={String(song.id)} song={song} />
          ))}
        </>
      );
    }

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider env="test">
        <ApiProvider config={createTestConfig()}>
          <QueryClientProvider client={queryClient}>
            <Rows />
          </QueryClientProvider>
        </ApiProvider>
      </MantineProvider>,
    );

    expect(await screen.findByRole('button', { name: 'Music video for Get Lucky is queued' })).toBeInTheDocument();

    // The next read is five seconds away.
    expect(
      await screen.findByRole('button', { name: 'Music video for Get Lucky' }, { timeout: 9000 }),
    ).toBeInTheDocument();
    expect(calls).toBeGreaterThanOrEqual(2);
  }, 15000);
});
