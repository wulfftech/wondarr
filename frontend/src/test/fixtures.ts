import type { HealthEntry, TaskResource } from '../api/types';

/** Canned API payloads. They mirror what the backend emits; no test reaches the network. */

export const SYSTEM_STATUS = {
  appName: 'Compilarr',
  instanceName: 'Compilarr',
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
  packageAuthor: 'Compilarr contributors',
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
    isDefault: true,
  },
];
