import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ALBUM_OPTIONS, ARTISTS, HEALTH_ENTRIES, LIBRARY_SONGS, paged, SYSTEM_STATUS } from '../test/fixtures';
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

/** The route table the page needs: the shell, the artists and the paged songs. */
function install(write: () => Response = () => jsonResponse(LIBRARY_SONGS[0])): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/artist')) {
      return jsonResponse(ARTISTS);
    }

    if (/\/api\/v1\/song\/\d+\/albumcontexts$/.test(url)) {
      return jsonResponse(ALBUM_OPTIONS);
    }

    // The item endpoint takes the writes: the PUT that saves and the DELETE that removes.
    if (/\/api\/v1\/song\/\d+$/.test(url)) {
      return write();
    }

    if (url.includes('/api/v1/song')) {
      return jsonResponse(paged(LIBRARY_SONGS));
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens one song's row menu and hands back the dropdown. */
async function openRowMenu(user: ReturnType<typeof userEvent.setup>, title: string): Promise<void> {
  await user.click(screen.getByLabelText(`Actions for ${title}`));
}

describe('LibraryPage', () => {
  it('lists the songs with their album assignment and version flags', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Get Lucky')).toBeInTheDocument();
    // Scoped to the cell: the artist filter's select also carries the name.
    expect(screen.getByRole('cell', { name: 'Daft Punk' })).toBeInTheDocument();
    expect(screen.getByText('Random Access Memories')).toBeInTheDocument();
    // The second song is filed under its artist's Singles pseudo-album.
    expect(screen.getByText('Singles')).toBeInTheDocument();
    expect(screen.getByText('radio_edit')).toBeInTheDocument();
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

    await user.click(await within(dialog).findByRole('radio', { name: 'Singles' }));

    await waitFor(async () => {
      const put = await lastBody('PUT');

      expect(put?.url).toContain('/api/v1/song/12/albumcontext');
      expect(put?.body).toEqual({ albumKey: 'singles' });
    });
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

  it('shows the empty state when the filters match nothing', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/artist')) {
        return jsonResponse(ARTISTS);
      }

      return jsonResponse(paged([]));
    });

    renderApp();

    expect(await screen.findByText('Nothing in the library matches these filters.')).toBeInTheDocument();
  });
});
