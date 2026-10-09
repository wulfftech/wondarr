import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ALBUM_OPTIONS,
  HEALTH_ENTRIES,
  HISTORY_ITEMS,
  LIBRARIES,
  LIBRARY_SONGS,
  paged,
  PREVIEW,
  QUALITY_DEFINITIONS,
  QUALITY_PROFILES,
  SYSTEM_STATUS,
} from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../test/helpers';

beforeEach(() => {
  resetLocation('/song/12');
  // jsdom has no scrollIntoView, which Mantine's combobox and menu call as they open.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** "Get Lucky" as `GET /song/12` sends it: the library row plus its file and tags. */
const SONG = {
  ...LIBRARY_SONGS[0],
  tags: ['disco'],
  albumContext: {
    ...LIBRARY_SONGS[0].albumContext,
    coverUrl: 'https://coverartarchive.org/release/9c1b3a2f-0000-0000-0000-000000000000/front-250',
    pinned: false,
  },
  file: {
    path: '/data/music/Daft Punk/Get Lucky.flac',
    codec: 'flac',
    container: 'flac',
    bitrateKbps: 980,
    size: 31457280,
    sourceType: 'soulseek',
    sampleRate: 44100,
    bitDepth: 16,
    channels: 2,
    durationMs: 369000,
    qualityId: 20,
    acoustId: 'a1b2c3d4-0000-0000-0000-000000000000',
    fingerprintVerified: true,
    importedAt: '2026-01-05T10:00:00Z',
    tagsWritten: null,
    replayGainDb: -6.3,
    replayGainPeak: 0.98,
    source: {
      kind: 'download',
      provider: 'soulseek',
      name: 'Get Lucky.flac',
      queueItemId: 3,
      referenceLibraryId: null,
      referenceLibraryName: null,
      relativePath: null,
    },
  },
};

/** One release row of `/details`. */
function release(index: number, extra: Record<string, unknown> = {}) {
  return {
    key: `release-${index}`,
    mbReleaseId: null,
    mbReleaseGroupId: null,
    title: `Release ${String(index).padStart(2, '0')}`,
    albumArtist: 'Daft Punk',
    primaryType: 'Album',
    secondaryTypes: [],
    status: 'Official',
    date: `${2000 + index}-01-01`,
    trackNo: 1,
    totalTracks: 10,
    coverUrl: null,
    isCurrent: false,
    ...extra,
  };
}

/** Three editions of one album (one release group) plus single releases, for the grouping tests. */
const GROUP_ID = '8mile-0000-0000-0000-000000000000';

function edition(index: number, extra: Record<string, unknown> = {}) {
  return {
    ...ALBUM_OPTIONS[0],
    key: `8mile-${index}`,
    mbReleaseId: `8mile-release-${index}`,
    mbReleaseGroupId: GROUP_ID,
    title: '8 Mile',
    albumArtist: 'Various Artists',
    coverUrl: index === 1 ? null : `https://coverartarchive.org/release/8mile-${index}/front-250`,
    date: `${2002 + index}-11-12`,
    totalTracks: index === 3 ? 16 : 22,
    trackNo: null,
    isCurrent: false,
    ...extra,
  };
}

const DETAILS = {
  releases: [
    release(1, { title: 'Random Access Memories', isCurrent: true, date: '2013-05-17' }),
    release(2, { title: 'Alive 2007', date: '2007-11-19' }),
  ],
  musicBrainz: {
    recordingId: '833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d',
    firstReleaseDate: '2013-04-19',
    disambiguation: 'album version',
    isrcs: ['USQX91300516'],
    artistCredit: 'Daft Punk feat. Pharrell Williams',
    url: 'https://musicbrainz.org/recording/833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d',
  },
  deezer: {
    trackId: 66877419,
    url: 'https://www.deezer.com/track/66877419',
    rank: 845123,
    explicitLyrics: false,
    bpm: 116,
    gain: -9.5,
    releaseDate: '2013-05-17',
    albumCoverUrl: 'https://cdn-images.dzcdn.net/images/cover/abc/250x250.jpg',
  },
  referenceFile: null,
  lyrics: { source: 'none', synced: false, plain: false },
  lastFm: null,
};

const LAST_FM = {
  url: 'https://www.last.fm/music/Daft+Punk/_/Get+Lucky',
  listeners: 1234567,
  playcount: 9876543,
  tags: ['electronic', 'disco'],
  wiki: 'A funky single. <a href="https://www.last.fm/evil">Read more</a>',
  artist: {
    name: 'Daft Punk',
    url: 'https://www.last.fm/music/Daft+Punk',
    bioSummary: 'French electronic duo.',
    listeners: 5000000,
  },
  similar: [
    { artist: 'Chic', title: 'Le Freak', url: 'https://www.last.fm/music/Chic/_/Le+Freak', match: 0.9, songId: null },
    { artist: 'Daft Punk', title: 'Lose Yourself to Dance', url: null, match: 0.8, songId: 13 },
  ],
};

interface Options {
  song?: unknown;
  details?: unknown;
  /** Answers `/song/12` with this status instead of the song. */
  songStatus?: number;
  lyrics?: unknown;
  albumOptions?: unknown[];
}

function install(options: Options = {}) {
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

    if (url.includes('/api/v1/qualitydefinition')) {
      return jsonResponse(QUALITY_DEFINITIONS);
    }

    if (url.includes('/api/v1/library')) {
      return jsonResponse(LIBRARIES);
    }

    if (url.includes('/api/v1/preview')) {
      return jsonResponse(PREVIEW);
    }

    if (url.includes('/api/v1/command')) {
      return jsonResponse({}, 201);
    }

    if (url.includes('/api/v1/release')) {
      return jsonResponse({ searchRunId: 1, outcome: 'noResults', message: 'Nothing answered.', releases: [] });
    }

    if (url.includes('/api/v1/history')) {
      return jsonResponse(paged(HISTORY_ITEMS));
    }

    if (url.includes('/api/v1/queue') || url.includes('/api/v1/blocklist')) {
      return jsonResponse(paged([]));
    }

    if (url.includes('/api/v1/tag')) {
      return jsonResponse([]);
    }

    if (url.endsWith('/song/12/details')) {
      return jsonResponse(options.details ?? DETAILS);
    }

    if (url.endsWith('/song/12/lyrics')) {
      return jsonResponse(options.lyrics ?? { source: null, synced: null, plain: null });
    }

    if (url.endsWith('/song/12/albumcontexts')) {
      return jsonResponse(options.albumOptions ?? ALBUM_OPTIONS);
    }

    if (url.endsWith('/song/12') && method === 'GET') {
      return options.songStatus === undefined
        ? jsonResponse(options.song ?? SONG)
        : new Response('not found', { status: options.songStatus });
    }

    return new Response('not found', { status: 404 });
  });
}

/** The URLs the page asked for. */
function requested(): string[] {
  return vi
    .mocked(globalThis.fetch)
    .mock.calls.map(([input]) => (input instanceof Request ? input.url : String(input)));
}

describe('SongPage', () => {
  it('shows the header: cover, title, flags, artist link, album line and the status chips', async () => {
    install();

    renderApp();

    expect(await screen.findByRole('heading', { name: 'Get Lucky' })).toBeInTheDocument();
    expect(screen.getByText('Radio edit')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Daft Punk' })).toHaveAttribute('href', '/library?artistId=7');
    expect(screen.getByText('Random Access Memories by Daft Punk · track 8 of 13 · 2013')).toBeInTheDocument();
    expect(screen.getByText('Downloaded')).toBeInTheDocument();
    // The FLAC file is above the profile's MP3-256 cutoff.
    expect(screen.getByText('Cutoff met')).toBeInTheDocument();
    expect(screen.getByText('disco')).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'Monitored' })).toBeChecked();
    expect(screen.getAllByLabelText('Quality profile')[0]).toHaveValue('Standard 320');
    expect(document.querySelector('img')).toHaveAttribute('src', expect.stringContaining('coverartarchive.org'));
    await waitFor(() => expect(document.title).toBe('Daft Punk - Get Lucky · Wondarr'));
  });

  it('falls back to the Deezer cover when the album has none', async () => {
    install({ song: { ...SONG, albumContext: { ...SONG.albumContext, coverUrl: null } } });

    renderApp();

    await screen.findByRole('heading', { name: 'Get Lucky' });
    await waitFor(() =>
      expect(document.querySelector('img')).toHaveAttribute('src', expect.stringContaining('dzcdn.net')),
    );
  });

  it('shows the file section: path, quality, codec, source and the fingerprint mark', async () => {
    install();

    renderApp();

    await screen.findByRole('heading', { name: 'Get Lucky' });

    const file = await screen.findByRole('region', { name: 'File' });

    expect(within(file).getByText('/data/music/Daft Punk/Get Lucky.flac')).toBeInTheDocument();
    expect(within(file).getByText('FLAC · 980 kbps')).toBeInTheDocument();
    expect(within(file).getByText('44.1 kHz')).toBeInTheDocument();
    expect(within(file).getByText('16-bit')).toBeInTheDocument();
    expect(within(file).getByText('Stereo')).toBeInTheDocument();
    expect(within(file).getByText('30 MB')).toBeInTheDocument();
    expect(within(file).getByText('-6.3 dB · peak 0.98')).toBeInTheDocument();
    expect(within(file).getByText('Fingerprint verified')).toBeInTheDocument();
    expect(within(file).getByText('Soulseek: Get Lucky.flac')).toBeInTheDocument();
  });

  it('shows the no-file state, with the search buttons, for a missing song', async () => {
    install({ song: { ...SONG, hasFile: false, qualityId: null, file: null } });
    const user = userEvent.setup();

    renderApp();

    const file = await screen.findByRole('region', { name: 'File' });

    expect(within(file).getByText(/No file/)).toBeInTheDocument();
    expect(screen.getByText('Missing')).toBeInTheDocument();

    await user.click(within(file).getByRole('button', { name: 'Search for a file' }));

    await waitFor(() => expect(requested().some((url) => url.endsWith('/api/v1/command'))).toBe(true));
  });

  it('shows the About section from MusicBrainz, Deezer and the release list', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'About' }));

    const mb = await screen.findByRole('region', { name: 'MusicBrainz' });

    expect(within(mb).getByText('2013-04-19')).toBeInTheDocument();
    expect(within(mb).getByText('album version')).toBeInTheDocument();
    expect(within(mb).getByText('USQX91300516')).toBeInTheDocument();

    const link = within(mb).getByRole('link', { name: /View on MusicBrainz/ });

    expect(link).toHaveAttribute('href', 'https://musicbrainz.org/recording/833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d');
    expect(link).toHaveAttribute('rel', 'noreferrer noopener');
    expect(link).toHaveAttribute('target', '_blank');

    const deezer = screen.getByRole('region', { name: 'Deezer' });

    expect(within(deezer).getByText('116')).toBeInTheDocument();
    expect(within(deezer).getByText('845,123')).toBeInTheDocument();
    expect(within(deezer).getByText('-9.5 dB')).toBeInTheDocument();

    const appears = screen.getByRole('region', { name: 'Appears on' });

    expect(within(appears).getByText('Random Access Memories')).toBeInTheDocument();
    expect(within(appears).getByText('Current album')).toBeInTheDocument();
    expect(within(appears).getByText('Alive 2007')).toBeInTheDocument();
  });

  it('hides the Deezer block when its section is null, and still shows the rest', async () => {
    install({ details: { ...DETAILS, deezer: null } });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'About' }));

    expect(await screen.findByRole('region', { name: 'MusicBrainz' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Deezer' })).not.toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Appears on' })).toBeInTheDocument();
  });

  it('shows the current album and ten releases, then all of them with a filter after Show all', async () => {
    const releases = [
      release(0, { title: 'Current One', isCurrent: true, date: '2013-01-01' }),
      // A compilation sorts after the originals even though it is the oldest.
      release(1, { title: 'Old Compilation', secondaryTypes: ['Compilation'], date: '1990-01-01' }),
      ...Array.from({ length: 13 }, (_, index) => release(index + 2)),
    ];
    install({ details: { ...DETAILS, releases } });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'About' }));

    const appears = await screen.findByRole('region', { name: 'Appears on' });

    // The current album and the first ten others: Release 02..11, no compilation yet.
    expect(within(appears).getByText('Current One')).toBeInTheDocument();
    expect(within(appears).getByText('Release 02')).toBeInTheDocument();
    expect(within(appears).getByText('Release 11')).toBeInTheDocument();
    expect(within(appears).queryByText('Release 12')).not.toBeInTheDocument();
    expect(within(appears).queryByText('Old Compilation')).not.toBeInTheDocument();
    expect(within(appears).queryByRole('textbox', { name: 'Filter releases' })).not.toBeInTheDocument();

    await user.click(within(appears).getByRole('button', { name: 'Show all 15' }));

    expect(within(appears).getByText('Release 14')).toBeInTheDocument();
    expect(within(appears).getByText('Old Compilation')).toBeInTheDocument();

    await user.type(within(appears).getByRole('textbox', { name: 'Filter releases' }), 'compil');

    expect(within(appears).getByText('Old Compilation')).toBeInTheDocument();
    expect(within(appears).queryByText('Release 14')).not.toBeInTheDocument();
  });

  it('offers Last.fm only when the details carry it, as plain text with safe links', async () => {
    install({ details: { ...DETAILS, lastFm: LAST_FM } });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'Last.fm' }));

    expect(await screen.findByText('1,234,567')).toBeInTheDocument();
    expect(screen.getByText('9,876,543')).toBeInTheDocument();
    expect(screen.getByText('electronic')).toBeInTheDocument();
    // The wiki's markup is shown as text, not parsed: the tag is gone and there is no injected link.
    expect(screen.getByText('A funky single. Read more')).toBeInTheDocument();
    expect(document.querySelector('a[href="https://www.last.fm/evil"]')).toBeNull();

    const more = screen.getAllByRole('link', { name: /Read more on Last.fm/ })[0];

    expect(more).toHaveAttribute('href', 'https://www.last.fm/music/Daft+Punk/_/Get+Lucky');
    expect(more).toHaveAttribute('rel', 'noreferrer noopener');
    expect(screen.getByText('French electronic duo.')).toBeInTheDocument();

    // A track not in the library links to Last.fm; one that is links to its song page.
    expect(screen.getByRole('link', { name: 'Chic – Le Freak' })).toHaveAttribute(
      'href',
      'https://www.last.fm/music/Chic/_/Le+Freak',
    );
    expect(screen.getByRole('link', { name: 'Daft Punk – Lose Yourself to Dance' })).toHaveAttribute(
      'href',
      '/song/13',
    );
  });

  it('has no Last.fm tab without a Last.fm section', async () => {
    install();

    renderApp();

    await screen.findByRole('tab', { name: 'About' });

    expect(screen.queryByRole('tab', { name: 'Last.fm' })).not.toBeInTheDocument();
  });

  it('asks for the lyrics only when the Lyrics tab opens, and shows synced lyrics without timestamps', async () => {
    install({
      lyrics: { source: 'lrclib', synced: '[ar:Daft Punk]\n[00:12.30]Here I am\n[00:15.00]Come on', plain: null },
    });
    const user = userEvent.setup();

    renderApp();

    await screen.findByRole('heading', { name: 'Get Lucky' });
    await screen.findByRole('tab', { name: 'Lyrics' });

    expect(requested().some((url) => url.endsWith('/lyrics'))).toBe(false);

    await user.click(screen.getByRole('tab', { name: 'Lyrics' }));

    expect(await screen.findByText('Here I am')).toBeInTheDocument();
    expect(screen.getByText('Come on')).toBeInTheDocument();
    expect(screen.queryByText(/00:12/)).not.toBeInTheDocument();
    expect(screen.getByText('Source: LRCLIB')).toBeInTheDocument();
    expect(requested().filter((url) => url.endsWith('/lyrics'))).toHaveLength(1);
  });

  it('says so when there are no lyrics', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'Lyrics' }));

    expect(await screen.findByText('No lyrics found.')).toBeInTheDocument();
  });

  it("lists the song's history from the songId-filtered endpoint", async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'History' }));

    expect(await screen.findByText('Imported')).toBeInTheDocument();
    expect(requested().some((url) => url.includes('/api/v1/history') && url.includes('songId=12'))).toBe(true);
  });

  it("asks for the song's queue and blocklist on the last tab", async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'Queue & blocklist' }));

    expect(await screen.findByText('Nothing is downloading.')).toBeInTheDocument();
    expect(await screen.findByText('Nothing is blocked.')).toBeInTheDocument();
    expect(requested().some((url) => url.includes('/api/v1/queue') && url.includes('songId=12'))).toBe(true);
    expect(requested().some((url) => url.includes('/api/v1/blocklist') && url.includes('songId=12'))).toBe(true);
  });

  it('shows a not-found state, with a link back to the Library, for an unknown id', async () => {
    resetLocation('/song/999');
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      return new Response('not found', { status: 404 });
    });

    renderApp();

    expect(await screen.findByRole('heading', { name: 'Song not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to Library' })).toHaveAttribute('href', '/library');
  });

  it('starts the automatic search with the SongSearch command', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Search' }));

    await waitFor(() => expect(mock.calls.some((call) => call.url.endsWith('/api/v1/command'))).toBe(true));

    const post = vi
      .mocked(globalThis.fetch)
      .mock.calls.map(([input]) => input)
      .find((input): input is Request => input instanceof Request && input.url.endsWith('/api/v1/command'));

    expect(post?.method).toBe('POST');
    expect(JSON.parse(await (post?.clone().text() ?? Promise.resolve('{}')))).toEqual({
      name: 'SongSearch',
      songId: 12,
    });
  });

  it('runs the interactive search inline under the header', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Interactive search' }));

    const panel = await screen.findByRole('region', { name: 'Interactive search' });

    expect(await within(panel).findByText('No candidate came back for this song.')).toBeInTheDocument();
    // Inline, not a dialog.
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(requested().some((url) => url.includes('/api/v1/release') && url.includes('songId=12'))).toBe(true);
  });

  it('opens the Change album dialog from the song page, with a filter once there are many options', async () => {
    const many = Array.from({ length: 12 }, (_, index) => ({
      ...ALBUM_OPTIONS[1],
      key: `many-${index}`,
      mbReleaseGroupId: null,
      title: index === 5 ? 'Needle Album' : `Hits ${index}`,
      isCurrent: false,
    }));
    install({ albumOptions: [ALBUM_OPTIONS[0], ...many] });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(await within(dialog).findByRole('textbox', { name: 'Filter albums' }), 'needle');

    expect(within(dialog).getByRole('radio', { name: /Needle Album/ })).toBeInTheDocument();
    expect(within(dialog).queryByRole('radio', { name: /Hits 3/ })).not.toBeInTheDocument();
  });

  it('shows no filter box in Change album when there are ten options or fewer', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');

    await within(dialog).findByRole('radio', { name: /Singles/ });
    expect(within(dialog).queryByRole('textbox', { name: 'Filter albums' })).not.toBeInTheDocument();
  });

  it('groups editions of one album in Change album: one row, expandable, radios only on the editions', async () => {
    const options = [
      ALBUM_OPTIONS[0],
      edition(1),
      edition(2),
      edition(3),
      { ...ALBUM_OPTIONS[1], key: 'other', mbReleaseGroupId: null, title: 'Other Hits' },
    ];
    install({ albumOptions: options });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');

    // The group is one row with the earliest year and a count; its editions (and their radios) are hidden.
    expect(await within(dialog).findByText(/2003 · 3 editions/)).toBeInTheDocument();
    expect(within(dialog).getAllByText('8 Mile')).toHaveLength(1);
    expect(within(dialog).getAllByRole('radio')).toHaveLength(2);

    await user.click(within(dialog).getByRole('button', { name: 'Show the editions of 8 Mile' }));

    const editions = within(dialog).getByRole('group', { name: 'Editions of 8 Mile' });

    expect(within(editions).getAllByRole('radio')).toHaveLength(3);
    expect(within(editions).getByRole('radio', { name: /2005 · Official/ })).toBeInTheDocument();
    expect(within(editions).getByRole('radio', { name: /16 tracks/ })).toBeInTheDocument();
  });

  it('starts the group that holds the current album expanded in Change album', async () => {
    install({ albumOptions: [edition(1), edition(2, { isCurrent: true }), edition(3)] });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');
    const editions = await within(dialog).findByRole('group', { name: 'Editions of 8 Mile' });

    expect(within(editions).getAllByRole('radio')).toHaveLength(3);
    expect(within(editions).getByRole('radio', { checked: true })).toBeInTheDocument();
  });

  it('filters the grouped Change album list by group title', async () => {
    const many = Array.from({ length: 12 }, (_, index) => ({
      ...ALBUM_OPTIONS[1],
      key: `many-${index}`,
      mbReleaseGroupId: null,
      title: `Hits ${index}`,
      isCurrent: false,
    }));
    install({ albumOptions: [ALBUM_OPTIONS[0], edition(1), edition(2), ...many] });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Change album…' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(await within(dialog).findByRole('textbox', { name: 'Filter albums' }), '8 mile');

    expect(within(dialog).getByText(/2 editions/)).toBeInTheDocument();
    expect(within(dialog).queryByText('Hits 3')).not.toBeInTheDocument();
  });

  it('groups editions in Appears on, counting albums not editions', async () => {
    const releases = [
      release(0, { title: 'Current One', isCurrent: true, date: '2013-01-01' }),
      ...[1, 2, 3].map((index) =>
        release(20 + index, {
          title: '8 Mile',
          mbReleaseGroupId: GROUP_ID,
          date: `${2002 + index}-11-12`,
          totalTracks: index === 3 ? 16 : 22,
        }),
      ),
      ...Array.from({ length: 11 }, (_, index) => release(index + 2)),
    ];
    install({ details: { ...DETAILS, releases } });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('tab', { name: 'About' }));

    const appears = await screen.findByRole('region', { name: 'Appears on' });

    // 1 current + 1 group + 11 singles = 13 albums, not 15 releases; the group is one row.
    expect(within(appears).getAllByText('8 Mile')).toHaveLength(1);
    expect(within(appears).getByText(/2003 · 3 editions/)).toBeInTheDocument();
    expect(within(appears).getByRole('button', { name: 'Show all 13' })).toBeInTheDocument();

    await user.click(within(appears).getByRole('button', { name: 'Show the editions of 8 Mile' }));

    const editions = within(appears).getByRole('group', { name: 'Editions of 8 Mile' });

    expect(within(editions).getByText(/of 16/)).toBeInTheDocument();
    expect(within(editions).queryByRole('radio')).not.toBeInTheDocument();

    await user.click(within(appears).getByRole('button', { name: 'Show all 13' }));
    await user.type(within(appears).getByRole('textbox', { name: 'Filter releases' }), '8 mile');

    expect(within(appears).getByText(/3 editions/)).toBeInTheDocument();
    expect(within(appears).queryByText('Current One')).not.toBeInTheDocument();
  });

  it('highlights Library in the sidebar on a song page', async () => {
    install();

    renderApp();

    await screen.findByRole('heading', { name: 'Get Lucky' });
    expect(screen.getByRole('link', { name: 'Library' })).toHaveAttribute('data-active', 'true');
    expect(screen.getByRole('link', { name: 'Wanted' })).not.toHaveAttribute('data-active');
  });

  it('opens the Convert dialog from the song page', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Convert…' }));
    expect(await screen.findByRole('dialog', { name: 'Convert' })).toBeInTheDocument();
  });

  it('saves the monitored flag from the header switch', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('switch', { name: 'Monitored' }));

    await waitFor(() => {
      const put = vi
        .mocked(globalThis.fetch)
        .mock.calls.map(([input]) => input)
        .find((input): input is Request => input instanceof Request && input.method === 'PUT');

      expect(put?.url).toContain('/api/v1/song/12');
    });
  });
});
