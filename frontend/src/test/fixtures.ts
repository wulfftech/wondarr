import type { TaskResource } from '../api/hooks';
import type { HealthEntry } from '../api/types';

/** Canned API payloads. They mirror what the backend emits; no test reaches the network. */

export const SYSTEM_STATUS = {
  appName: 'Wondarr',
  instanceName: 'Wondarr',
  version: '0.1.0-test',
  buildTime: '2026-01-01T00:00:00Z',
  isDebug: false,
  isProduction: true,
  isAdmin: false,
  isUserInteractive: false,
  startupPath: '/app',
  appData: '/config',
  osName: 'Linux',
  osVersion: '6.1.0',
  isNetCore: true,
  isLinux: true,
  isOsx: false,
  isWindows: false,
  isDocker: true,
  isContainerized: true,
  mode: 'console',
  branch: 'develop',
  authentication: 'none',
  databaseType: 'sqLite',
  databaseVersion: '3.46.0',
  migrationVersion: 4,
  urlBase: '',
  runtimeVersion: '10.0.0',
  runtimeName: 'netcore',
  startTime: '2026-01-01T00:00:00Z',
  packageVersion: '0.1.0-test',
  packageAuthor: 'Wondarr contributors',
  packageUpdateMechanism: 'docker',
} as const;

export const HEALTH_ENTRIES: HealthEntry[] = [
  { source: 'Database', type: 'ok', message: 'Database is up.', wikiUrl: null },
  { source: 'Config', type: 'warning', message: 'No download client is configured.', wikiUrl: null },
];

export const TASKS: TaskResource[] = [
  {
    id: 1,
    name: 'Heartbeat',
    taskName: 'Heartbeat',
    interval: 60,
    lastExecution: '2026-01-01T00:00:00Z',
    lastStartTime: '2026-01-01T00:00:00Z',
    lastDuration: '00:00:01',
    nextExecution: '2026-01-01T01:00:00Z',
    lastResult: 'successful',
  },
];

/**
 * The list endpoints all answer in the same envelope. The enum-carrying fixtures below (libraries
 * especially) are written in the wire shape: the API serialises enums as camelCase strings even
 * though the OpenAPI document types them as integers.
 */
export function paged<T>(records: T[]): {
  page: number;
  pageSize: number;
  sortKey: string;
  sortDirection: string;
  totalRecords: number;
  records: T[];
} {
  return {
    page: 1,
    pageSize: 50,
    sortKey: 'added',
    sortDirection: 'descending',
    totalRecords: records.length,
    records,
  };
}

export const SONGS = [
  {
    id: 1,
    title: 'Teardrop',
    artistCredit: 'Massive Attack',
    primaryArtistId: 1,
    primaryArtistName: 'Massive Attack',
    mbRecordingId: null,
    deezerId: null,
    isrcs: [],
    durationMs: 369000,
    versionFlags: [],
    monitored: true,
    qualityProfileId: 1,
    libraryId: 1,
    addedBy: 'user',
    added: '2026-01-02T00:00:00Z',
    hasFile: false,
    qualityId: null,
    albumContext: {
      kind: 'album',
      albumTitle: 'Mezzanine',
      albumArtist: 'Massive Attack',
      albumKey: 'mezzanine',
      mbReleaseId: null,
      mbReleaseGroupId: null,
      trackNo: 1,
      discNo: 1,
      totalTracks: 11,
      date: '1998-04-20',
      originalDate: null,
      coverUrl: null,
      isVariousArtists: false,
    },
  },
];

export const HISTORY_ITEMS = [
  {
    id: 1,
    songId: 1,
    song: SONGS[0],
    eventType: 'imported',
    date: '2026-01-03T00:00:00Z',
    sourceInstanceId: null,
    qualityId: 20,
    data: {},
  },
];

export const BLOCKLIST_ITEMS = [
  {
    id: 7,
    songId: 1,
    sourceType: 'soulseek',
    blocklistKey: 'a1b2c3d4',
    reason: 'duration mismatch',
    date: '2026-01-04T00:00:00Z',
    expiresAt: null,
  },
];

export const QUALITY_DEFINITIONS = [
  {
    id: 10,
    name: 'MP3-256',
    group: 'Mid lossy',
    rank: 5,
    codec: 'MP3',
    lossless: false,
    minBitrate: 256,
    maxBitrate: 256,
    bitDepth: null,
  },
  {
    id: 20,
    name: 'FLAC',
    group: 'Lossless',
    rank: 7,
    codec: 'FLAC',
    lossless: true,
    minBitrate: null,
    maxBitrate: null,
    bitDepth: 16,
  },
];

/** The items run worst → best, as the API stores and sends them. */
export const QUALITY_PROFILES = [
  {
    id: 1,
    name: 'Standard 320',
    upgradeAllowed: true,
    cutoff: 10,
    minScore: 0,
    durationToleranceMs: 3000,
    items: [
      { name: 'Trash lossy', qualities: [{ id: 30, name: 'MP3-8' }], allowed: false },
      { name: 'Mid lossy', qualities: [{ id: 10, name: 'MP3-256' }], allowed: true },
      { name: 'Lossless', qualities: [{ id: 20, name: 'FLAC' }], allowed: true },
    ],
  },
];

/**
 * The two songs the Library page lists: one filed under a real album, one under its artist's
 * Singles pseudo-album. The values mirror what the backend serialises, enums as camelCase strings.
 */
export const LIBRARY_SONGS = [
  {
    id: 12,
    title: 'Get Lucky',
    artistCredit: 'Daft Punk',
    primaryArtistId: 7,
    primaryArtistName: 'Daft Punk',
    mbRecordingId: '833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d',
    deezerId: null,
    isrcs: ['USQX91300516'],
    durationMs: 369000,
    versionFlags: ['radio_edit'],
    monitored: true,
    qualityProfileId: 1,
    libraryId: 1,
    addedBy: 'user',
    added: '2026-01-05T00:00:00Z',
    hasFile: true,
    qualityId: 20,
    albumContext: {
      kind: 'album',
      albumTitle: 'Random Access Memories',
      albumArtist: 'Daft Punk',
      albumKey: '9c1b3a2f-random-access-memories',
      mbReleaseId: '9c1b3a2f-0000-0000-0000-000000000000',
      mbReleaseGroupId: null,
      trackNo: 8,
      discNo: 1,
      totalTracks: 13,
      date: '2013-05-17',
      originalDate: null,
      coverUrl: null,
      isVariousArtists: false,
    },
  },
  {
    id: 13,
    title: 'Xtal',
    artistCredit: 'Aphex Twin',
    primaryArtistId: 8,
    primaryArtistName: 'Aphex Twin',
    mbRecordingId: null,
    deezerId: 66877419,
    isrcs: [],
    durationMs: 293000,
    versionFlags: [],
    monitored: false,
    qualityProfileId: 1,
    libraryId: 1,
    addedBy: 'user',
    added: '2026-01-06T00:00:00Z',
    hasFile: false,
    qualityId: null,
    albumContext: {
      kind: 'pseudoSingles',
      albumTitle: 'Singles',
      albumArtist: 'Aphex Twin',
      albumKey: 'singles',
      mbReleaseId: null,
      mbReleaseGroupId: null,
      trackNo: null,
      discNo: null,
      totalTracks: null,
      date: null,
      originalDate: null,
      coverUrl: null,
      isVariousArtists: false,
    },
  },
];

export const ARTISTS = [
  { id: 7, name: 'Daft Punk', sortName: 'Daft Punk', mbArtistId: null, deezerId: null, songCount: 1 },
  { id: 8, name: 'Aphex Twin', sortName: 'Aphex Twin', mbArtistId: null, deezerId: null, songCount: 1 },
];

/** The releases "Get Lucky" could be filed under, plus the artist's Singles pseudo-album. */
export const ALBUM_OPTIONS = [
  {
    key: '9c1b3a2f-random-access-memories',
    mbReleaseId: '9c1b3a2f-0000-0000-0000-000000000000',
    mbReleaseGroupId: null,
    title: 'Random Access Memories',
    albumArtist: 'Daft Punk',
    primaryType: 'Album',
    secondaryTypes: [],
    status: 'Official',
    date: '2013-05-17',
    trackNo: 8,
    totalTracks: 13,
    isCurrent: true,
    coverUrl: 'https://coverartarchive.org/release/9c1b3a2f-0000-0000-0000-000000000000/front-250',
    isVariousArtists: false,
    originalDate: null,
    discNo: 1,
  },
  {
    key: 'b7a1c2d3-so-fresh-summer-2004',
    mbReleaseId: 'b7a1c2d3-0000-0000-0000-000000000000',
    mbReleaseGroupId: 'b7a1c2d3-1111-0000-0000-000000000000',
    title: 'So Fresh: The Hits of Summer 2004',
    albumArtist: 'Various Artists',
    primaryType: 'Album',
    secondaryTypes: ['Compilation'],
    status: 'Official',
    date: '2004-12-01',
    trackNo: null,
    totalTracks: 22,
    isCurrent: false,
    coverUrl: 'https://coverartarchive.org/release/b7a1c2d3-0000-0000-0000-000000000000/front-250',
    isVariousArtists: true,
    originalDate: '1999-06-01',
    discNo: null,
  },
  {
    key: 'singles',
    mbReleaseId: null,
    mbReleaseGroupId: null,
    title: 'Singles',
    albumArtist: 'Daft Punk',
    primaryType: null,
    secondaryTypes: [],
    status: null,
    date: null,
    trackNo: null,
    totalTracks: null,
    isCurrent: false,
    coverUrl: null,
    isVariousArtists: false,
    originalDate: null,
    discNo: null,
  },
];

/** What `POST /api/v1/song` answers with for "Get Lucky". */
export const ADDED_SONG = {
  ...LIBRARY_SONGS[0],
  id: 41,
  added: '2026-01-07T00:00:00Z',
  hasFile: false,
  qualityId: null,
};

/** Two candidates for "Daft Punk - Get Lucky": a MusicBrainz one and a Deezer-only one. */
export const LOOKUP_RESULTS = [
  {
    source: 'musicbrainz',
    mbRecordingId: '833f5a5d-9c2a-4a1c-9b6f-2f5b2f0a1c3d',
    deezerId: null,
    title: 'Get Lucky',
    artistCredit: 'Daft Punk feat. Pharrell Williams',
    durationMs: 369000,
    disambiguation: 'album version',
    versionFlags: ['radio_edit'],
    firstReleaseDate: '2013-04-19',
    releaseTypes: ['Album', 'Single'],
    albumTitle: 'Random Access Memories',
    coverUrl: null,
    isrcs: ['USQX91300516'],
    score: 100,
    viaIsrc: false,
    existingSongId: null,
  },
  {
    source: 'deezer',
    mbRecordingId: null,
    deezerId: 66877419,
    title: 'Get Lucky (Radio Edit)',
    artistCredit: 'Daft Punk',
    durationMs: 248000,
    disambiguation: null,
    versionFlags: [],
    firstReleaseDate: '2013-04-19',
    releaseTypes: ['Single'],
    albumTitle: 'Get Lucky',
    coverUrl: null,
    isrcs: ['GBDUW1300012'],
    score: 84,
    viaIsrc: false,
    existingSongId: null,
  },
];

/** A fresh Deezer preview link, as `GET /api/v1/preview` answers. */
export const PREVIEW = { url: 'https://cdns-preview-1.dzcdn.net/stream/get-lucky.mp3' };

/** What `POST /api/v1/song/bulk` accepts: the stored list, its command and its line count. */
export const BULK_ACCEPTED = { importListId: 5, commandId: 9, lineCount: 50 };

/** The bulk add's command while it is still resolving. */
export const COMMAND_RUNNING = {
  id: 9,
  name: 'BulkAddSongs',
  commandName: 'BulkAddSongs',
  message: 'Resolved 12 of 50 lines',
  body: null,
  priority: 'normal',
  status: 'started',
  result: 'unknown',
  queued: '2026-01-08T00:00:00Z',
  started: '2026-01-08T00:00:01Z',
  ended: null,
  duration: null,
  exception: null,
  trigger: 'manual',
  stateChangeTime: '2026-01-08T00:00:01Z',
};

/** The same command once the resolve has finished. */
export const COMMAND_COMPLETED = {
  ...COMMAND_RUNNING,
  message: 'Resolved 50 of 50 lines',
  status: 'completed',
  result: 'successful',
  ended: '2026-01-08T00:01:00Z',
  duration: '00:00:59',
};

/** The stored paste list, with three lines left for the review screen. */
export const IMPORT_LIST = {
  id: 5,
  type: 'paste',
  name: 'Pasted 2026-01-08',
  created: '2026-01-08T00:00:00Z',
  lastSyncedAt: '2026-01-08T00:01:00Z',
  counts: { pending: 0, added: 47, unresolved: 3, skipped: 0 },
};

/** Two unresolved lines, the first with a candidate to pick. */
export const IMPORT_LIST_ITEMS = [
  {
    id: 5,
    importListId: 5,
    line: 1,
    text: 'Aphex Twin - Xtal',
    artist: 'Aphex Twin',
    title: 'Xtal',
    state: 'unresolved',
    reason: 'No provider matched closely enough.',
    songId: null,
    candidates: [
      {
        source: 'musicbrainz',
        mbRecordingId: 'a1b2c3d4-0000-0000-0000-000000000001',
        deezerId: null,
        title: 'Xtal',
        artistCredit: 'Aphex Twin',
        durationMs: 293000,
        score: 71.5,
      },
    ],
  },
  {
    id: 6,
    importListId: 5,
    line: 2,
    text: 'Nobody - Nothing At All',
    artist: 'Nobody',
    title: 'Nothing At All',
    state: 'unresolved',
    reason: 'Neither provider knows this one.',
    songId: null,
    candidates: [],
  },
];

/**
 * Two reference libraries, one read-only and one that adopts into `LIBRARIES[0]`, with the counts the
 * list renders as badges: the read-only one has three files waiting in the Match queue.
 */
export const REFERENCE_LIBRARIES = [
  {
    id: 3,
    name: 'Old music',
    rootPath: '/data/old-music',
    mode: 'reference',
    libraryId: 1,
    enabled: true,
    lastScannedAt: '2026-01-08T00:00:00Z',
    lastScanMessage: 'Scanned 412 files; 109 identified, 3 need review.',
    counts: {
      total: 412,
      pending: 0,
      identified: 400,
      ambiguous: 3,
      unmatched: 0,
      adopted: 0,
      unreadable: 4,
      missing: 5,
      skipped: 0,
    },
  },
  {
    id: 4,
    name: 'Bought music',
    rootPath: '/data/bought',
    mode: 'adopt',
    libraryId: 1,
    enabled: true,
    lastScannedAt: null,
    lastScanMessage: null,
    counts: {
      total: 0,
      pending: 0,
      identified: 0,
      ambiguous: 0,
      unmatched: 0,
      adopted: 0,
      unreadable: 0,
      missing: 0,
      skipped: 0,
    },
  },
];

/**
 * Two files the Match queue is asking about: an ambiguous one with two ranked candidates, and an
 * unmatched one with none. The file's probe is 3 s shorter than the best candidate.
 */
export const MATCH_QUEUE_ITEMS = [
  {
    id: 71,
    referenceLibraryId: 3,
    relativePath: 'Daft Punk/Discovery/04 - Harder Better.flac',
    state: 'ambiguous',
    file: {
      title: 'Harder Better Faster Stronger',
      artist: 'Daft Punk',
      album: 'Discovery',
      trackNumber: 4,
      durationMs: 224000,
      codec: 'flac',
      bitrate: 900,
      isrc: null,
      mbRecordingId: null,
    },
    candidates: [
      {
        rank: 1,
        score: 0.87,
        reason: 'search 9d3f5a1e-1111-4222-8333-444455556666, length differs by 3 s',
        source: 'musicbrainz',
        mbRecordingId: 'a1b2c3d4-0000-0000-0000-000000000002',
        deezerId: null,
        title: 'Harder, Better, Faster, Stronger',
        artistCredit: 'Daft Punk',
        durationMs: 227000,
        albumTitle: 'Discovery',
      },
      {
        rank: 2,
        score: 0.62,
        reason: 'search 62',
        source: 'deezer',
        mbRecordingId: null,
        deezerId: 66877419,
        title: 'Harder, Better, Faster, Stronger (Live)',
        artistCredit: 'Daft Punk',
        durationMs: 260000,
        albumTitle: 'Alive 2007',
      },
    ],
    message: null,
  },
  {
    id: 72,
    referenceLibraryId: 3,
    relativePath: 'Unknown artist/04 track04.mp3',
    state: 'unmatched',
    file: {
      title: null,
      artist: null,
      album: null,
      trackNumber: null,
      durationMs: 201000,
      codec: 'mp3',
      bitrate: 320,
      isrc: null,
      mbRecordingId: null,
    },
    candidates: [],
    message: 'Nothing matched closely enough.',
  },
];

/** What `GET /api/v1/plex` answers before an account token is stored. */
export const PLEX_STATE_SIGNED_OUT = {
  signedIn: false,
  serverUrl: null,
  serverName: null,
  machineIdentifier: null,
  clientIdentifier: 'wondarr-2f7c1a9b',
};

/** The same state once an account is signed in but no server has been chosen yet. */
export const PLEX_STATE_SIGNED_IN_UNSELECTED = {
  ...PLEX_STATE_SIGNED_OUT,
  signedIn: true,
};

/** The state with a server selected, as `PUT /api/v1/plex/server` answers. */
export const PLEX_STATE_SIGNED_IN = {
  ...PLEX_STATE_SIGNED_OUT,
  signedIn: true,
  serverUrl: 'http://10.0.0.5:32400',
  serverName: 'Home NAS',
  machineIdentifier: 'a1b2c3d4e5f6',
};

/**
 * Two servers the account can reach. The owned one lists its relay connection first, so a page that
 * offers the slowest way in last is visibly reordering them.
 */
export const PLEX_SERVERS = [
  {
    name: 'Home NAS',
    machineIdentifier: 'a1b2c3d4e5f6',
    owned: true,
    productVersion: '1.43.4.1000',
    connections: [
      { uri: 'https://relay-abc123.plex.direct:8443', local: false, relay: true },
      { uri: 'http://10.0.0.5:32400', local: true, relay: false },
      { uri: 'https://10-0-0-5.abc123.plex.direct:32400', local: false, relay: false },
    ],
  },
  {
    name: "Sam's Plex",
    machineIdentifier: 'ffeeddccbbaa',
    owned: false,
    productVersion: null,
    connections: [{ uri: 'https://shared.example.com:32400', local: false, relay: false }],
  },
];

/** The music sections of the selected server, as `GET /api/v1/plex/sections` sends them. */
export const PLEX_SECTIONS = [
  { key: '3', title: 'Music', locations: ['/music'] },
  { key: '5', title: 'Singles', locations: ['/data/singles'] },
];

export const LIBRARIES = [
  {
    id: 1,
    name: 'Music',
    rootPath: '/data/music',
    layout: 'plexamp',
    namingTemplate: '{Album Artist Name}/{Album Title}/{track:00} - {Track Title}',
    sidecarOptions: {},
    albumPolicy: 'fewestAlbums',
    minTracksPerRealAlbum: 2,
    plexSectionId: null,
    plexLibraryPath: null,
    isDefault: true,
  },
];

/** The same library with a Plex section linked, as `GET /api/v1/library/1` answers after a link. */
export const LIBRARY_PLEX_LINKED = {
  ...LIBRARIES[0],
  plexSectionId: '3',
  plexLibraryPath: '/music',
};

/**
 * The notification providers and the settings form each one wants, as `GET
 * /api/v1/notification/schema` sends it. The Webhook's headers are the one advanced field, so a page
 * that ignores `advanced` puts them beside the URL.
 */
export const NOTIFICATION_SCHEMA = [
  {
    implementation: 'Webhook',
    fields: [
      {
        name: 'url',
        label: 'URL',
        type: 'url',
        required: true,
        helpText: 'Where the JSON body is sent.',
        options: null,
        secret: false,
        advanced: false,
      },
      {
        name: 'method',
        label: 'Method',
        type: 'select',
        required: false,
        helpText: 'Which HTTP method to submit with.',
        options: ['POST', 'PUT'],
        secret: false,
        advanced: false,
      },
      {
        name: 'username',
        label: 'Username',
        type: 'text',
        required: false,
        helpText: 'For HTTP basic authentication.',
        options: null,
        secret: false,
        advanced: false,
      },
      {
        name: 'password',
        label: 'Password',
        type: 'password',
        required: false,
        helpText: 'For HTTP basic authentication.',
        options: null,
        secret: true,
        advanced: false,
      },
      {
        name: 'headers',
        label: 'Headers',
        type: 'keyValueList',
        required: false,
        helpText: 'Extra headers to send, as name/value pairs.',
        options: null,
        secret: false,
        advanced: true,
      },
    ],
  },
  {
    implementation: 'Discord',
    fields: [
      {
        name: 'webHookUrl',
        label: 'Webhook URL',
        type: 'url',
        required: true,
        helpText: "The channel's webhook URL.",
        options: null,
        secret: true,
        advanced: false,
      },
      {
        name: 'username',
        label: 'Username',
        type: 'text',
        required: false,
        helpText: "The name to post as, if not Discord's default.",
        options: null,
        secret: false,
        advanced: false,
      },
      {
        name: 'avatar',
        label: 'Avatar',
        type: 'url',
        required: false,
        helpText: 'The avatar URL to post the message with.',
        options: null,
        secret: false,
        advanced: false,
      },
    ],
  },
  {
    implementation: 'Apprise',
    fields: [
      {
        name: 'serverUrl',
        label: 'Server URL',
        type: 'url',
        required: true,
        helpText: 'The Apprise API server, including http(s):// and the port.',
        options: null,
        secret: false,
        advanced: false,
      },
    ],
  },
];

/**
 * Two stored notifications. The Discord one's webhook URL reads back masked, which is what the form
 * shows as its placeholder and what a save that leaves it alone sends back.
 */
export const NOTIFICATIONS = [
  {
    id: 1,
    name: 'Home webhook',
    implementation: 'Webhook',
    enabled: true,
    events: ['grab', 'import'],
    settings: { url: 'https://hooks.example.com/wondarr', method: 'POST', username: null, password: null, headers: [] },
  },
  {
    id: 2,
    name: 'Discord alerts',
    implementation: 'Discord',
    enabled: false,
    events: ['failure'],
    settings: { webHookUrl: '********', username: null, avatar: null },
  },
];

/**
 * What `GET /api/v1/library/1/compact` would do: one song whose file moves with it and one that only
 * changes album, so the table shows both the paths and the "album only" case.
 */
export const COMPACT_PLAN = {
  libraryId: 1,
  albumsBefore: 4,
  albumsAfter: 2,
  songsConsidered: 12,
  moves: [
    {
      songId: 12,
      title: 'Get Lucky',
      artistCredit: 'Daft Punk',
      from: { albumKey: 'singles', kind: 'pseudoSingles', albumTitle: 'Singles', albumArtist: 'Daft Punk' },
      to: {
        albumKey: '9c1b3a2f-random-access-memories',
        kind: 'album',
        albumTitle: 'Random Access Memories',
        albumArtist: 'Daft Punk',
      },
      fromPath: '/data/music/Daft Punk/Singles/08 - Get Lucky.flac',
      toPath: '/data/music/Daft Punk/Random Access Memories/08 - Get Lucky.flac',
    },
    {
      songId: 13,
      title: 'Xtal',
      artistCredit: 'Aphex Twin',
      from: { albumKey: 'singles', kind: 'pseudoSingles', albumTitle: 'Singles', albumArtist: 'Aphex Twin' },
      to: {
        albumKey: 'selected-ambient-works',
        kind: 'album',
        albumTitle: 'Selected Ambient Works',
        albumArtist: 'Aphex Twin',
      },
      fromPath: null,
      toPath: null,
    },
  ],
};

/** The same dry run for a library that is already where the policy would put it. */
export const COMPACT_PLAN_EMPTY = {
  ...COMPACT_PLAN,
  albumsBefore: 2,
  albumsAfter: 2,
  moves: [],
};

/** One field of a provider's settings form, in the server's schema shape. */
function schemaField(
  name: string,
  label: string,
  type: string,
  extra: Partial<{
    required: boolean;
    helpText: string | null;
    options: string[] | null;
    secret: boolean;
    advanced: boolean;
  }> = {},
) {
  return {
    name,
    label,
    type,
    required: extra.required ?? false,
    helpText: extra.helpText ?? null,
    options: extra.options ?? null,
    secret: extra.secret ?? false,
    advanced: extra.advanced ?? false,
  };
}

/** `GET /api/v1/indexer/schema`: Torznab and Newznab fix their protocol, Prowlarr lets the row choose. */
export const INDEXER_SCHEMA = [
  {
    type: 'torznab',
    protocol: 'torrent',
    protocolChoosable: false,
    fields: [
      schemaField('url', 'URL', 'url', { required: true }),
      schemaField('apiKey', 'API key', 'password', { secret: true }),
      schemaField('categories', 'Categories', 'text'),
    ],
  },
  {
    type: 'newznab',
    protocol: 'usenet',
    protocolChoosable: false,
    fields: [
      schemaField('url', 'URL', 'url', { required: true }),
      schemaField('apiKey', 'API key', 'password', { secret: true }),
    ],
  },
  {
    type: 'prowlarr',
    protocol: null,
    protocolChoosable: true,
    fields: [
      schemaField('url', 'URL', 'url', { required: true }),
      schemaField('apiKey', 'API key', 'password', { secret: true }),
    ],
  },
];

/** `GET /api/v1/indexer`: a Torznab feed on the default client and a Newznab feed with its own. */
export const INDEXERS = [
  {
    id: 1,
    name: 'Prowlarr torrents',
    type: 'torznab',
    protocol: 'torrent',
    enabled: true,
    priority: 25,
    downloadClientId: null,
    settings: { url: 'http://prowlarr:9696/1/', apiKey: '********', categories: '3000' },
  },
  {
    id: 2,
    name: 'Usenet indexer',
    type: 'newznab',
    protocol: 'usenet',
    enabled: false,
    priority: 10,
    downloadClientId: 4,
    settings: { url: 'https://indexer.example', apiKey: '********' },
  },
];

/** `GET /api/v1/downloadclient/schema`. */
export const DOWNLOAD_CLIENT_SCHEMA = [
  {
    type: 'qbittorrent',
    protocol: 'torrent',
    fields: [
      schemaField('host', 'Host', 'text', { required: true }),
      schemaField('port', 'Port', 'number'),
      schemaField('password', 'Password', 'password', { secret: true }),
      schemaField('remotePathMappings', 'Remote path mappings', 'keyValueList'),
    ],
  },
  {
    type: 'sabnzbd',
    protocol: 'usenet',
    fields: [
      schemaField('host', 'Host', 'text', { required: true }),
      schemaField('apiKey', 'API key', 'password', { required: true, secret: true }),
    ],
  },
];

/** `GET /api/v1/downloadclient`. */
export const DOWNLOAD_CLIENTS = [
  {
    id: 3,
    name: 'qBittorrent',
    type: 'qbittorrent',
    protocol: 'torrent',
    enabled: true,
    priority: 1,
    settings: {
      host: 'qbittorrent',
      port: 8080,
      password: '********',
      remotePathMappings: [{ key: '/downloads', value: '/data/torrents' }],
    },
  },
  {
    id: 4,
    name: 'SABnzbd',
    type: 'sabnzbd',
    protocol: 'usenet',
    enabled: true,
    priority: 1,
    settings: { host: 'sabnzbd', apiKey: '********' },
  },
];
