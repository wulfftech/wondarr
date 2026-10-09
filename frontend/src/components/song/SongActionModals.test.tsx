import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../../api/ApiProvider';
import type { SongResource } from '../../api/songs';
import { HEALTH_ENTRIES, LIBRARIES, LIBRARY_SONGS, SYSTEM_STATUS } from '../../test/fixtures';
import { createTestConfig, installFetch, jsonResponse, type FetchMock } from '../../test/helpers';
import { theme } from '../../theme/theme';
import { ConvertSongModal } from './ConvertSongModal';
import { MoveSongModal } from './MoveSongModal';

/**
 * The two per-song modals the Library page's row menu opens, rendered on their own against a mocked
 * fetch: the move posts `/api/v1/song/move`, the convert previews and then posts.
 */

/**
 * "Get Lucky", as the Library page holds it. The shared fixture is the wire shape — its `kind` is the
 * camelCase string the server sends, where the generated type says number — and predates the `pinned`
 * flag, so it is widened to the prop's type here.
 */
const SONG = {
  ...LIBRARY_SONGS[0],
  albumContext: { ...LIBRARY_SONGS[0].albumContext, pinned: true },
} as unknown as SongResource;

/** A second library, so the move modal has somewhere to move the song to. */
const SECOND_LIBRARY = { ...LIBRARIES[0], id: 2, name: 'Singles', rootPath: '/data/singles', isDefault: false };

const MOVE_ACCEPTED = { commandId: 21 };
const CONVERT_ACCEPTED = { commandId: 22 };

/** What `POST /api/v1/song/convert/preview` plans for "Get Lucky": flac to mp3, about 9 MB. */
const CONVERT_PLAN = {
  convert: 1,
  skip: 0,
  refuse: 0,
  currentSize: 30_000_000,
  estimatedSize: 9_000_000,
  songs: [
    {
      songId: 12,
      outcome: 'converted',
      reason: null,
      fromCodec: 'flac',
      toCodec: 'mp3',
      currentSize: 30_000_000,
      estimatedSize: 9_000_000,
    },
  ],
};

/** The two commands the modals poll, each finished by the time the test looks at it. */
function commandOf(id: number, message: string) {
  return {
    id,
    name: id === MOVE_ACCEPTED.commandId ? 'MoveSongs' : 'ConvertSongs',
    commandName: id === MOVE_ACCEPTED.commandId ? 'MoveSongs' : 'ConvertSongs',
    message,
    body: null,
    priority: 'normal',
    status: 'completed',
    result: 'successful',
    queued: '2026-01-08T00:00:00Z',
    started: '2026-01-08T00:00:01Z',
    ended: '2026-01-08T00:00:05Z',
    duration: '00:00:04',
    exception: null,
    trigger: 'manual',
    stateChangeTime: '2026-01-08T00:00:05Z',
  };
}

const MOVE_COMMAND = commandOf(MOVE_ACCEPTED.commandId, 'Moved 1 song to Singles');
const CONVERT_COMMAND = commandOf(CONVERT_ACCEPTED.commandId, 'Converted 1 song to MP3-320');

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` actually received; see the sibling Library page test. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** The body of the last request the page sent with this method, already parsed. */
async function lastBody(method: string): Promise<{ url: string; body: unknown } | null> {
  const found = sent()
    .filter((request) => request.method === method)
    .at(-1);

  return found === undefined ? null : { url: found.url, body: JSON.parse(await found.body) };
}

interface RouteTable {
  libraries?: () => Response;
  commands?: () => Response;
}

function install(routes: RouteTable = {}): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/library')) {
      return (routes.libraries ?? (() => jsonResponse(LIBRARIES)))();
    }

    if (url.includes('/api/v1/song/convert/preview')) {
      return jsonResponse(CONVERT_PLAN);
    }

    if (url.includes('/api/v1/song/convert')) {
      return jsonResponse(CONVERT_ACCEPTED, 202);
    }

    if (url.includes('/api/v1/song/move')) {
      return jsonResponse(MOVE_ACCEPTED, 202);
    }

    if (url.includes('/api/v1/command/')) {
      return (routes.commands ?? (() => jsonResponse(MOVE_COMMAND)))();
    }

    return new Response('not found', { status: 404 });
  });
}

/** The providers a modal needs when it is rendered without the app shell around it. */
function renderModal(ui: ReactElement): void {
  render(
    <MantineProvider theme={theme} defaultColorScheme="auto" env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } })}
        >
          {ui}
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

describe('SongActionModals', () => {
  it('moves the song to the library that was picked and shows what the command reported', async () => {
    install({ libraries: () => jsonResponse([LIBRARIES[0], SECOND_LIBRARY]) });
    const user = userEvent.setup();

    renderModal(<MoveSongModal song={SONG} opened onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('combobox', { name: 'Library' }));
    await user.click(await screen.findByRole('option', { name: 'Singles' }));

    await user.click(within(dialog).getByRole('button', { name: 'Move' }));

    expect(await screen.findByText('Moved 1 song to Singles')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song/move');
      expect(post?.body).toEqual({ songIds: [12], libraryId: 2 });
    });
  });

  it('previews what the conversion would do and then converts the file', async () => {
    install({ commands: () => jsonResponse(CONVERT_COMMAND) });
    const user = userEvent.setup();

    renderModal(<ConvertSongModal song={SONG} opened onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Preview' }));

    expect(await screen.findByText('Would convert from flac to mp3, ~9 MB.')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song/convert/preview');
      expect(post?.body).toEqual({ songIds: [12], libraryId: null, rule: null });
    });

    await user.click(within(dialog).getByRole('button', { name: 'Convert' }));

    expect(await screen.findByText('Converted 1 song to MP3-320')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      // The preview's URL also contains `/convert`, so the exact path is what is asserted.
      expect(post?.url).toMatch(/\/api\/v1\/song\/convert$/);
      expect(post?.body).toEqual({ songIds: [12], libraryId: null, rule: null });
    });
  });
});
