import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  COMMAND_COMPLETED,
  HEALTH_ENTRIES,
  LIBRARIES,
  QUALITY_PROFILES,
  SYSTEM_STATUS,
} from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/importlists');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * The providers and the settings form each one wants, as `GET /api/v1/importlist/schema` sends it.
 * The Deezer playlist asks for one field; the CSV provider's fields are the column mapping a generic
 * file is read with.
 */
const IMPORT_LIST_SCHEMA = [
  {
    type: 'deezerPlaylist',
    displayName: 'Deezer playlist',
    fields: [
      {
        name: 'playlist',
        label: 'Playlist',
        type: 'text',
        required: true,
        helpText: 'The playlist id, or the address of its page.',
        options: null,
        secret: false,
        advanced: false,
      },
    ],
  },
  {
    type: 'csv',
    displayName: 'CSV file',
    fields: [
      {
        name: 'titleColumn',
        label: 'Title column',
        type: 'text',
        required: false,
        helpText: 'The column holding the track title.',
        options: null,
        secret: false,
        advanced: false,
      },
      {
        name: 'artistColumn',
        label: 'Artist column',
        type: 'text',
        required: false,
        helpText: 'The column holding the artist name.',
        options: null,
        secret: false,
        advanced: false,
      },
    ],
  },
];

/** One list, with the counts of its items and the message its last sync left. */
const IMPORT_LISTS = [
  {
    id: 7,
    type: 'deezerPlaylist',
    name: 'Deezer favourites',
    created: '2026-01-08T00:00:00Z',
    lastSyncedAt: '2026-01-08T00:01:00Z',
    counts: { pending: 1, added: 47, unresolved: 3, skipped: 0, removed: 2 },
    enabled: true,
    syncIntervalHours: 24,
    policy: 'AddOnly',
    qualityProfileId: 0,
    libraryId: 0,
    lastSyncMessage: 'Failed: Deezer answered 403 for playlist 66877419.',
    settings: { playlist: '66877419' },
    hasFile: false,
    plexPlaylist: false,
    m3uExport: false,
  },
];

/** What the list a create returns, so the dialog closes on a body the client can read. */
const CREATED_LIST = { ...IMPORT_LISTS[0], id: 8, name: 'Deezer favourites', lastSyncMessage: null };

/** What `POST /api/v1/importlist/csv/preview` answers: the format, the count and the first rows. */
const CSV_PREVIEW = {
  format: 'exportify',
  headers: ['Title', 'Artist', 'ISRC', 'Duration Ms'],
  rowCount: 2,
  sample: [
    {
      externalId: '1',
      artist: 'Aphex Twin',
      title: 'Xtal',
      album: null,
      durationMs: 293000,
      isrc: 'GBBPN1200001',
      mbRecordingId: null,
    },
    {
      externalId: '2',
      artist: 'Nobody',
      title: 'Nothing At All',
      album: null,
      durationMs: 211000,
      isrc: null,
      mbRecordingId: null,
    },
  ],
  problems: [],
};

/** The text of the file a test uploads; the browser reads it, the request carries it as `sourceText`. */
const CSV_TEXT = 'Title,Artist,ISRC,Duration Ms\nXtal,Aphex Twin,GBBPN1200001,293000\nNothing At All,Nobody,,211000\n';

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** The last request whose URL and method match, so a dialog's save is read rather than the list's fetch. */
async function lastRequest(method: string, urlPart: string): Promise<Record<string, unknown> | null> {
  const calls = sent().filter((request) => request.method === method && request.url.includes(urlPart));
  const call = calls[calls.length - 1];

  return call === undefined ? null : (JSON.parse(await call.body) as Record<string, unknown>);
}

/**
 * The route table the page needs. The schema, the preview, the sync and the command are matched
 * before the collection, because their own URLs start with the collection's.
 */
function install(options: { create?: () => Response; preview?: () => Response } = {}): FetchMock {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/qualityprofile')) {
      return jsonResponse(QUALITY_PROFILES);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (url.includes('/api/v1/importlist/schema')) {
      return jsonResponse(IMPORT_LIST_SCHEMA);
    }

    if (url.includes('/api/v1/importlist/csv/preview')) {
      return options.preview?.() ?? jsonResponse(CSV_PREVIEW);
    }

    if (url.includes('/api/v1/importlist/7/sync')) {
      return jsonResponse({ commandId: 9 });
    }

    if (url.includes('/api/v1/command/9')) {
      return jsonResponse(COMMAND_COMPLETED);
    }

    if (url.includes('/api/v1/importlist')) {
      if (method === 'POST') {
        return options.create?.() ?? jsonResponse(CREATED_LIST, 201);
      }

      if (method === 'PUT') {
        return jsonResponse(IMPORT_LISTS[0]);
      }

      if (method === 'DELETE') {
        return jsonResponse({});
      }

      return jsonResponse(IMPORT_LISTS);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens a Mantine Select by the label on its input and hands back the listbox it is showing. */
async function openSelect(user: UserEvent, label: string): Promise<HTMLElement> {
  const input = screen.getByLabelText(label, { selector: 'input' });

  await user.click(input);

  const listbox = document.getElementById(input.getAttribute('aria-controls') ?? '');

  if (listbox === null) {
    throw new Error(`the ${label} dropdown did not open`);
  }

  return listbox;
}

/** Opens a Mantine Select and picks one of its options by label. */
async function pickOption(user: UserEvent, label: string, option: string): Promise<void> {
  const listbox = await openSelect(user, label);

  await user.click(within(listbox).getByText(option));
}

/** Opens the add dialog and picks a provider from the schema. */
async function addList(user: UserEvent, type: string): Promise<HTMLElement> {
  await user.click(screen.getByRole('button', { name: 'Add list' }));
  await pickOption(user, 'Type', type);
  await user.click(screen.getByRole('button', { name: 'Continue' }));

  return screen.findByRole('dialog');
}

/** The table row of one list, found by its name. */
async function rowOf(name: string): Promise<HTMLElement> {
  const row = (await screen.findByText(name)).closest('tr');

  if (row === null) {
    throw new Error(`the ${name} row was not found`);
  }

  return row;
}

describe('ImportListsSettingsPage', () => {
  it('lists the import lists with their counts and the failed sync in red', async () => {
    install();

    renderApp();

    const row = await rowOf('Deezer favourites');

    expect(within(row).getByText('Deezer playlist')).toBeInTheDocument();
    expect(within(row).getByText('Added 47')).toBeInTheDocument();
    expect(within(row).getByText('Unresolved 3')).toBeInTheDocument();
    expect(within(row).getByText('Pending 1')).toBeInTheDocument();
    expect(within(row).getByText('Removed 2')).toBeInTheDocument();

    const message = within(row).getByText('Failed: Deezer answered 403 for playlist 66877419.');

    // Mantine's `c="red"` resolves to the theme's red text variable, inline on the element.
    expect(message).toHaveAttribute('style', expect.stringContaining('--mantine-color-red'));
  });

  it('renders the Deezer playlist form from the schema and posts what it holds', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await addList(user, 'Deezer playlist');

    expect(within(dialog).getByLabelText('Playlist *')).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText('Name'), 'Deezer favourites');
    await user.type(within(dialog).getByLabelText('Playlist *'), '66877419');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const body = await lastRequest('POST', '/api/v1/importlist');

      expect(body).toMatchObject({
        type: 'deezerPlaylist',
        name: 'Deezer favourites',
        settings: { playlist: '66877419' },
        policy: 'AddOnly',
        qualityProfileId: null,
        libraryId: null,
        enabled: true,
        syncIntervalHours: 24,
        plexPlaylist: false,
        m3uExport: false,
      });
    });
  });

  it('syncs a list now and shows the message its command finished with', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const row = await rowOf('Deezer favourites');

    await user.click(within(row).getByRole('button', { name: 'Sync now' }));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST' && request.url.includes('/api/v1/importlist/7/sync'))).toBe(
        true,
      );
    });

    expect(await screen.findByText('Synced Deezer favourites')).toBeInTheDocument();
    expect(screen.getByText('Resolved 50 of 50 lines')).toBeInTheDocument();
  });

  it('asks before deleting, then deletes', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const row = await rowOf('Deezer favourites');

    await user.click(within(row).getByRole('button', { name: 'Delete' }));

    const dialog = screen.getByRole('dialog');

    expect(within(dialog).getByText(/stay in the library/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(
        sent().some((request) => request.method === 'DELETE' && request.url.includes('/api/v1/importlist/7')),
      ).toBe(true);
    });
  });

  it('reads a chosen CSV file, previews it and shows the rows', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await addList(user, 'CSV file');
    const input = document.querySelector('input[type="file"]');

    if (!(input instanceof HTMLInputElement)) {
      throw new Error('the CSV file input was not found');
    }

    await user.upload(input, new File([CSV_TEXT], 'playlist.csv', { type: 'text/csv' }));

    expect(await within(dialog).findByText('Exportify export')).toBeInTheDocument();
    expect(within(dialog).getByText('2 rows')).toBeInTheDocument();
    expect(within(dialog).getByText('Xtal')).toBeInTheDocument();
    expect(within(dialog).getByText('Aphex Twin')).toBeInTheDocument();

    const body = await lastRequest('POST', '/api/v1/importlist/csv/preview');

    expect(body).toMatchObject({ sourceText: CSV_TEXT, settings: { titleColumn: '', artistColumn: '' } });
  });

  it('maps a generic CSV by its columns and previews again', async () => {
    // A file the server could not recognise is mapped by hand: the column fields show, and the
    // preview runs again once a column is named.
    install({ preview: () => jsonResponse({ ...CSV_PREVIEW, format: 'generic' }) });
    const user = userEvent.setup();

    renderApp();

    const dialog = await addList(user, 'CSV file');
    const input = document.querySelector('input[type="file"]');

    if (!(input instanceof HTMLInputElement)) {
      throw new Error('the CSV file input was not found');
    }

    await user.upload(input, new File([CSV_TEXT], 'playlist.csv', { type: 'text/csv' }));

    expect(await within(dialog).findByText('Mapped CSV')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Title column')).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText('Title column'), 'Track name');

    await waitFor(
      async () => {
        const body = await lastRequest('POST', '/api/v1/importlist/csv/preview');

        expect(body).toMatchObject({ sourceText: CSV_TEXT, settings: { titleColumn: 'Track name' } });
      },
      { timeout: 3000 },
    );
  });
});
