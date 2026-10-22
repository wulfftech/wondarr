import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MATCH_QUEUE_ITEMS } from '../test/fixtures';
import {
  editNote,
  findSeed,
  forgetSeed,
  musicBrainzSeed,
  MUSICBRAINZ_ADD_RELEASE,
  newSeedToken,
  noteReturn,
  openSeededEditor,
  readMusicBrainzReturn,
  releaseDateParts,
  rememberSeed,
  returnAddress,
  SEED_STORAGE_KEY,
  seedDefaults,
  unsettledReturn,
  type MusicBrainzReturn,
  type SeedInput,
} from './musicBrainzSeed';

const RELEASE_ID = '4587fe99-db0e-4553-a56a-164dd38ab380';

const INPUT: SeedInput = {
  title: ' Yeah the Boys ',
  artist: 'Hype Duo',
  durationMs: 196_273,
  releaseDate: '2019-03-12',
  link: 'https://www.youtube.com/watch?v=aBcDeFgHiJk',
  linkDetails: { channel: 'Hype Duo', uploadDate: '2019-03-12' },
  returnTo: 'http://wondarr.lan:1077/match?musicBrainzFor=440&seed=abc',
};

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('releaseDateParts', () => {
  it('reads a year, a month and a real day, and nothing at all', () => {
    expect(releaseDateParts('')).toEqual([]);
    expect(releaseDateParts(' 2019 ')).toEqual([2019]);
    expect(releaseDateParts('2019-03')).toEqual([2019, 3]);
    expect(releaseDateParts('2019-3-12')).toEqual([2019, 3, 12]);
    expect(releaseDateParts('2020-02-29')).toEqual([2020, 2, 29]);
  });

  it('refuses anything that is not a date the release editor can take', () => {
    expect(releaseDateParts('2019-02-29')).toBeNull();
    expect(releaseDateParts('2019-13')).toBeNull();
    expect(releaseDateParts('2019-00-10')).toBeNull();
    expect(releaseDateParts('12 March 2019')).toBeNull();
    expect(releaseDateParts('19-03-12')).toBeNull();
  });
});

describe('musicBrainzSeed', () => {
  it('fills the release editor with a worldwide digital single of one track, in MusicBrainz’ own field names', () => {
    expect(musicBrainzSeed(INPUT)).toEqual([
      ['name', 'Yeah the Boys'],
      ['type', 'Single'],
      ['status', 'official'],
      ['packaging', 'None'],
      ['artist_credit.names.0.name', 'Hype Duo'],
      ['artist_credit.names.0.artist.name', 'Hype Duo'],
      ['events.0.country', 'XW'],
      ['events.0.date.year', '2019'],
      ['events.0.date.month', '3'],
      ['events.0.date.day', '12'],
      ['mediums.0.format', 'Digital Media'],
      ['mediums.0.track.0.number', '1'],
      ['mediums.0.track.0.name', 'Yeah the Boys'],
      ['mediums.0.track.0.length', '196273'],
      ['urls.0.url', 'https://www.youtube.com/watch?v=aBcDeFgHiJk'],
      [
        'edit_note',
        'Uploaded by the "Hype Duo" YouTube channel on 2019-03-12: https://www.youtube.com/watch?v=aBcDeFgHiJk\n' +
          "Title, artist and length filled in by Wondarr from the file's tags.",
      ],
      ['redirect_uri', INPUT.returnTo],
    ]);
  });

  it('leaves out the date, the length and the link it does not have', () => {
    const fields = musicBrainzSeed({ ...INPUT, releaseDate: '', durationMs: null, link: '', linkDetails: null });
    const names = fields.map(([name]) => name);

    expect(names).not.toContain('events.0.date.year');
    expect(names).not.toContain('mediums.0.track.0.length');
    expect(names).not.toContain('urls.0.url');
    expect(names).toContain('events.0.country');
    expect(Object.fromEntries(fields).edit_note).toBe(
      "Title, artist and length filled in by Wondarr from the file's tags.",
    );
  });

  it('writes an edit note with no path of the user’s own in it', () => {
    expect(editNote({ ...INPUT, linkDetails: null })).toBe(
      'Released at: https://www.youtube.com/watch?v=aBcDeFgHiJk\n' +
        "Title, artist and length filled in by Wondarr from the file's tags.",
    );
    expect(editNote({ ...INPUT, linkDetails: { channel: 'Hype Duo', uploadDate: null } })).toContain(
      'Uploaded by the "Hype Duo" YouTube channel: https://',
    );
  });
});

describe('returnAddress', () => {
  it('sends the browser back to the Match page, under the URL base, naming the file and the token', () => {
    expect(returnAddress('', 440, 'abc', 'http://wondarr.lan:1077')).toBe(
      'http://wondarr.lan:1077/match?musicBrainzFor=440&seed=abc',
    );
    expect(returnAddress('/wondarr/', 440, 'abc', 'https://home.example')).toBe(
      'https://home.example/wondarr/match?musicBrainzFor=440&seed=abc',
    );
  });
});

describe('seedDefaults', () => {
  it('starts from the file’s own tags', () => {
    expect(seedDefaults(MATCH_QUEUE_ITEMS[0])).toEqual({ title: 'Harder Better Faster Stronger', artist: 'Daft Punk' });
  });

  it('reads an untagged file’s name: "Artist - Title", or a title under its artist’s folder', () => {
    const untagged = MATCH_QUEUE_ITEMS[1];

    expect(seedDefaults({ ...untagged, relativePath: 'Misc/Uni Revue - We’re Bogan.mp3' })).toEqual({
      title: 'We’re Bogan',
      artist: 'Uni Revue',
    });
    expect(seedDefaults({ ...untagged, relativePath: 'Hype Duo/_ Yeah the Boys.mp3' })).toEqual({
      title: 'Yeah the Boys',
      artist: 'Hype Duo',
    });
    expect(seedDefaults({ ...untagged, relativePath: 'Nena/03. 99 Luftballons.flac' })).toEqual({
      title: '99 Luftballons',
      artist: 'Nena',
    });
    // A track number is not an artist.
    expect(seedDefaults({ ...untagged, relativePath: 'Hype Duo/01 - Yeah the Boys.mp3' })).toEqual({
      title: 'Yeah the Boys',
      artist: 'Hype Duo',
    });
    expect(seedDefaults({ ...untagged, relativePath: 'Misc/02 - Uni Revue - We’re Bogan.mp3' })).toEqual({
      title: 'We’re Bogan',
      artist: 'Uni Revue',
    });
  });
});

describe('the seeds this browser opened', () => {
  it('are found by file and token, and forgotten once used', () => {
    rememberSeed(440, 'Hype Duo/_ Yeah the Boys.mp3', 'token-a', 1_000);

    expect(findSeed(440, 'token-a', 2_000)).toMatchObject({ fileId: 440, path: 'Hype Duo/_ Yeah the Boys.mp3' });
    expect(findSeed(440, 'token-b', 2_000)).toBeNull();
    expect(findSeed(441, 'token-a', 2_000)).toBeNull();

    forgetSeed(440, 'token-a', 2_000);

    expect(findSeed(440, 'token-a', 2_000)).toBeNull();
    expect(localStorage.getItem(SEED_STORAGE_KEY)).toBeNull();
  });

  it('are forgotten after a week', () => {
    rememberSeed(440, 'a.mp3', 'token-a', 0);

    expect(findSeed(440, 'token-a', 7 * 24 * 60 * 60 * 1000)).toBeNull();
  });

  it('survive storage that holds something else', () => {
    localStorage.setItem(SEED_STORAGE_KEY, '{"not":"a list"}');

    expect(findSeed(440, 'token-a')).toBeNull();

    rememberSeed(440, 'a.mp3', 'token-a');

    expect(findSeed(440, 'token-a')).not.toBeNull();
  });

  it('get a fresh token every time', () => {
    const token = newSeedToken();

    expect(token).toMatch(/^[0-9a-f]{32}$/);
    expect(newSeedToken()).not.toBe(token);
  });
});

describe('the way back that was not settled', () => {
  it('keeps the release on this browser’s seed until the file is settled', () => {
    rememberSeed(440, 'a.mp3', 'token-a', 1_000);

    const returned = readMusicBrainzReturn(
      new URLSearchParams(`musicBrainzFor=440&seed=token-a&release_mbid=${RELEASE_ID}`),
      2_000,
    );

    expect(unsettledReturn(2_000)).toBeNull();
    expect(returned).not.toBeNull();

    noteReturn(returned as MusicBrainzReturn, 2_000);

    expect(unsettledReturn(3_000)).toMatchObject({ fileId: 440, releaseId: RELEASE_ID, token: 'token-a' });

    forgetSeed(440, 'token-a', 3_000);

    expect(unsettledReturn(3_000)).toBeNull();
  });

  it('keeps nothing for a way back this browser did not start', () => {
    const forged = readMusicBrainzReturn(
      new URLSearchParams(`musicBrainzFor=440&seed=forged&release_mbid=${RELEASE_ID}`),
    );

    noteReturn(forged as MusicBrainzReturn);

    expect(unsettledReturn()).toBeNull();
    expect(localStorage.getItem(SEED_STORAGE_KEY)).toBeNull();
  });
});

describe('readMusicBrainzReturn', () => {
  it('reads the file and the release, and finds the seed this browser opened', () => {
    rememberSeed(440, 'Hype Duo/_ Yeah the Boys.mp3', 'token-a', 1_000);

    const returned = readMusicBrainzReturn(
      new URLSearchParams(`musicBrainzFor=440&seed=token-a&release_mbid=${RELEASE_ID.toUpperCase()}`),
      2_000,
    );

    expect(returned).toMatchObject({ fileId: 440, releaseId: RELEASE_ID, token: 'token-a' });
    expect(returned?.pending?.path).toBe('Hype Duo/_ Yeah the Boys.mp3');
  });

  it('has no seed for a token this browser did not open', () => {
    const returned = readMusicBrainzReturn(
      new URLSearchParams(`musicBrainzFor=440&seed=forged&release_mbid=${RELEASE_ID}`),
    );

    expect(returned).toMatchObject({ fileId: 440, pending: null });
  });

  it('is nothing without a file and a release MBID', () => {
    expect(readMusicBrainzReturn(new URLSearchParams(''))).toBeNull();
    expect(readMusicBrainzReturn(new URLSearchParams('musicBrainzFor=440&release_mbid=nope'))).toBeNull();
    expect(readMusicBrainzReturn(new URLSearchParams(`musicBrainzFor=x&release_mbid=${RELEASE_ID}`))).toBeNull();
  });
});

describe('openSeededEditor', () => {
  it('posts the fields to the release editor in a new tab, without a referrer, and leaves nothing behind', () => {
    const posted: Record<string, unknown>[] = [];

    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(function (this: HTMLFormElement) {
      posted.push({
        method: this.method,
        action: this.action,
        target: this.target,
        rel: this.getAttribute('rel'),
        noteInTextarea: this.querySelector('textarea[name="edit_note"]') !== null,
        fields: Array.from(this.elements, (element) => {
          const field = element as HTMLInputElement | HTMLTextAreaElement;

          return [field.name, field.value];
        }),
      });
    });

    const seed = musicBrainzSeed(INPUT);

    openSeededEditor(seed);

    expect(posted).toEqual([
      {
        method: 'post',
        action: MUSICBRAINZ_ADD_RELEASE,
        target: '_blank',
        rel: 'noopener noreferrer',
        noteInTextarea: true,
        fields: seed,
      },
    ]);
    expect(document.querySelector('form')).toBeNull();
  });
});
