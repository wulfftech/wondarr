import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ALBUM_OPTIONS,
  ARTISTS,
  HEALTH_ENTRIES,
  LIBRARIES,
  LIBRARY_SONGS,
  paged,
  QUALITY_PROFILES,
  SYSTEM_STATUS,
} from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/library');
  // jsdom has no scrollIntoView, which Mantine's combobox and menu call as they open.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * What the mocked `fetch` actually received. The generated client calls `fetch(request)`, so the
 * method and the body live on that `Request`, not on the second argument the shared helper records.
 */
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

  if (found === undefined) {
    return null;
  }

  // A DELETE carries no body at all, so there is nothing to parse.
  const text = await found.body;

  return { url: found.url, body: text === '' ? null : JSON.parse(text) };
}

/** The routes every Library test needs besides the songs: the profiles, the tags and the saved views. */
function sharedRoutes(url: string): Response | null {
  if (url.includes('/api/v1/qualityprofile')) {
    return jsonResponse(QUALITY_PROFILES);
  }

  if (url.includes('/api/v1/tag')) {
    return jsonResponse([{ label: 'rock', songCount: 2 }]);
  }

  if (url.includes('/api/v1/customfilter')) {
    return jsonResponse([]);
  }

  return null;
}

/** `count` songs with ids from 12 up, each carrying `tags`. */
function makeSongs(count: number, tags: string[] = []): (typeof LIBRARY_SONGS)[number][] {
  return Array.from({ length: count }, (_, index) => ({
    ...LIBRARY_SONGS[0],
    id: 12 + index,
    title: `Song ${String.fromCharCode(65 + index)}`,
    tags,
  }));
}

interface InstallOptions {
  songs?: unknown[];
  totalRecords?: number;
  /** Answers first: a test's own routes (the editor, the saved views) go here. */
  extra?: (url: string, method: string) => Response | null;
}

/** The route table the page needs: the shell, the artists and the paged songs. */
function install(options: InstallOptions = {}): FetchMock {
  return installFetch((url, init) => {
    const own = options.extra?.(url, init?.method ?? 'GET') ?? null;

    if (own !== null) {
      return own;
    }

    const shared = sharedRoutes(url);

    if (shared !== null) {
      return shared;
    }

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/artist')) {
      return jsonResponse(ARTISTS);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (/\/api\/v1\/song\/\d+\/albumcontexts$/.test(url)) {
      return jsonResponse(ALBUM_OPTIONS);
    }

    // The item endpoint takes the writes: the PUT that saves and the DELETE that removes.
    if (/\/api\/v1\/song\/\d+$/.test(url)) {
      return jsonResponse(LIBRARY_SONGS[0]);
    }

    if (url.includes('/api/v1/song')) {
      const songs = options.songs ?? LIBRARY_SONGS;

      return jsonResponse({ ...paged(songs), totalRecords: options.totalRecords ?? songs.length });
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens one song's row menu and hands back the dropdown. */
async function openRowMenu(user: ReturnType<typeof userEvent.setup>, title: string): Promise<void> {
  await user.click(screen.getByLabelText(`Actions for ${title}`));
}

describe('LibraryPage', () => {
  it('links each title to the song page, so it can be opened in a new tab', async () => {
    install();

    renderApp();

    expect(await screen.findByRole('link', { name: 'Get Lucky' })).toHaveAttribute('href', '/song/12');
  });

  it('keeps its filters in the URL, so they are still there after a visit to a song page', async () => {
    resetLocation('/library?tag=rock&hasFile=true');
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('link', { name: 'Get Lucky' }));
    expect(window.location.pathname).toBe('/song/12');

    window.history.back();

    await waitFor(() => expect(window.location.pathname).toBe('/library'));
    expect(window.location.search).toContain('tag=rock');
    expect(window.location.search).toContain('hasFile=true');

    await screen.findByRole('link', { name: 'Get Lucky' });

    const lastSongsCall = sent()
      .filter((request) => request.url.includes('/api/v1/song') && !/song\/\d+/.test(request.url))
      .at(-1);

    expect(lastSongsCall?.url).toContain('tag=rock');
    expect(lastSongsCall?.url).toContain('hasFile=true');
  });

  it('lists the songs with their album assignment and version flags', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Get Lucky')).toBeInTheDocument();
    // Scoped to the cell: the artist filter's select also carries the name.
    expect(screen.getByRole('cell', { name: 'Daft Punk' })).toBeInTheDocument();
    expect(screen.getByText('Random Access Memories')).toBeInTheDocument();
    // The second song is filed under its artist's Singles pseudo-album.
    expect(screen.getByText('Singles')).toBeInTheDocument();
    expect(screen.getByText('Radio edit')).toBeInTheDocument();
    // 369000 ms reads as 6:09.
    expect(screen.getByText('6:09')).toBeInTheDocument();
  });

  it('sends the monitored flag when a row switch is turned off', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Get Lucky monitored'));

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain('/api/v1/song/12');
      expect(put?.body).toMatchObject({ monitored: false });
    });
  });

  it('moves the song when an album is picked in the change-album modal', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await openRowMenu(user, 'Get Lucky');
    await user.click(await screen.findByRole('menuitem', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('radio', { name: /^Singles/ }));

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain('/api/v1/song/12/albumcontext');
      expect(put?.body).toEqual({ albumKey: 'singles' });
    });
  });

  it('shows each album option with its cover, album artist, compilation badge and track text', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await openRowMenu(user, 'Get Lucky');
    await user.click(await screen.findByRole('menuitem', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');
    const compilation = await within(dialog).findByRole('radio', { name: /So Fresh/ });
    const row = compilation.closest('.mantine-Radio-root') as HTMLElement;

    expect(within(row).getByText('by Various Artists')).toBeInTheDocument();
    expect(within(row).getByText('Compilation')).toBeInTheDocument();
    expect(within(row).getByText('22 tracks · 2004 · first released 1999')).toBeInTheDocument();
    expect(row.querySelector('img')).toHaveAttribute(
      'src',
      'https://coverartarchive.org/release/b7a1c2d3-0000-0000-0000-000000000000/front-250',
    );
    expect(row.querySelector('img')).toHaveAttribute('loading', 'lazy');

    const current = within(dialog).getByRole('radio', { name: /Random Access Memories/ });

    expect(current).toBeChecked();
    expect(within(dialog).getByText('Track 8 of 13 · 2013')).toBeInTheDocument();
    expect(within(dialog).getAllByText('by Daft Punk')).toHaveLength(2);
  });

  it('deletes the song once the confirmation is accepted', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await openRowMenu(user, 'Get Lucky');
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'DELETE' && request.url.endsWith('/api/v1/song/12'))).toBe(
        true,
      );
    });
  });

  it('starts the automatic search for a song when its search button is used', async () => {
    installFetch((url, init) => {
      const shared = sharedRoutes(url);

      if (shared !== null) {
        return shared;
      }

      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/command')) {
        return jsonResponse({}, init?.method === 'POST' ? 201 : 200);
      }

      if (url.includes('/api/v1/artist')) {
        return jsonResponse(ARTISTS);
      }

      if (url.includes('/api/v1/library')) {
        return jsonResponse(LIBRARIES);
      }

      return jsonResponse(paged(LIBRARY_SONGS));
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Search for Get Lucky'));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST' && request.url.endsWith('/api/v1/command'))).toBe(true);
    });

    const post = sent().find((request) => request.method === 'POST');
    const body = JSON.parse(await (post?.body ?? Promise.resolve('{}'))) as { name: string; songId: number };

    expect(body).toEqual({ name: 'SongSearch', songId: 12 });
  });

  it('opens the interactive search for a song when its button is used', async () => {
    installFetch((url) => {
      const shared = sharedRoutes(url);

      if (shared !== null) {
        return shared;
      }

      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/release')) {
        return jsonResponse({
          searchRunId: 5,
          outcome: 'noResults',
          message: 'Nothing answered.',
          releases: [],
        });
      }

      if (url.includes('/api/v1/artist')) {
        return jsonResponse(ARTISTS);
      }

      if (url.includes('/api/v1/library')) {
        return jsonResponse(LIBRARIES);
      }

      return jsonResponse(paged(LIBRARY_SONGS));
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Interactive search for Get Lucky'));

    expect(await screen.findByText('No source returned anything: Nothing answered.')).toBeInTheDocument();
    expect(screen.getByText('No candidate came back for this song.')).toBeInTheDocument();
  });

  it('offers the rest of the album and opens it on the Add page', async () => {
    installFetch((url) => {
      const shared = sharedRoutes(url);

      if (shared !== null) {
        return shared;
      }

      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/artist')) {
        return jsonResponse(ARTISTS);
      }

      if (url.includes('/api/v1/library')) {
        return jsonResponse(LIBRARIES);
      }

      // The release the song is pinned to, as `GET /api/v1/song/{id}/album` answers.
      if (/\/api\/v1\/song\/\d+\/album$/.test(url)) {
        return jsonResponse({ source: 'musicbrainz', id: '9c1b3a2f-0000-0000-0000-000000000000' });
      }

      if (/\/api\/v1\/song\/\d+\/albumcontexts$/.test(url)) {
        return jsonResponse(ALBUM_OPTIONS);
      }

      if (/\/api\/v1\/song\/\d+$/.test(url)) {
        return jsonResponse(LIBRARY_SONGS[0]);
      }

      if (url.includes('/api/v1/song')) {
        return jsonResponse(paged(LIBRARY_SONGS));
      }

      return new Response('not found', { status: 404 });
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await openRowMenu(user, 'Get Lucky');

    await user.click(await screen.findByRole('menuitem', { name: 'Add the rest of this album' }));

    expect(window.location.pathname).toBe('/add');
    expect(window.location.search).toBe('?album=musicbrainz:9c1b3a2f-0000-0000-0000-000000000000');
  });

  it('shows the empty state when the filters match nothing', async () => {
    installFetch((url) => {
      const shared = sharedRoutes(url);

      if (shared !== null) {
        return shared;
      }

      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/artist')) {
        return jsonResponse(ARTISTS);
      }

      if (url.includes('/api/v1/library')) {
        return jsonResponse(LIBRARIES);
      }

      return jsonResponse(paged([]));
    });

    renderApp();

    expect(await screen.findByText('Nothing in the library matches these filters.')).toBeInTheDocument();
  });
});

const EDITOR_URL = '/api/v1/song/editor';
const FILTERS_URL = '/api/v1/customfilter';

/** The `GET /api/v1/song` requests the page has made, newest last, as parsed query strings. */
function songQueries(): URLSearchParams[] {
  return sent()
    .filter((request) => request.method === 'GET' && /\/api\/v1\/song\?/.test(request.url))
    .map((request) => new URL(request.url).searchParams);
}

/** Picks an option of a Mantine select by its label. */
async function choose(user: ReturnType<typeof userEvent.setup>, label: string, option: string): Promise<void> {
  await user.click(inputLabelled(screen.getAllByLabelText(label)));
  await user.click(await screen.findByRole('option', { name: option }));
}

/** A Mantine select's label also names its option list; the field itself is the input. */
function inputLabelled(elements: HTMLElement[]): HTMLElement {
  const input = elements.find((element) => element.tagName === 'INPUT');

  if (input === undefined) {
    throw new Error('No input carries that label.');
  }

  return input;
}

function problem(status: number, detail: string): Response {
  return jsonResponse({ title: 'Problem', status, detail }, status);
}

const EDITOR_OK = { songs: [], moveCommandIds: [] };

describe('LibraryPage mass editor', () => {
  it('sends the filters as a query string and goes back to page 1 when one changes', async () => {
    install({ songs: makeSongs(2), totalRecords: 120 });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Song A');
    await user.click(screen.getByRole('button', { name: '2' }));

    await waitFor(() => expect(songQueries().at(-1)?.get('page')).toBe('2'));

    await choose(user, 'File', 'Has a file');

    await waitFor(() => {
      const query = songQueries().at(-1);

      expect(query?.get('hasFile')).toBe('true');
      expect(query?.get('page')).toBe('1');
    });
  });

  it('asks the server for the search term once typing pauses', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await user.type(screen.getByRole('textbox', { name: 'Search' }), 'luck');

    await waitFor(() => expect(songQueries().at(-1)?.get('term')).toBe('luck'));
    // Typing did not ask once per keystroke.
    expect(songQueries().filter((query) => query.has('term')).length).toBe(1);
  });

  it('unmonitors the selected songs through the editor endpoint', async () => {
    install({
      songs: makeSongs(3),
      extra: (url, method) => (url.endsWith(EDITOR_URL) && method === 'PUT' ? jsonResponse(EDITOR_OK) : null),
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Select Song A'));
    await user.click(screen.getByLabelText('Select Song B'));

    expect(screen.getByText('2 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Unmonitor' }));

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain(EDITOR_URL);
      expect(put?.body).toMatchObject({ songIds: [12, 13], monitored: false });
    });
  });

  it('selects every song on the page from the header checkbox', async () => {
    install({ songs: makeSongs(3) });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Song A');
    await user.click(screen.getByLabelText('Select all on this page'));

    expect(screen.getByText('3 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Clear selection' }));

    expect(screen.queryByText('3 selected')).not.toBeInTheDocument();
  });

  it('selects a range of rows with shift-click', async () => {
    install({ songs: makeSongs(4) });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Select Song A'));
    await user.keyboard('{Shift>}');
    await user.click(screen.getByLabelText('Select Song C'));
    await user.keyboard('{/Shift}');

    expect(screen.getByText('3 selected')).toBeInTheDocument();
    expect(screen.getByLabelText('Select Song B')).toBeChecked();
    expect(screen.getByLabelText('Select Song D')).not.toBeChecked();
  });

  it('sends the tags and how to apply them from the tags dialog', async () => {
    install({
      songs: makeSongs(3),
      extra: (url, method) => (url.endsWith(EDITOR_URL) && method === 'PUT' ? jsonResponse(EDITOR_OK) : null),
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Select Song A'));
    await user.click(screen.getByLabelText('Select Song B'));
    await user.click(screen.getByRole('button', { name: 'Tags…' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(inputLabelled(within(dialog).getAllByLabelText('Tags')), 'jazz{Enter}');
    await user.click(within(dialog).getByRole('radio', { name: 'Replace the songs’ tags' }));
    await user.click(within(dialog).getByRole('button', { name: 'Apply' }));

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain(EDITOR_URL);
      expect(put?.body).toMatchObject({ songIds: [12, 13], tags: ['jazz'], applyTags: 'replace' });
    });
  });

  it('deletes the selected songs once the confirmation is accepted', async () => {
    install({
      songs: makeSongs(3),
      extra: (url, method) => (url.endsWith(EDITOR_URL) && method === 'DELETE' ? jsonResponse({ deleted: 2 }) : null),
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Select Song A'));
    await user.click(screen.getByLabelText('Select Song B'));
    await user.click(screen.getByRole('button', { name: 'Delete…' }));

    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText('Delete 2 songs from Wondarr? Their files stay on disk.')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(async () => {
      const removed = await lastBody('DELETE');

      expect(removed?.url).toContain(EDITOR_URL);
      expect(removed?.body).toEqual({ songIds: [12, 13] });
    });
  });

  it('shows why the delete was refused when a song is still in the queue', async () => {
    install({
      songs: makeSongs(3),
      extra: (url, method) =>
        url.endsWith(EDITOR_URL) && method === 'DELETE' ? problem(409, 'Song A is still downloading.') : null,
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Select Song A'));
    await user.click(screen.getByRole('button', { name: 'Delete…' }));

    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await within(dialog).findByText('Song A is still downloading.')).toBeInTheDocument();
  });

  it('saves the current filters as a named view', async () => {
    install({
      extra: (url, method) =>
        url.endsWith(FILTERS_URL) && method === 'POST'
          ? jsonResponse({ id: 5, type: 'library', label: 'Needs files', filters: [] }, 201)
          : null,
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await choose(user, 'File', 'Missing its file');
    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Save view…' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Needs files');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain(FILTERS_URL);
      expect(post?.body).toEqual({
        type: 'library',
        label: 'Needs files',
        filters: [{ key: 'hasFile', value: 'false', type: 'equal' }],
      });
    });
  });

  it('shows the detail when a view with that name already exists', async () => {
    install({
      extra: (url, method) =>
        url.endsWith(FILTERS_URL) && method === 'POST'
          ? problem(409, 'A view named “Needs files” already exists.')
          : null,
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await choose(user, 'File', 'Missing its file');
    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Save view…' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Needs files');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('A view named “Needs files” already exists.')).toBeInTheDocument();
  });

  it('applies a saved view’s filters when it is chosen', async () => {
    install({
      extra: (url, method) =>
        url.endsWith(`${FILTERS_URL}?type=library`) && method === 'GET'
          ? jsonResponse([
              {
                id: 5,
                type: 'library',
                label: 'Quiet rock',
                filters: [
                  { key: 'monitored', value: 'false', type: 'equal' },
                  { key: 'tag', value: 'rock', type: 'equal' },
                ],
              },
            ])
          : null,
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Get Lucky');
    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Quiet rock' }));

    await waitFor(() => {
      const query = songQueries().at(-1);

      expect(query?.get('monitored')).toBe('false');
      expect(query?.get('tag')).toBe('rock');
      expect(query?.get('page')).toBe('1');
    });

    // The active view's label is on the menu button.
    expect(screen.getByRole('button', { name: 'Quiet rock' })).toBeInTheDocument();
  });

  it('hides the tags column when no song on the page has tags', async () => {
    install({ songs: makeSongs(2) });

    renderApp();

    await screen.findByText('Song A');

    expect(screen.queryByRole('columnheader', { name: 'Tags' })).not.toBeInTheDocument();
  });

  it('shows the tags column and filters by a tag when its badge is clicked', async () => {
    install({ songs: makeSongs(2, ['rock']) });
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Song A');

    expect(screen.getByRole('columnheader', { name: 'Tags' })).toBeInTheDocument();

    await user.click(screen.getAllByLabelText('Filter by tag rock')[0] ?? document.body);

    await waitFor(() => expect(songQueries().at(-1)?.get('tag')).toBe('rock'));
  });
});
