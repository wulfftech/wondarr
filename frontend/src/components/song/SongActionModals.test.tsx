import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within, type RenderResult } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../../api/ApiProvider';
import type { SongResource } from '../../api/songs';
import { ALBUM_OPTIONS, HEALTH_ENTRIES, LIBRARIES, LIBRARY_SONGS, SYSTEM_STATUS } from '../../test/fixtures';
import { createTestConfig, installFetch, jsonResponse, type FetchMock } from '../../test/helpers';
import { theme } from '../../theme/theme';
import { ChangeAlbumModal } from './ChangeAlbumModal';
import { ConvertSongModal } from './ConvertSongModal';
import { DeleteSongModal } from './DeleteSongModal';
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
  albumChoice?: () => Response;
  move?: () => Response;
  remove?: () => Response;
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

    if (/\/api\/v1\/song\/\d+\/albumcontexts$/.test(url)) {
      return jsonResponse(ALBUM_OPTIONS);
    }

    if (/\/api\/v1\/song\/\d+\/albumcontext$/.test(url)) {
      return (routes.albumChoice ?? (() => jsonResponse(SONG)))();
    }

    if (url.includes('/api/v1/song/convert/preview')) {
      return jsonResponse(CONVERT_PLAN);
    }

    if (url.includes('/api/v1/song/convert')) {
      return jsonResponse(CONVERT_ACCEPTED, 202);
    }

    if (url.includes('/api/v1/song/move')) {
      return (routes.move ?? (() => jsonResponse(MOVE_ACCEPTED, 202)))();
    }

    if (/\/api\/v1\/song\/\d+$/.test(url)) {
      return (routes.remove ?? (() => new Response(null, { status: 204 })))();
    }

    if (url.includes('/api/v1/command/')) {
      return (routes.commands ?? (() => jsonResponse(MOVE_COMMAND)))();
    }

    return new Response('not found', { status: 404 });
  });
}

/** The providers a modal needs when it is rendered without the app shell around it. */
function renderModal(
  ui: ReactElement,
  client = testClient(),
): RenderResult & { rerenderModal: (next: ReactElement) => void } {
  const wrap = (inner: ReactElement) => (
    <MantineProvider theme={theme} defaultColorScheme="auto" env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>{inner}</QueryClientProvider>
      </ApiProvider>
    </MantineProvider>
  );
  const result = render(wrap(ui));

  return { ...result, rerenderModal: (next) => result.rerender(wrap(next)) };
}

function testClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
}

/** "Get Lucky" with the file it holds, of the given source. */
function songWithFile(sourceType: string): SongResource {
  return {
    ...SONG,
    file: {
      path: '/data/music/Daft Punk/Single/Get Lucky.flac',
      codec: 'flac',
      container: 'flac',
      size: 30_000_000,
      sourceType,
    },
  } as unknown as SongResource;
}

describe('SongActionModals', () => {
  it('tells the user a managed file will move, and follows the re-file command until it finishes', async () => {
    const onClose = vi.fn();
    install({
      albumChoice: () => jsonResponse({ ...SONG, refileCommandId: 31 }),
      commands: () => jsonResponse(commandOf(31, 'Moved song 12 to its new album folder')),
    });
    const user = userEvent.setup();

    renderModal(<ChangeAlbumModal song={songWithFile('soulseek')} opened onClose={onClose} />);

    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText("The file will be moved to the new album's folder.")).toBeInTheDocument();

    await user.click(await within(dialog).findByRole('radio', { name: /^Singles/ }));

    expect(await within(dialog).findByText("The file was moved to the new album's folder.")).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain('/api/v1/song/12/albumcontext');
      expect(put?.body).toEqual({ albumKey: 'singles' });
    });

    await user.click(within(dialog).getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalled();
  });

  it('says nothing about moving a reference file and closes at once when no re-file was queued', async () => {
    const onClose = vi.fn();
    install({ albumChoice: () => jsonResponse({ ...SONG, refileCommandId: null }) });
    const user = userEvent.setup();

    renderModal(<ChangeAlbumModal song={songWithFile('reference')} opened onClose={onClose} />);

    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).queryByText(/will be moved/)).not.toBeInTheDocument();

    await user.click(await within(dialog).findByRole('radio', { name: /^Singles/ }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('keeps following a re-file that is still running after the dialog is closed', async () => {
    const onClose = vi.fn();
    let polls = 0;
    install({
      albumChoice: () => jsonResponse({ ...SONG, refileCommandId: 33 }),
      commands: () => {
        polls += 1;

        return jsonResponse(
          polls < 2
            ? { ...commandOf(33, ''), status: 'started', result: 'unknown', ended: null }
            : commandOf(33, 'Moved song 12 to its new album folder'),
        );
      },
    });
    const user = userEvent.setup();

    renderModal(<ChangeAlbumModal song={songWithFile('soulseek')} opened onClose={onClose} />);

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('radio', { name: /^Singles/ }));
    await user.click(await within(dialog).findByRole('button', { name: 'Close and let it finish' }));

    expect(onClose).toHaveBeenCalled();

    // The command is still polled with the dialog closed, until it has finished.
    await waitFor(() => expect(polls).toBeGreaterThanOrEqual(2), { timeout: 8000 });
  }, 15000);

  it('shows why the re-file failed', async () => {
    install({
      albumChoice: () => jsonResponse({ ...SONG, refileCommandId: 32 }),
      commands: () =>
        jsonResponse({
          ...commandOf(32, ''),
          status: 'failed',
          result: 'unsuccessful',
          exception: 'the song is being imported.',
        }),
    });
    const user = userEvent.setup();

    renderModal(<ChangeAlbumModal song={songWithFile('soulseek')} opened onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('radio', { name: /^Singles/ }));

    expect(await within(dialog).findByText('the song is being imported.')).toBeInTheDocument();
  });

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

  it('refreshes the song lists only once the move command has finished, not when the API answers 202', async () => {
    let polls = 0;
    install({
      libraries: () => jsonResponse([LIBRARIES[0], SECOND_LIBRARY]),
      commands: () => {
        polls += 1;

        return jsonResponse(polls === 1 ? { ...MOVE_COMMAND, status: 'started', message: 'Moving…' } : MOVE_COMMAND);
      },
    });
    const user = userEvent.setup();
    const client = testClient();
    client.setQueryData(['songs', { page: 1 }], { records: [] });
    const stale = () => client.getQueryState(['songs', { page: 1 }])?.isInvalidated === true;

    renderModal(<MoveSongModal song={SONG} opened onClose={() => {}} />, client);

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('combobox', { name: 'Library' }));
    await user.click(await screen.findByRole('option', { name: 'Singles' }));
    await user.click(within(dialog).getByRole('button', { name: 'Move' }));

    // The command is accepted and running: the lists are still current.
    expect(await screen.findByText('Moving…')).toBeInTheDocument();
    expect(stale()).toBe(false);

    expect(await screen.findByText('Moved 1 song to Singles', {}, { timeout: 5000 })).toBeInTheDocument();
    await waitFor(() => expect(stale()).toBe(true));
  });

  it('refreshes the song lists only once the conversion command has finished', async () => {
    let polls = 0;
    install({
      commands: () => {
        polls += 1;

        return jsonResponse(
          polls === 1 ? { ...CONVERT_COMMAND, status: 'started', message: 'Converting…' } : CONVERT_COMMAND,
        );
      },
    });
    const user = userEvent.setup();
    const client = testClient();
    client.setQueryData(['songs', { page: 1 }], { records: [] });
    const stale = () => client.getQueryState(['songs', { page: 1 }])?.isInvalidated === true;

    renderModal(<ConvertSongModal song={SONG} opened onClose={() => {}} />, client);

    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Convert' }));

    expect(await screen.findByText('Converting…')).toBeInTheDocument();
    expect(stale()).toBe(false);

    expect(await screen.findByText('Converted 1 song to MP3-320', {}, { timeout: 5000 })).toBeInTheDocument();
    await waitFor(() => expect(stale()).toBe(true));
  });

  it("does not show song A's delete failure when the dialog opens for song B", async () => {
    install({ remove: () => new Response(null, { status: 409 }) });
    const user = userEvent.setup();
    const songB = { ...SONG, id: 13, title: 'Instant Crush' } as unknown as SongResource;

    const { rerenderModal } = renderModal(<DeleteSongModal song={SONG} opened onClose={() => {}} />);

    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }));
    expect(await screen.findByText('The song could not be deleted.')).toBeInTheDocument();

    rerenderModal(<DeleteSongModal song={SONG} opened={false} onClose={() => {}} />);
    rerenderModal(<DeleteSongModal song={songB} opened onClose={() => {}} />);

    expect(await screen.findByText(/Instant Crush/)).toBeInTheDocument();
    expect(screen.queryByText('The song could not be deleted.')).not.toBeInTheDocument();
  });

  it("does not show song A's move failure when the dialog opens for song B", async () => {
    install({
      libraries: () => jsonResponse([LIBRARIES[0], SECOND_LIBRARY]),
      move: () => new Response(null, { status: 409 }),
    });
    const user = userEvent.setup();
    const songB = { ...SONG, id: 13, title: 'Instant Crush' } as unknown as SongResource;

    const { rerenderModal } = renderModal(<MoveSongModal song={SONG} opened onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('combobox', { name: 'Library' }));
    await user.click(await screen.findByRole('option', { name: 'Singles' }));
    await user.click(within(dialog).getByRole('button', { name: 'Move' }));
    expect(await within(dialog).findByText('The songs could not be moved.')).toBeInTheDocument();

    rerenderModal(<MoveSongModal song={SONG} opened={false} onClose={() => {}} />);
    rerenderModal(<MoveSongModal song={songB} opened onClose={() => {}} />);

    expect(await screen.findByText(/Instant Crush/)).toBeInTheDocument();
    expect(screen.queryByText('The songs could not be moved.')).not.toBeInTheDocument();
  });
});
