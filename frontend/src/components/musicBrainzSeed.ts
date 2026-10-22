/**
 * Adding a song MusicBrainz does not have, from the Match queue (DECISIONS build session 14 #33).
 * MusicBrainz has no API for adding a release: its release editor takes a form POST that fills it in
 * ("seeding"), the user checks it and submits it under their own account, and MusicBrainz then sends the
 * browser to `redirect_uri` with `release_mbid` added. Wondarr builds that form here and settles the file
 * when the browser comes back.
 */

import type { MatchQueueItemResource } from '../api/references';

/** Where MusicBrainz' release editor takes a seeded POST. */
export const MUSICBRAINZ_ADD_RELEASE = 'https://musicbrainz.org/release/add';

/** The parameter of the address MusicBrainz sends the browser back to that names the file. */
export const RETURN_FILE_PARAM = 'musicBrainzFor';

/** The parameter that carries the one-time token the seed was opened with. */
export const RETURN_SEED_PARAM = 'seed';

/** The parameter MusicBrainz adds: the MBID of the release that was submitted. */
export const RETURN_RELEASE_PARAM = 'release_mbid';

/** Where the seeds this browser opened are remembered until MusicBrainz sends it back. */
export const SEED_STORAGE_KEY = 'wondarr.match.musicBrainzSeeds';

/** How long an opened seed is remembered: long enough to add an artist and a release, and to sleep on it. */
const SEED_LIFETIME_MS = 7 * 24 * 60 * 60 * 1000;

/** How many opened seeds are remembered at most; the oldest go first. */
const MAX_SEEDS = 20;

/** A MusicBrainz id. */
const MBID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A leading track number ("01 - ", "7. ") or the underscores some rippers put in front of a title. */
const LEADING_NOISE = /^(?:_+\s*|\d{1,3}\s*(?:\.|-)\s+)/;

/** What the form says about the song. Every text is trimmed before it is used. */
export interface SeedInput {
  title: string;
  artist: string;
  /** The file's length, which becomes the track's. */
  durationMs: number | null;
  /** `YYYY`, `YYYY-MM`, `YYYY-MM-DD`, or empty. */
  releaseDate: string;
  /** Where the song was released, for example its YouTube video; empty for none. */
  link: string;
  /** What Wondarr read about the link, for the edit note; `null` when it read nothing. */
  linkDetails: { channel: string | null; uploadDate: string | null } | null;
  /** The absolute address MusicBrainz sends the browser back to. */
  returnTo: string;
}

/** One form field: the name the release editor reads, and its value. */
export type SeedField = [name: string, value: string];

/** A seed this browser opened: which file, under which token, and when. */
export interface PendingSeed {
  fileId: number;
  token: string;
  path: string;
  openedAt: number;
  /**
   * The release MusicBrainz sent the browser back with, once it has: kept until the file is settled or
   * the user sets it aside, so a failed first try is not lost with the tab.
   */
  releaseId?: string;
}

/** Where MusicBrainz sent the browser back to: the file, the release, and whether this browser opened the seed. */
export interface MusicBrainzReturn {
  fileId: number;
  releaseId: string;
  token: string;
  /** The seed this browser opened for that file under that token, or `null` when it did not. */
  pending: PendingSeed | null;
}

/**
 * What the form starts with: the file's own tags, else its name ("Artist - Title", or the title under
 * its artist's folder).
 */
export function seedDefaults(item: MatchQueueItemResource): { title: string; artist: string } {
  const parts = item.relativePath.split('/');
  // The track number goes first, so "01 - Title" is not read as an artist called "01".
  const base = (parts.pop() ?? '')
    .replace(/\.[^.]+$/, '')
    .trim()
    .replace(LEADING_NOISE, '')
    .trim();
  const dashed = base.split(/\s+-\s+/);
  const fromName =
    dashed.length === 2
      ? { artist: (dashed[0] ?? '').trim(), title: (dashed[1] ?? '').trim() }
      : { artist: (parts[0] ?? '').trim(), title: base };

  const tagged = (value: string | null | undefined) => (value === null || value === undefined ? '' : value.trim());

  return {
    title: tagged(item.file.title) || fromName.title,
    artist: tagged(item.file.artist) || fromName.artist,
  };
}

/**
 * A release date as the release editor's year, month and day fields: `[]` when empty, `null` when the
 * text is not `YYYY`, `YYYY-MM` or a real `YYYY-MM-DD`.
 */
export function releaseDateParts(text: string): number[] | null {
  const trimmed = text.trim();

  if (trimmed === '') {
    return [];
  }

  const match = /^(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?$/.exec(trimmed);

  if (match === null) {
    return null;
  }

  const year = Number(match[1]);
  const month = match[2] === undefined ? undefined : Number(match[2]);
  const day = match[3] === undefined ? undefined : Number(match[3]);

  if (month !== undefined && (month < 1 || month > 12)) {
    return null;
  }

  if (month !== undefined && day !== undefined) {
    const date = new Date(Date.UTC(year, month - 1, day));

    if (date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
      return null;
    }
  }

  return [year, month, day].filter((part): part is number => part !== undefined);
}

/**
 * The edit note: where the song comes from, so the people who vote on edits can check it, and what filled
 * the form in. No path of the user's own goes in it: edit notes are public.
 */
export function editNote(input: SeedInput): string {
  const link = input.link.trim();
  const lines: string[] = [];

  if (link !== '') {
    const channel = input.linkDetails?.channel ?? null;
    const uploaded = input.linkDetails?.uploadDate ?? null;

    if (channel !== null && uploaded !== null) {
      lines.push(`Uploaded by the "${channel}" YouTube channel on ${uploaded}: ${link}`);
    } else if (channel !== null) {
      lines.push(`Uploaded by the "${channel}" YouTube channel: ${link}`);
    } else {
      lines.push(`Released at: ${link}`);
    }
  }

  lines.push("Title, artist and length filled in by Wondarr from the file's tags.");

  return lines.join('\n');
}

/**
 * The fields of the seeded POST, named as MusicBrainz' seeding documentation names them: a single with
 * one digital track, released worldwide, credited to the artist by name. The artist is not linked:
 * the release editor lets the user pick the MusicBrainz artist or add a new one.
 */
export function musicBrainzSeed(input: SeedInput): SeedField[] {
  const title = input.title.trim();
  const artist = input.artist.trim();
  const link = input.link.trim();
  const date = releaseDateParts(input.releaseDate) ?? [];

  const fields: SeedField[] = [
    ['name', title],
    ['type', 'Single'],
    ['status', 'official'],
    ['packaging', 'None'],
    ['artist_credit.names.0.name', artist],
    ['artist_credit.names.0.artist.name', artist],
    ['events.0.country', 'XW'],
  ];

  (['year', 'month', 'day'] as const).forEach((part, index) => {
    if (date[index] !== undefined) {
      fields.push([`events.0.date.${part}`, String(date[index])]);
    }
  });

  fields.push(
    ['mediums.0.format', 'Digital Media'],
    ['mediums.0.track.0.number', '1'],
    ['mediums.0.track.0.name', title],
  );

  if (input.durationMs !== null && Number.isFinite(input.durationMs) && input.durationMs > 0) {
    fields.push(['mediums.0.track.0.length', String(Math.round(input.durationMs))]);
  }

  if (link !== '') {
    // No link type: the release editor picks it from the address, or asks.
    fields.push(['urls.0.url', link]);
  }

  fields.push(['edit_note', editNote(input)], ['redirect_uri', input.returnTo]);

  return fields;
}

/** The address MusicBrainz sends the browser back to: the Match page, naming the file and the token. */
export function returnAddress(urlBase: string, fileId: number, token: string, origin = window.location.origin): string {
  const address = new URL(`${urlBase.replace(/\/+$/, '')}/match`, origin);

  address.searchParams.set(RETURN_FILE_PARAM, String(fileId));
  address.searchParams.set(RETURN_SEED_PARAM, token);

  return address.toString();
}

/**
 * Opens the seeded release editor in a new tab. The form is posted from the click itself, so no popup
 * blocker stands in the way. `noreferrer` leaves the page's address out of the request's Referer, but
 * MusicBrainz still learns Wondarr's address: it is the `redirect_uri` it sends the browser back to.
 */
export function openSeededEditor(fields: SeedField[], doc: Document = document): void {
  const form = doc.createElement('form');

  form.method = 'post';
  form.action = MUSICBRAINZ_ADD_RELEASE;
  form.target = '_blank';
  form.acceptCharset = 'UTF-8';
  form.setAttribute('rel', 'noopener noreferrer');
  form.hidden = true;

  for (const [name, value] of fields) {
    // A textarea keeps the edit note's line breaks; a hidden input would too, but says less about it.
    const field = value.includes('\n') ? doc.createElement('textarea') : doc.createElement('input');

    if (field instanceof HTMLInputElement) {
      field.type = 'hidden';
    }

    field.name = name;
    field.value = value;
    form.appendChild(field);
  }

  doc.body.appendChild(form);

  try {
    form.submit();
  } finally {
    form.remove();
  }
}

/** A fresh one-time token. `crypto.randomUUID` needs HTTPS, which a LAN address often is not. */
export function newSeedToken(): string {
  const bytes = new Uint8Array(16);

  crypto.getRandomValues(bytes);

  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

/** The seeds this browser opened that are still fresh, newest last; empty without storage. */
function readSeeds(now: number): PendingSeed[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(SEED_STORAGE_KEY) ?? '[]');

    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.filter(
      (seed): seed is PendingSeed =>
        typeof seed === 'object' &&
        seed !== null &&
        typeof (seed as PendingSeed).fileId === 'number' &&
        typeof (seed as PendingSeed).token === 'string' &&
        typeof (seed as PendingSeed).path === 'string' &&
        typeof (seed as PendingSeed).openedAt === 'number' &&
        now - (seed as PendingSeed).openedAt < SEED_LIFETIME_MS &&
        ((seed as PendingSeed).releaseId === undefined || MBID.test(String((seed as PendingSeed).releaseId))),
    );
  } catch {
    return [];
  }
}

function writeSeeds(seeds: PendingSeed[]): void {
  try {
    if (seeds.length === 0) {
      localStorage.removeItem(SEED_STORAGE_KEY);
    } else {
      localStorage.setItem(SEED_STORAGE_KEY, JSON.stringify(seeds.slice(-MAX_SEEDS)));
    }
  } catch {
    // No storage: the way back asks before it settles anything.
  }
}

/** Remembers a seed this browser is about to open, so the way back can settle the file without asking. */
export function rememberSeed(fileId: number, path: string, token: string, now = Date.now()): void {
  writeSeeds([...readSeeds(now), { fileId, token, path, openedAt: now }]);
}

/** The seed this browser opened for a file under a token, or `null`. Reading it does not use it up. */
export function findSeed(fileId: number, token: string, now = Date.now()): PendingSeed | null {
  return readSeeds(now).find((seed) => seed.fileId === fileId && seed.token === token) ?? null;
}

/**
 * Forgets a seed once it has been used or set aside. Other tabs hear of it through the `storage` event,
 * which is how the tab that opened the seed knows the file has been settled.
 */
export function forgetSeed(fileId: number, token: string, now = Date.now()): void {
  writeSeeds(readSeeds(now).filter((seed) => !(seed.fileId === fileId && seed.token === token)));
}

/**
 * Keeps the release MusicBrainz sent back with on the seed this browser opened, so the Match page can try
 * again after a reload or another visit until the file is settled or the user sets it aside.
 */
export function noteReturn(returned: MusicBrainzReturn, now = Date.now()): void {
  if (returned.pending === null) {
    return;
  }

  writeSeeds(
    readSeeds(now).map((seed) =>
      seed.fileId === returned.fileId && seed.token === returned.token
        ? { ...seed, releaseId: returned.releaseId }
        : seed,
    ),
  );
}

/** The newest way back this browser was given but has not settled yet, or `null`. */
export function unsettledReturn(now = Date.now()): MusicBrainzReturn | null {
  const seed = readSeeds(now)
    .filter((candidate) => candidate.releaseId !== undefined)
    .pop();

  return seed?.releaseId === undefined
    ? null
    : { fileId: seed.fileId, releaseId: seed.releaseId, token: seed.token, pending: seed };
}

/**
 * Reads the way back from MusicBrainz out of the Match page's address: `null` unless it names a file and
 * a release MBID. A token this browser did not open (a link someone else made, or no storage) leaves
 * `pending` empty, and the page asks before it settles anything.
 */
export function readMusicBrainzReturn(params: URLSearchParams, now = Date.now()): MusicBrainzReturn | null {
  const fileId = Number(params.get(RETURN_FILE_PARAM));
  const releaseId = params.get(RETURN_RELEASE_PARAM) ?? '';
  const token = params.get(RETURN_SEED_PARAM) ?? '';

  if (!Number.isInteger(fileId) || fileId <= 0 || !MBID.test(releaseId)) {
    return null;
  }

  return {
    fileId,
    releaseId: releaseId.toLowerCase(),
    token,
    pending: token === '' ? null : findSeed(fileId, token, now),
  };
}
