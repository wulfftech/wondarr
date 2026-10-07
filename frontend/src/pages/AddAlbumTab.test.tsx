import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, LIBRARIES, QUALITY_PROFILES, SYSTEM_STATUS, paged } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

/**
 * The Add songs page's Album tab, against a mocked fetch. The fixtures are the shapes `schema.d.ts`
 * documents for the album endpoints, with the values the real server would send.
 */

/** The release group the search returns, and the release of it the API marks as the default. */
const RELEASE_GROUP_ID = '9c1b3a2f-1111-1111-1111-111111111111';
const DEFAULT_RELEASE_ID = '9c1b3a2f-2222-2222-2222-222222222222';

/** What the release select shows for the default release: title, date, country, formats, track count. */
const DEFAULT_RELEASE_LABEL = 'Random Access Memories (Deluxe) · 2013-12-23 · XE · 2xCD · 23 tracks';

const ALBUM_SEARCH_RESULTS = [
  {
    source: 'musicbrainz',
    id: RELEASE_GROUP_ID,
    title: 'Random Access Memories',
    artist: 'Daft Punk',
    year: '2013',
    type: 'Album',
    trackCount: 13,
    coverUrl: null,
  },
  {
    source: 'deezer',
    id: '302127',
    title: 'Discovery',
    artist: 'Daft Punk',
    year: '2001',
    type: 'Album',
    trackCount: 14,
    coverUrl: null,
  },
];

const ALBUM_RELEASES = [
  {
    id: DEFAULT_RELEASE_ID,
    title: 'Random Access Memories (Deluxe)',
    date: '2013-12-23',
    country: 'XE',
    formats: '2xCD',
    trackCount: 23,
    disambiguation: null,
    isDefault: true,
  },
  {
    id: '9c1b3a2f-3333-3333-3333-333333333333',
    title: 'Random Access Memories',
    date: '2013-05-17',
    country: 'US',
    formats: 'CD',
    trackCount: 13,
    disambiguation: null,
    isDefault: false,
  },
];

/** Three tracks of the default release: the first is one the library already holds. */
const ALBUM_TRACKS = [
  {
    disc: 1,
    position: 1,
    title: 'Give Life Back to Music',
    artistCredit: 'Daft Punk',
    lengthMs: 274000,
    mbRecordingId: 'rec-give-life',
    deezerTrackId: null,
    isrcs: [],
    songId: 12,
    libraryId: 1,
    owned: true,
  },
  {
    disc: 1,
    position: 2,
    title: 'The Game of Love',
    artistCredit: 'Daft Punk',
    lengthMs: 304000,
    mbRecordingId: 'rec-game-of-love',
    deezerTrackId: null,
    isrcs: [],
    songId: null,
    libraryId: null,
    owned: false,
  },
  {
    disc: 1,
    position: 3,
    title: 'Giorgio by Moroder',
    artistCredit: 'Daft Punk',
    lengthMs: 549000,
    mbRecordingId: 'rec-giorgio',
    deezerTrackId: null,
    isrcs: [],
    songId: null,
    libraryId: null,
    owned: false,
  },
];

const ALBUM_ADD_ACCEPTED = { commandId: 30 };

/** The album add's command once it has finished. */
const ALBUM_COMMAND_COMPLETED = {
  id: 30,
  name: 'AddAlbum',
  commandName: 'AddAlbum',
  message: 'Added 1 song to Music',
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

beforeEach(() => {
  resetLocation('/add');
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` actually received; see the sibling Add songs page test. */
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
  lookup?: () => Response;
  releases?: () => Response;
  tracks?: () => Response;
  add?: () => Response;
  commands?: () => Response;
}

/** The routes the Album tab reads. The album endpoints are matched before the catch-all tracks one. */
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

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (url.includes('/api/v1/album/lookup')) {
      return (routes.lookup ?? (() => jsonResponse(ALBUM_SEARCH_RESULTS)))();
    }

    if (url.includes('/api/v1/album/releasegroup/')) {
      return (routes.releases ?? (() => jsonResponse(ALBUM_RELEASES)))();
    }

    if (url.includes('/api/v1/album/add')) {
      return (routes.add ?? (() => jsonResponse(ALBUM_ADD_ACCEPTED, 202)))();
    }

    if (url.includes('/api/v1/album/')) {
      return (routes.tracks ?? (() => jsonResponse(ALBUM_TRACKS)))();
    }

    if (url.includes('/api/v1/command/')) {
      return (routes.commands ?? (() => jsonResponse(ALBUM_COMMAND_COMPLETED)))();
    }

    if (url.includes('/api/v1/song')) {
      return jsonResponse(paged([]));
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens the Album tab and runs a search, the way the first two tests arrive at the results. */
async function searchAlbum(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.click(screen.getByRole('tab', { name: 'Album' }));
  await user.type(screen.getByRole('textbox', { name: 'Album' }), 'Daft Punk Random Access Memories{Enter}');
  await screen.findByText('Random Access Memories');
}

describe('AddAlbumTab', () => {
  it('searches for an album and shows what tells the results apart', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await searchAlbum(user);

    // Scoped to the panel: both results carry the same artist name.
    const panel = within(screen.getByRole('tabpanel', { name: 'Album' }));

    expect(panel.getAllByText('Daft Punk')).toHaveLength(2);
    expect(panel.getByText('2013')).toBeInTheDocument();
    expect(panel.getByText('13 tracks')).toBeInTheDocument();
    expect(panel.getByText('MusicBrainz')).toBeInTheDocument();
    expect(panel.getByText('Deezer only')).toBeInTheDocument();

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'GET' && request.url.includes('/api/v1/album/lookup'))).toBe(
        true,
      );
    });
  });

  it('lists the releases of a MusicBrainz group with the default preselected and the owned track disabled', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await searchAlbum(user);
    await user.click(screen.getAllByRole('button', { name: 'Choose' })[0]);

    expect(await screen.findByRole('combobox', { name: 'Release' })).toHaveValue(DEFAULT_RELEASE_LABEL);

    expect(await screen.findByText('Giorgio by Moroder')).toBeInTheDocument();
    // 274000 ms reads as 4:34.
    expect(screen.getByText('4:34')).toBeInTheDocument();
    expect(screen.getByLabelText('Add Give Life Back to Music')).toBeDisabled();
    expect(screen.getByLabelText('Add The Game of Love')).toBeEnabled();
    expect(screen.getByText('In the library')).toBeInTheDocument();
    expect(screen.getByText('2 of 2 tracks selected')).toBeInTheDocument();

    expect(
      sent().some((request) => request.url.includes(`/api/v1/album/releasegroup/${RELEASE_GROUP_ID}/releases`)),
    ).toBe(true);
    expect(
      sent().some((request) => request.url.includes(`/api/v1/album/musicbrainz/${DEFAULT_RELEASE_ID}/tracks`)),
    ).toBe(true);
  });

  it('adds only the ticked tracks and shows what the command reported', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await searchAlbum(user);
    await user.click(screen.getAllByRole('button', { name: 'Choose' })[0]);
    await screen.findByText('Giorgio by Moroder');

    await user.click(screen.getByLabelText('Add The Game of Love'));

    expect(screen.getByText('1 of 2 tracks selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add 1 song' }));

    expect(await screen.findByText('Added 1 song to Music')).toBeInTheDocument();

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/album/add');
      // The unticked track and the one the library holds are both left out.
      expect(post?.body).toEqual({
        source: 'musicbrainz',
        id: DEFAULT_RELEASE_ID,
        trackKeys: ['rec-giorgio'],
        libraryId: 1,
        qualityProfileId: 1,
        monitored: true,
      });
    });
  });

  it('opens the tracklist straight away when the URL carries ?album=', async () => {
    install();

    resetLocation(`/add?album=musicbrainz:${DEFAULT_RELEASE_ID}`);
    renderApp();

    expect(await screen.findByText('Giorgio by Moroder')).toBeInTheDocument();
    expect(screen.getByLabelText('Add Give Life Back to Music')).toBeDisabled();
    // A release that is not a group has no releases to choose from.
    expect(screen.queryByRole('combobox', { name: 'Release' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add 2 songs' })).toBeInTheDocument();
    expect(
      sent().some((request) => request.url.includes(`/api/v1/album/musicbrainz/${DEFAULT_RELEASE_ID}/tracks`)),
    ).toBe(true);
  });
});
