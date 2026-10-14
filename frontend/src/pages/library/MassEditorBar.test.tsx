import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../../api/ApiProvider';
import type { LibraryResource } from '../../api/profiles';
import { LIBRARIES } from '../../test/fixtures';
import { createTestConfig, installFetch, jsonResponse } from '../../test/helpers';
import { theme } from '../../theme/theme';
import { MassEditorBar } from './MassEditorBar';

/** The bar on its own, against a mocked fetch: a library change queues a move the bar follows. */

const SECOND_LIBRARY = { ...LIBRARIES[0], id: 2, name: 'Singles', rootPath: '/data/singles', isDefault: false };

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function command(status: string, message: string) {
  return {
    id: 40,
    name: 'MoveSongs',
    commandName: 'MoveSongs',
    message,
    body: null,
    priority: 'normal',
    status,
    result: status === 'completed' ? 'successful' : 'unknown',
    queued: '2026-01-08T00:00:00Z',
    started: '2026-01-08T00:00:01Z',
    ended: null,
    duration: null,
    exception: null,
    trigger: 'manual',
    stateChangeTime: '2026-01-08T00:00:05Z',
  };
}

describe('MassEditorBar', () => {
  it('refreshes the song lists when the library move it queued has finished, not before', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let polls = 0;
    installFetch((url, init) => {
      if (url.includes('/api/v1/song/editor') && init?.method === 'PUT') {
        return jsonResponse({ songs: [], moveCommandIds: [40] });
      }

      if (url.includes('/api/v1/command/')) {
        polls += 1;

        return jsonResponse(polls === 1 ? command('started', 'Moving') : command('completed', 'Moved'));
      }

      return new Response('not found', { status: 404 });
    });
    const user = userEvent.setup({ advanceTimers: (ms) => vi.advanceTimersByTime(ms) });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const songsKey = ['songs', { page: 1 }];
    client.setQueryData(songsKey, { records: [] });
    // The edit itself refreshes the lists once; start counting from after it.
    const libraries = [LIBRARIES[0], SECOND_LIBRARY] as unknown as LibraryResource[];

    render(
      <MantineProvider theme={theme} env="test">
        <ApiProvider config={createTestConfig()}>
          <QueryClientProvider client={client}>
            <MassEditorBar selectedIds={[1, 2]} profiles={[]} libraries={libraries} knownTags={[]} onClear={() => {}} />
          </QueryClientProvider>
        </ApiProvider>
      </MantineProvider>,
    );

    await user.click(screen.getByRole('combobox', { name: 'Library for the selection' }));
    await user.click(await screen.findByRole('option', { name: 'Singles' }));
    await user.click(screen.getByRole('button', { name: 'Apply library' }));

    expect(await screen.findByText('Moving 2 songs…')).toBeInTheDocument();
    // Forget the invalidation the PUT's own success made, to see what the finished command does.
    client.setQueryData(songsKey, { records: [] }, { updatedAt: Date.now() });
    expect(client.getQueryState(songsKey)?.isInvalidated).toBe(false);

    await vi.advanceTimersByTimeAsync(1600);
    expect(await within(document.body).findByText('Moved 2 songs.')).toBeInTheDocument();
    expect(client.getQueryState(songsKey)?.isInvalidated).toBe(true);
  });

  it.each(['cancelled', 'orphaned'])('stops polling a library move that ended %s and reports it', async (status) => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let polls = 0;
    installFetch((url, init) => {
      if (url.includes('/api/v1/song/editor') && init?.method === 'PUT') {
        return jsonResponse({ songs: [], moveCommandIds: [40] });
      }

      if (url.includes('/api/v1/command/')) {
        polls += 1;

        return jsonResponse(command(status, `The move was ${status}`));
      }

      return new Response('not found', { status: 404 });
    });
    const user = userEvent.setup({ advanceTimers: (ms) => vi.advanceTimersByTime(ms) });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const libraries = [LIBRARIES[0], SECOND_LIBRARY] as unknown as LibraryResource[];

    render(
      <MantineProvider theme={theme} env="test">
        <ApiProvider config={createTestConfig()}>
          <QueryClientProvider client={client}>
            <MassEditorBar selectedIds={[1, 2]} profiles={[]} libraries={libraries} knownTags={[]} onClear={() => {}} />
          </QueryClientProvider>
        </ApiProvider>
      </MantineProvider>,
    );

    await user.click(screen.getByRole('combobox', { name: 'Library for the selection' }));
    await user.click(await screen.findByRole('option', { name: 'Singles' }));
    await user.click(screen.getByRole('button', { name: 'Apply library' }));

    expect(await screen.findByText(`The move was ${status}`)).toBeInTheDocument();
    const pollsWhenShown = polls;

    await vi.advanceTimersByTimeAsync(5000);
    expect(polls).toBe(pollsWhenShown);
  });
});
