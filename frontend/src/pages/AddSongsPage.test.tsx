import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ADDED_SONG,
  BULK_ACCEPTED,
  COMMAND_COMPLETED,
  COMMAND_RUNNING,
  HEALTH_ENTRIES,
  IMPORT_LIST,
  LIBRARIES,
  LOOKUP_RESULTS,
  QUALITY_PROFILES,
  SYSTEM_STATUS,
} from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/add');
  Element.prototype.scrollIntoView = vi.fn();
});

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
  /** The lookup's answer: the candidates, or the 400 the user has to read. */
  lookup?: () => Response;
  /** The add's answer: the stored song, or the 409 that says it is already there. */
  add?: () => Response;
  /** How many times the command has been asked about, so it can finish on the second poll. */
  commands?: () => Response;
  importList?: () => Response;
  /** The libraries songs can be filed into; two or more make the picker appear. */
  libraries?: () => Response;
}

function install(routes: RouteTable = {}): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/qualityprofile')) {
      return jsonResponse(QUALITY_PROFILES);
    }

    if (routes.libraries !== undefined && url.includes('/api/v1/library')) {
      return routes.libraries();
    }

    if (url.includes('/api/v1/song/lookup')) {
      return (routes.lookup ?? (() => jsonResponse(LOOKUP_RESULTS)))();
    }

    if (url.includes('/api/v1/song/bulk')) {
      return jsonResponse(BULK_ACCEPTED, 202);
    }

    if (url.includes('/api/v1/command/')) {
      return (routes.commands ?? (() => jsonResponse(COMMAND_COMPLETED)))();
    }

    if (url.includes('/api/v1/importlist/')) {
      return (routes.importList ?? (() => jsonResponse(IMPORT_LIST)))();
    }

    if (url.includes('/api/v1/song')) {
      return (routes.add ?? (() => jsonResponse(ADDED_SONG, 201)))();
    }

    return new Response('not found', { status: 404 });
  });
}

/**
 * Types a term into the search box and submits it with Enter. The box is found by role because the
 * tab panel is itself labelled "Search" and would also answer a label query.
 */
async function search(user: ReturnType<typeof userEvent.setup>, term: string): Promise<void> {
  await user.type(screen.getByRole('textbox', { name: 'Search' }), `${term}{Enter}`);
}

describe('AddSongsPage', () => {
  it('looks a term up and shows what tells the candidates apart', async () => {
    install();
    const user = userEvent.setup();

    renderApp();
    await search(user, 'Daft Punk - Get Lucky');

    expect(await screen.findByText('album version')).toBeInTheDocument();
    // 369000 ms reads as 6:09.
    expect(screen.getByText('6:09')).toBeInTheDocument();
    expect(screen.getByText('MusicBrainz')).toBeInTheDocument();
    expect(screen.getByText('Deezer only')).toBeInTheDocument();
    expect(screen.getByText('Album')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song/lookup');
      expect(post?.body).toEqual({ term: 'Daft Punk - Get Lucky' });
    });
  });

  it('adds a candidate by its recording id and flips the row to in library', async () => {
    install();
    const user = userEvent.setup();

    renderApp();
    await search(user, 'Daft Punk - Get Lucky');
    await screen.findByText('album version');

    await user.click(screen.getAllByRole('button', { name: 'Add' })[0]);

    expect(await screen.findByText('In library')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song');
      expect(post?.body).toMatchObject({ mbRecordingId: '833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d' });
    });
  });

  it('offers the library picker once there are two libraries and sends the chosen one', async () => {
    install({
      libraries: () =>
        jsonResponse([
          LIBRARIES[0],
          { ...LIBRARIES[0], id: 2, name: 'Singles', rootPath: '/data/singles', isDefault: false },
        ]),
    });
    const user = userEvent.setup();

    renderApp();
    await search(user, 'Daft Punk - Get Lucky');
    await screen.findByText('album version');

    // The default library is the picker's starting value, and both are offered. The paste tab has
    // its own picker, so the search panel's is the one read here.
    const panel = screen.getByRole('tabpanel', { name: 'Search' });
    const select = await within(panel).findByLabelText('Library', { selector: 'input' });

    expect(select).toHaveValue('Music');

    await user.click(select);

    const listbox = document.getElementById(select.getAttribute('aria-controls') ?? '');

    await user.click(within(listbox as HTMLElement).getByText('Singles'));
    await user.click(within(panel).getAllByRole('button', { name: 'Add' })[0]);

    expect(await screen.findByText('In library')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song');
      expect(post?.body).toMatchObject({
        mbRecordingId: '833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d',
        libraryId: 2,
      });
    });
  });

  it('flips the row to in library when the API answers 409', async () => {
    install({
      add: () =>
        jsonResponse(
          { title: 'Song already exists', detail: 'The library already holds this song as 41.', songId: 41 },
          409,
        ),
    });
    const user = userEvent.setup();

    renderApp();
    await search(user, 'Daft Punk - Get Lucky');
    await screen.findByText('album version');

    await user.click(screen.getAllByRole('button', { name: 'Add' })[0]);

    expect(await screen.findByText('In library')).toBeInTheDocument();
  });

  it('shows the detail when the lookup refuses the term', async () => {
    install({
      lookup: () => jsonResponse({ title: 'Unsupported lookup', detail: 'A Spotify link cannot be looked up.' }, 400),
    });
    const user = userEvent.setup();

    renderApp();
    await search(user, 'https://open.spotify.com/track/abc');

    expect(await screen.findByText('A Spotify link cannot be looked up.')).toBeInTheDocument();
  });

  it('counts the pasted lines, resolves them and offers the review', async () => {
    let polls = 0;
    const mock = install({
      commands: () => {
        polls += 1;

        return jsonResponse(polls === 1 ? COMMAND_RUNNING : COMMAND_COMPLETED);
      },
      // As on the real server: until the command has run, every line of the list is still pending.
      importList: () =>
        jsonResponse(
          polls < 2 ? { ...IMPORT_LIST, counts: { pending: 50, added: 0, unresolved: 0, skipped: 0 } } : IMPORT_LIST,
        ),
    });
    const user = userEvent.setup();

    renderApp();
    await user.click(screen.getByRole('tab', { name: 'Paste a list' }));

    const textarea = await screen.findByLabelText('Songs');

    await user.type(textarea, 'Daft Punk - Get Lucky\nAphex Twin - Xtal');

    expect(await screen.findByText('2 lines')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add all' }));

    expect(await screen.findByText('Resolved 12 of 50 lines')).toBeInTheDocument();

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('/api/v1/song/bulk'))).toBe(true);
    });

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/song/bulk');
      expect(post?.body).toMatchObject({ text: 'Daft Punk - Get Lucky\nAphex Twin - Xtal' });
    });

    // The command is polled until it stops running; then the list's counts are shown.
    expect(await screen.findByText('Resolved 50 of 50 lines', {}, { timeout: 5000 })).toBeInTheDocument();
    expect(await screen.findByText('Added, including ones the library already held: 47')).toBeInTheDocument();
    expect(screen.getByText('Unresolved: 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review 3 unresolved' })).toBeInTheDocument();
  });
});
