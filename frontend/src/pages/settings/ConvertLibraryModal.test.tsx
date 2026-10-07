import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, LIBRARIES, PLEX_STATE_SIGNED_OUT, SYSTEM_STATUS } from '../../test/fixtures';
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

/**
 * The dry run for a library whose rules re-encode two of its four files: one conversion that keeps
 * its size, one that shrinks it, a file already in the target format, and a song with no file.
 */
const CONVERT_PLAN = {
  convert: 2,
  skip: 1,
  refuse: 1,
  currentSize: 3 * 1024 ** 3,
  estimatedSize: 1024 ** 3,
  songs: [
    {
      songId: 12,
      outcome: 'convert',
      reason: 'A YouTube download the rules turn into AAC',
      fromCodec: 'opus',
      toCodec: 'aac',
      currentSize: 2 * 1024 ** 3,
      estimatedSize: 512 * 1024 ** 2,
    },
    {
      songId: 13,
      outcome: 'convert',
      reason: 'A lossless file the rules re-encode',
      fromCodec: 'flac',
      toCodec: 'aac',
      currentSize: 1024 ** 3,
      estimatedSize: 512 * 1024 ** 2,
    },
    {
      songId: 14,
      outcome: 'skip',
      reason: 'Already AAC',
      fromCodec: 'aac',
      toCodec: 'aac',
      currentSize: 0,
      estimatedSize: 0,
    },
    {
      songId: 15,
      outcome: 'refuse',
      reason: 'No file stored for the song',
      fromCodec: null,
      toCodec: null,
      currentSize: 0,
      estimatedSize: 0,
    },
  ],
};

/** One command resource, as `GET /api/v1/command/{id}` sends it. */
function command(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    id: 21,
    name: 'ConvertSongs',
    commandName: 'ConvertSongs',
    message: 'Converted 2 files, skipped 1 and refused 1.',
    body: { name: 'ConvertSongs', libraryId: 1 },
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

    // The conversion plan is a preview too, so it is matched before the naming template's one.
    if (url.includes('/api/v1/song/convert/preview')) {
      return jsonResponse(options.plan ?? CONVERT_PLAN);
    }

    if (url.includes('/preview')) {
      return jsonResponse({ path: 'Music/Wondarr - Get Lucky.m4a', errors: [] });
    }

    if (url.includes('/api/v1/song/convert')) {
      return jsonResponse({ commandId: 21 }, 202);
    }

    if (url.includes('/api/v1/plex')) {
      return jsonResponse(PLEX_STATE_SIGNED_OUT);
    }

    if (url.includes('/api/v1/command/')) {
      return jsonResponse(options.command ?? command({}));
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens the Convert existing files dialog from the library's panel. */
async function openConvert(): Promise<HTMLElement> {
  const user = userEvent.setup();

  renderApp();

  await screen.findByDisplayValue('Music');
  await user.click(screen.getByRole('button', { name: 'Convert existing files…' }));

  return screen.getByRole('dialog');
}

describe('ConvertLibraryModal', () => {
  it('shows the plan: how many files move, how much room that saves, and the first rows', async () => {
    install();

    const dialog = await openConvert();

    expect(
      await within(dialog).findByText('2 files would be converted, 1 skipped, 1 refused; about 3 GB → 1 GB'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/The originals go to the recycle bin\./)).toBeInTheDocument();

    // Each row names the song, the format it leaves and the one it becomes, and why.
    const row = within(dialog).getByText('12').closest('tr');

    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText('opus → aac')).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText('A YouTube download the rules turn into AAC')).toBeInTheDocument();
    expect(within(dialog).getByText('flac → aac')).toBeInTheDocument();
    expect(within(dialog).getByText('No file stored for the song')).toBeInTheDocument();
  });

  it('says there is nothing to convert and disables the button', async () => {
    install({ plan: { ...CONVERT_PLAN, convert: 0, skip: 4, refuse: 0, songs: [] } });

    const dialog = await openConvert();

    expect(
      await within(dialog).findByText('0 files would be converted, 4 skipped, 0 refused; about 3 GB → 1 GB'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('Nothing to convert — every file already matches the rules.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Convert' })).toBeDisabled();
  });

  it('queues the conversion for the whole library and shows what the command finished with', async () => {
    install();
    const user = userEvent.setup();

    const dialog = await openConvert();

    await within(dialog).findByText('2 files would be converted, 1 skipped, 1 refused; about 3 GB → 1 GB');
    await user.click(within(dialog).getByRole('button', { name: 'Convert' }));

    await waitFor(async () => {
      const post = sent().find(
        (request) =>
          request.method === 'POST' && request.url.includes('/api/v1/song/convert') && !request.url.includes('/preview'),
      );

      expect(post).toBeDefined();
      expect(JSON.parse((await post?.body) ?? '{}')).toEqual({ songIds: null, libraryId: 1, rule: null });
    });

    expect(await within(dialog).findByText('Converted 2 files, skipped 1 and refused 1.')).toBeInTheDocument();
  });

  it('shows the error a failed command ended with', async () => {
    install({
      command: command({
        status: 'failed',
        result: 'failed',
        message: 'The command failed',
        exception: 'A file could not be re-encoded.',
      }),
    });
    const user = userEvent.setup();

    const dialog = await openConvert();

    await within(dialog).findByText('2 files would be converted, 1 skipped, 1 refused; about 3 GB → 1 GB');
    await user.click(within(dialog).getByRole('button', { name: 'Convert' }));

    expect(await within(dialog).findByText('A file could not be re-encoded.')).toBeInTheDocument();
  });
});
