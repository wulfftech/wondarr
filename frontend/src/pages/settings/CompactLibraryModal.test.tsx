import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  COMPACT_PLAN,
  COMPACT_PLAN_EMPTY,
  HEALTH_ENTRIES,
  LIBRARIES,
  PLEX_STATE_SIGNED_OUT,
  SYSTEM_STATUS,
} from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/library');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** One command resource, as `GET /api/v1/command/{id}` sends it. */
function command(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    id: 21,
    name: 'CompactLibrary',
    commandName: 'CompactLibrary',
    message: 'Moved 2 files into 2 albums',
    body: { name: 'CompactLibrary', libraryId: 1 },
    priority: 'normal',
    status: 'completed',
    result: 'successful',
    queued: '2026-01-09T00:00:00Z',
    started: '2026-01-09T00:00:01Z',
    ended: '2026-01-09T00:00:03Z',
    duration: '00:00:02',
    exception: null,
    trigger: 'manual',
    stateChangeTime: '2026-01-09T00:00:03Z',
    ...overrides,
  };
}

/** The route table the library page and the dialog need. */
function install(options: { plan?: unknown; command?: Record<string, unknown> } = {}): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/preview')) {
      return jsonResponse({ path: 'Music/Wondarr - Get Lucky.m4a', errors: [] });
    }

    if (url.includes('/compact')) {
      return jsonResponse(options.plan ?? COMPACT_PLAN);
    }

    if (url.includes('/api/v1/plex')) {
      return jsonResponse(PLEX_STATE_SIGNED_OUT);
    }

    if (url.includes('/api/v1/command/')) {
      return jsonResponse(options.command ?? command({}));
    }

    if (url.includes('/api/v1/command')) {
      return jsonResponse(command({ status: 'queued', message: 'Queued', result: 'unknown' }), 201);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens the Compact library dialog from the library card. */
async function openCompact(): Promise<HTMLElement> {
  const user = userEvent.setup();

  renderApp();

  await screen.findByDisplayValue('Music');
  await user.click(screen.getByRole('button', { name: 'Compact library…' }));

  return screen.getByRole('dialog');
}

describe('CompactLibraryModal', () => {
  it('shows the album counts and every move, with the paths a file takes', async () => {
    install();

    const dialog = await openCompact();

    expect(await within(dialog).findByText('4 albums → 2 albums, 2 songs move')).toBeInTheDocument();
    expect(
      within(dialog).getByText(/Re-plans every song's album under this library's album policy/),
    ).toBeInTheDocument();

    // The file move carries both paths; the album-only move says so instead.
    expect(within(dialog).getByText('/data/music/Daft Punk/Singles/08 - Get Lucky.flac')).toBeInTheDocument();
    expect(
      within(dialog).getByText('/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac'),
    ).toBeInTheDocument();
    // The song with no file says so on both sides rather than showing a path.
    expect(within(dialog).getAllByText('album only')).toHaveLength(2);
    expect(within(dialog).getByText('Daft Punk — Random Access Memories')).toBeInTheDocument();
  });

  it('says there is nothing to do and disables the run button', async () => {
    install({ plan: COMPACT_PLAN_EMPTY });

    const dialog = await openCompact();

    expect(await within(dialog).findByText('2 albums → 2 albums, 0 songs move')).toBeInTheDocument();
    expect(
      within(dialog).getByText('Nothing to compact — every song is already on the album the policy would choose.'),
    ).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Run compaction' })).toBeDisabled();
  });

  it('confirms, queues the flat command and shows what it finished with', async () => {
    install();
    const user = userEvent.setup();

    const dialog = await openCompact();

    await within(dialog).findByText('4 albums → 2 albums, 2 songs move');
    await user.click(within(dialog).getByRole('button', { name: 'Run compaction' }));

    expect(within(dialog).getByText('Move 2 files? This rescans the linked Plex library.')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Run compaction' }));

    await waitFor(async () => {
      const post = sent().find((request) => request.method === 'POST' && request.url.includes('/api/v1/command'));

      expect(post).toBeDefined();
      expect(JSON.parse((await post?.body) ?? '{}')).toEqual({ name: 'CompactLibrary', libraryId: 1 });
    });

    expect(await within(dialog).findByText('Moved 2 files into 2 albums')).toBeInTheDocument();
  });

  it('shows the error a failed command ended with', async () => {
    install({
      command: command({
        status: 'failed',
        result: 'failed',
        message: 'The command failed',
        exception: 'A file could not be moved.',
      }),
    });
    const user = userEvent.setup();

    const dialog = await openCompact();

    await within(dialog).findByText('4 albums → 2 albums, 2 songs move');
    await user.click(within(dialog).getByRole('button', { name: 'Run compaction' }));
    await user.click(within(dialog).getByRole('button', { name: 'Run compaction' }));

    expect(await within(dialog).findByText('A file could not be moved.')).toBeInTheDocument();
  });
});
