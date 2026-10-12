import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, LIBRARIES, REFERENCE_LIBRARIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/references');
  // jsdom has no scrollIntoView, which Mantine's menu calls as it opens.
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

/** The route table the page needs: the shell, the libraries and the two commands. */
function install(
  libraries: unknown = REFERENCE_LIBRARIES,
  post: () => Response = () => jsonResponse(REFERENCE_LIBRARIES[0]),
  ownedSongs: number | (() => Response) = 12,
): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/scan')) {
      return jsonResponse({ id: 9, name: 'ReferenceLibraryScan', status: 'queued' });
    }

    if (url.includes('/ownedsongs')) {
      return typeof ownedSongs === 'function' ? ownedSongs() : jsonResponse({ count: ownedSongs });
    }

    if (url.includes('/api/v1/referencelibrary')) {
      return init?.method === 'POST' ? post() : jsonResponse(libraries);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (url.includes('/api/v1/command')) {
      return jsonResponse({ id: 11, name: 'ReferenceAdopt', status: 'queued' }, 201);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens a library row's action menu. */
async function openRowMenu(user: ReturnType<typeof userEvent.setup>, name: string): Promise<void> {
  await user.click(await screen.findByLabelText(`Actions for ${name}`));
}

describe('ReferenceLibrariesPage', () => {
  it('lists the libraries with their mode, target library and counts', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Old music')).toBeInTheDocument();
    expect(screen.getByText('Reference only')).toBeInTheDocument();
    expect(screen.getByText('Adopt into Music')).toBeInTheDocument();
    expect(screen.getByText('400 identified')).toBeInTheDocument();
    // "Needs review" counts the ambiguous and unmatched files of this library together.
    expect(screen.getByText('3 need review')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '3 need review' })).toHaveAttribute('href', '/match?referenceLibraryId=3');
  });

  it('shows the empty state when there is no reference library yet', async () => {
    install([]);

    renderApp();

    expect(await screen.findByText('No reference libraries yet')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add reference library' })).toBeInTheDocument();
  });

  it('refuses an adopt library with no target library before it sends anything', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Add reference library' }));
    await user.type(screen.getByLabelText('Name'), 'Bought music');
    await user.type(screen.getByLabelText('Root path'), '/data/bought');
    await user.click(screen.getByRole('radio', { name: 'Adopt' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('libraryId is required in adopt mode.')).toBeInTheDocument();
    expect(mock.calls.some((call) => call.init?.method === 'POST')).toBe(false);
  });

  it('shows the field message a 400 came back with', async () => {
    install(REFERENCE_LIBRARIES, () =>
      jsonResponse(
        { title: 'One or more validation errors occurred.', errors: { rootPath: ['rootPath is not a usable path.'] } },
        400,
      ),
    );
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Add reference library' }));
    await user.type(screen.getByLabelText('Name'), 'Bought music');
    await user.type(screen.getByLabelText('Root path'), 'relative/path');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('rootPath is not a usable path.')).toBeInTheDocument();
  });

  it('queues a scan of the row that was asked for', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await user.click(await screen.findByRole('menuitem', { name: 'Scan now' }));

    await waitFor(() => {
      const post = sent().find((request) => request.method === 'POST');

      expect(post?.url).toContain('/api/v1/referencelibrary/3/scan');
    });

    expect(await screen.findByText('Scan queued')).toBeInTheDocument();
    expect(mock.calls.length).toBeGreaterThan(0);
  });

  it('asks before deleting, and only then sends the DELETE', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    expect(mock.calls.some((call) => call.init?.method === 'DELETE')).toBe(false);

    const dialog = await screen.findByRole('dialog', { name: 'Delete reference library' });

    expect(await within(dialog).findByText(/12 songs are owned through this folder/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Your files are not touched/)).toBeInTheDocument();

    // The safe choice is the default, and the dialog says which choice starts searching.
    expect(within(dialog).getByRole('radio', { name: /Keep them wanted/ })).toBeChecked();
    expect(within(dialog).getByRole('radio', { name: /search every wanted song now/ })).not.toBeChecked();
    expect(within(dialog).getByRole('radio', { name: 'Unmonitor them' })).not.toBeChecked();
    expect(within(dialog).getByText(/Only the second choice starts searching/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      const deletion = sent().find((request) => request.method === 'DELETE');

      expect(deletion?.url).toContain('/api/v1/referencelibrary/3?songs=wanted');
    });
  });

  it.each([
    ['Make them wanted and search every wanted song now', 'search'],
    ['Unmonitor them', 'unmonitor'],
  ])('sends the chosen option "%s" with the DELETE', async (label, songs) => {
    install();
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete reference library' });

    await user.click(await within(dialog).findByRole('radio', { name: label }));
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      const deletion = sent().find((request) => request.method === 'DELETE');

      expect(deletion?.url).toContain(`/api/v1/referencelibrary/3?songs=${songs}`);
    });
  });

  it('still allows the delete when the count fails, and can retry the count', async () => {
    let attempts = 0;

    install(
      REFERENCE_LIBRARIES,
      () => jsonResponse(REFERENCE_LIBRARIES[0]),
      () => {
        attempts += 1;

        return attempts <= 1 ? new Response('boom', { status: 500 }) : jsonResponse({ count: 4 });
      },
    );
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete reference library' });

    expect(await within(dialog).findByText(/could not be read/)).toBeInTheDocument();
    expect(within(dialog).getByRole('radio', { name: /Keep them wanted/ })).toBeChecked();
    expect(within(dialog).getByRole('button', { name: 'Delete' })).toBeEnabled();

    await user.click(within(dialog).getByRole('button', { name: 'Retry' }));

    expect(await within(dialog).findByText(/4 songs are owned through this folder/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Retry' })).toBeNull();

    // The delete goes out with the default choice even though the count had failed.
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(sent().find((request) => request.method === 'DELETE')?.url).toContain('?songs=wanted');
    });
  });

  it('offers no choice when the library owns no song', async () => {
    install(REFERENCE_LIBRARIES, () => jsonResponse(REFERENCE_LIBRARIES[0]), 0);
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete reference library' });

    expect(await within(dialog).findByText(/No song is owned through this folder/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('radio')).toBeNull();
    expect(within(dialog).getByRole('button', { name: 'Delete' })).toBeEnabled();
  });

  it('queues the adopt command for an adopt-mode row, and offers it only there', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await openRowMenu(user, 'Old music');
    await screen.findByRole('menuitem', { name: 'Scan now' });

    expect(screen.queryByRole('menuitem', { name: 'Adopt now' })).toBeNull();

    await user.keyboard('{Escape}');
    await openRowMenu(user, 'Bought music');
    await user.click(await screen.findByRole('menuitem', { name: 'Adopt now' }));

    await waitFor(async () => {
      const post = sent().find((request) => request.method === 'POST' && request.url.includes('/api/v1/command'));

      expect(post).toBeDefined();

      const body: unknown = post === undefined ? null : JSON.parse(await post.body);

      expect(body).toMatchObject({ name: 'ReferenceAdopt', referenceLibraryId: 4 });
    });
  });
});
