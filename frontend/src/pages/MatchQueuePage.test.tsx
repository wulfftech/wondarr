import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  HEALTH_ENTRIES,
  LOOKUP_RESULTS,
  MATCH_QUEUE_ITEMS,
  REFERENCE_LIBRARIES,
  SYSTEM_STATUS,
  paged,
} from '../test/fixtures';
import { findSeed, rememberSeed, SEED_STORAGE_KEY } from '../components/musicBrainzSeed';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

const AMBIGUOUS = MATCH_QUEUE_ITEMS[0];
const UNMATCHED = MATCH_QUEUE_ITEMS[1];

/** The release MusicBrainz sends the browser back with. */
const RELEASE_ID = '4587fe99-db0e-4553-a56a-164dd38ab380';

/** What `GET /api/v1/youtube/video` answers for the link the tests paste. */
const VIDEO = {
  videoId: 'aBcDeFgHiJk',
  url: 'https://www.youtube.com/watch?v=aBcDeFgHiJk',
  title: 'Yeah the Boys (Official Video)',
  channel: 'Hype Duo',
  uploadDate: '2019-03-12',
  durationMs: 196320,
};

beforeEach(() => {
  resetLocation('/match');
  localStorage.clear();
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** The route table the page needs: the shell, the queue, the resolve and the manual lookup. */
function install(
  items: unknown = paged(MATCH_QUEUE_ITEMS),
  bulk: () => Response = () =>
    jsonResponse({ resolved: 1, failed: 1, errors: ['Unknown artist/04 track04.mp3: nothing to accept'] }),
  lookup: () => Response = () => jsonResponse(LOOKUP_RESULTS),
  resolve: () => Response = () => jsonResponse({ state: 'identified', songId: 12, message: null, replacement: null }),
  video: () => Response = () => jsonResponse(VIDEO),
): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/youtube/video')) {
      return video();
    }

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/matchqueue/bulk')) {
      return bulk();
    }

    if (url.includes('/resolve')) {
      return resolve();
    }

    if (url.includes('/api/v1/matchqueue')) {
      return jsonResponse(items);
    }

    if (url.includes('/api/v1/song/lookup')) {
      return lookup();
    }

    if (url.includes('/api/v1/referencelibrary')) {
      return jsonResponse(REFERENCE_LIBRARIES);
    }

    if (init?.method === 'POST') {
      return jsonResponse({});
    }

    return new Response('not found', { status: 404 });
  });
}

/** The body of the last request the page sent to `path`, once it has sent one. */
async function lastBody(path: string): Promise<unknown> {
  let body: unknown = null;

  await waitFor(async () => {
    const requests = sent().filter((request) => request.method === 'POST' && request.url.includes(path));

    expect(requests.length).toBeGreaterThan(0);

    const last = requests[requests.length - 1];

    body = last === undefined ? null : JSON.parse(await last.body);
  });

  return body;
}

/** Opens a Mantine Select by the label on its input and hands back the listbox it is showing. */
async function pickOption(user: UserEvent, label: string, option: string): Promise<void> {
  const input = screen.getByLabelText(label, { selector: 'input' });

  await user.click(input);

  const listbox = document.getElementById(input.getAttribute('aria-controls') ?? '');

  if (listbox === null) {
    throw new Error(`the ${label} dropdown did not open`);
  }

  await user.click(within(listbox).getByText(option));
}

describe('MatchQueuePage', () => {
  it('shows what each file says about itself and the best candidate with its length difference', async () => {
    install();

    renderApp();

    expect(await screen.findByText(AMBIGUOUS.relativePath)).toBeInTheDocument();
    // The tags are on the file's own line; the format, the probed length and the bitrate have columns.
    expect(screen.getByText('Daft Punk – Harder Better Faster Stronger · Discovery')).toBeInTheDocument();
    const row = screen.getByText(AMBIGUOUS.relativePath).closest('tr');
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText('FLAC')).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText('3:44')).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText('900 kbps')).toBeInTheDocument();
    expect(screen.getByText('Needs review')).toBeInTheDocument();
    expect(screen.getByText('No match')).toBeInTheDocument();
    // The top candidate is written Artist - Title, ran 3:47 and scored 87 %; the 3 s has its own column.
    expect(within(row as HTMLElement).getByText('Daft Punk - Harder, Better, Faster, Stronger')).toBeInTheDocument();
    // The recording id the reason was stored with is not shown.
    expect(screen.getByText(/3:47 · 87% · search, length differs by 3 s/)).toBeInTheDocument();
    expect(screen.queryByText(/9d3f5a1e/)).not.toBeInTheDocument();
  });

  it('puts the top candidate’s length difference in its own column, signed', async () => {
    const longer = MATCH_QUEUE_ITEMS[0];
    const shorter = {
      ...longer,
      id: 73,
      relativePath: 'Daft Punk/Discovery/05 - Shorter.flac',
      candidates: [{ ...longer.candidates[0], durationMs: 212000 }],
    };
    const same = {
      ...longer,
      id: 74,
      relativePath: 'Daft Punk/Discovery/06 - Same.flac',
      candidates: [{ ...longer.candidates[0], durationMs: 224000 }],
    };
    const unknown = {
      ...longer,
      id: 75,
      relativePath: 'Daft Punk/Discovery/07 - Unknown.flac',
      file: { ...longer.file, durationMs: null },
    };
    install(paged([longer, shorter, same, unknown]));

    renderApp();

    expect(await screen.findByRole('columnheader', { name: 'Δ Length' })).toBeInTheDocument();

    const cellFor = (path: string) => {
      const row = screen.getByText(path).closest('tr') as HTMLElement;
      const cells = within(row).getAllByRole('cell');
      // Δ Length sits right after Top candidate, before Actions.
      return cells[cells.length - 2];
    };

    expect(cellFor(longer.relativePath)).toHaveTextContent('+3 s');
    expect(cellFor(shorter.relativePath)).toHaveTextContent('−12 s');
    expect(cellFor(same.relativePath)).toHaveTextContent('0 s');
    expect(cellFor(unknown.relativePath)).toHaveTextContent('—');
  });

  it('writes the review drawer’s candidates as Artist - Title', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));

    expect(await screen.findByText('Daft Punk - Harder, Better, Faster, Stronger (Live)')).toBeInTheDocument();
  });

  it('accepts the top candidate by its rank', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Accept the top candidate for ${AMBIGUOUS.relativePath}`));

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ candidateRank: 1 });
  });

  it('accepts a lower-ranked candidate from the review drawer', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));
    await user.click(
      await screen.findByLabelText(`Accept ${AMBIGUOUS.candidates[1].title} for ${AMBIGUOUS.relativePath}`),
    );

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ candidateRank: 2 });
  });

  it('searches with the file’s own artist and title, and accepts a MusicBrainz result by its MBID', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));

    const term = screen.getByLabelText('Artist – title');

    expect(term).toHaveValue('Daft Punk – Harder Better Faster Stronger');

    await user.click(screen.getByRole('button', { name: 'Search' }));
    expect(await screen.findByText('Daft Punk feat. Pharrell Williams - Get Lucky')).toBeInTheDocument();
    await user.click(await screen.findByLabelText('Accept the musicbrainz result Get Lucky'));

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ mbRecordingId: LOOKUP_RESULTS[0].mbRecordingId });
  });

  it('accepts a Deezer-only result by its Deezer id', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await user.click(await screen.findByLabelText('Accept the deezer result Get Lucky (Radio Edit)'));

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ deezerId: LOOKUP_RESULTS[1].deezerId });
  });

  it('says so when the manual search only had one provider’s results', async () => {
    install(
      paged(MATCH_QUEUE_ITEMS),
      undefined,
      () =>
        new Response(JSON.stringify(LOOKUP_RESULTS), {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'X-Wondarr-Partial': 'musicbrainz' },
        }),
    );
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(
      await screen.findByText(
        "MusicBrainz didn't answer, so these are Deezer's results only. Try again for the full list.",
      ),
    ).toBeInTheDocument();
  });

  it('skips a file the user does not want to settle', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${UNMATCHED.relativePath}`));
    await user.click(await screen.findByRole('button', { name: 'Skip' }));

    const body = await lastBody(`/api/v1/matchqueue/${UNMATCHED.id}/resolve`);

    expect(body).toMatchObject({ skip: true });
  });

  it('accepts the top candidate for every selected row and reports what happened', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Select all on this page' }));
    await user.click(screen.getByRole('button', { name: 'Accept top candidate for selected' }));

    const body = await lastBody('/api/v1/matchqueue/bulk');

    expect(body).toEqual({ ids: [AMBIGUOUS.id, UNMATCHED.id], replace: false });
    expect(await screen.findByText('Resolved 1, failed 1')).toBeInTheDocument();
    expect(screen.getByText(/nothing to accept/)).toBeInTheDocument();
  });

  it('flags a likely YouTube rip and narrows the queue to them', async () => {
    const rip = { ...AMBIGUOUS, likelyYouTubeRip: true, file: { ...AMBIGUOUS.file, codec: 'mp3', sampleRate: 48000 } };
    const mock = install(paged([rip, UNMATCHED]));
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Likely YouTube rip')).toBeInTheDocument();
    expect(screen.getByText('48 kHz')).toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Only likely YouTube rips' }));

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('likelyYouTubeRip=true'))).toBe(true);
    });
  });

  it('replaces a file with a better copy of its top candidate and says what happened', async () => {
    install(undefined, undefined, undefined, () =>
      jsonResponse({ state: 'identified', songId: 12, message: null, replacement: 'Searching for a better copy now.' }),
    );
    const user = userEvent.setup();

    renderApp();

    await user.click(
      await screen.findByLabelText(`Replace ${AMBIGUOUS.relativePath} with a better copy of the top candidate`),
    );

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ candidateRank: 1, replace: true });
    expect(await screen.findByText('Searching for a better copy now.')).toBeInTheDocument();
  });

  it('replaces a lower-ranked candidate from the review drawer', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${AMBIGUOUS.relativePath}`));
    await user.click(
      await screen.findByLabelText(
        `Replace ${AMBIGUOUS.relativePath} with a better copy of Harder, Better, Faster, Stronger (Live)`,
      ),
    );

    const body = await lastBody(`/api/v1/matchqueue/${AMBIGUOUS.id}/resolve`);

    expect(body).toMatchObject({ candidateRank: 2, replace: true });
  });

  it('replaces every selected file and says how many songs are being searched', async () => {
    install(undefined, () => jsonResponse({ resolved: 2, failed: 0, errors: [], searching: 1 }));
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Select all on this page' }));
    await user.click(screen.getByRole('button', { name: 'Replace selected' }));

    const body = await lastBody('/api/v1/matchqueue/bulk');

    expect(body).toEqual({ ids: [AMBIGUOUS.id, UNMATCHED.id], replace: true });
    expect(await screen.findByText(/Searching for better copies of 1 song now/)).toBeInTheDocument();
  });

  it('says so when Replace for the selection has nothing to search for', async () => {
    install(undefined, () => jsonResponse({ resolved: 2, failed: 0, errors: [], searching: 0 }));
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Select all on this page' }));
    await user.click(screen.getByRole('button', { name: 'Replace selected' }));

    expect(await screen.findByText(/No song is below its cutoff, so nothing is searched for/)).toBeInTheDocument();
  });

  it('filters the queue to one reference library', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText(AMBIGUOUS.relativePath);
    await pickOption(user, 'Reference library', 'Old music');

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('referenceLibraryId=3'))).toBe(true);
    });
  });

  it('opens MusicBrainz’ release editor filled in from the file and the video, and remembers the seed', async () => {
    install();
    const user = userEvent.setup();
    const posted: Record<string, string>[] = [];

    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(function (this: HTMLFormElement) {
      posted.push(
        Object.fromEntries(
          Array.from(new FormData(this).entries(), ([name, value]) => [name, typeof value === 'string' ? value : '']),
        ),
      );
    });

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${UNMATCHED.relativePath}`));
    await user.click(screen.getByRole('button', { name: 'Add to MusicBrainz…' }));

    // An untagged file starts from its name, under its artist's folder.
    expect(screen.getByLabelText(/^Artist\s*\*?$/)).toHaveValue('Unknown artist');

    await user.clear(screen.getByLabelText(/^Title\s*\*?$/));
    await user.type(screen.getByLabelText(/^Title\s*\*?$/), 'Yeah the Boys');
    await user.clear(screen.getByLabelText(/^Artist\s*\*?$/));
    await user.type(screen.getByLabelText(/^Artist\s*\*?$/), 'Hype Duo');
    await user.type(screen.getByLabelText(/^YouTube link/), 'https://youtu.be/aBcDeFgHiJk');
    await user.click(screen.getByRole('button', { name: 'Read video' }));

    expect(await screen.findByText('Hype Duo · uploaded 2019-03-12 · 3:16')).toBeInTheDocument();
    expect(screen.getByLabelText(/^Release date/)).toHaveValue('2019-03-12');
    expect(screen.getByLabelText(/^YouTube link/)).toHaveValue(VIDEO.url);

    await user.click(screen.getByRole('button', { name: 'Open MusicBrainz' }));

    expect(posted).toHaveLength(1);

    const form = posted[0] ?? {};
    const returnTo = new URL(form.redirect_uri ?? '');

    expect(form.name).toBe('Yeah the Boys');
    expect(form['artist_credit.names.0.name']).toBe('Hype Duo');
    expect(form['mediums.0.track.0.length']).toBe('201000');
    expect(form['events.0.date.year']).toBe('2019');
    expect(form['urls.0.url']).toBe(VIDEO.url);
    expect(form.edit_note).toContain('Uploaded by the "Hype Duo" YouTube channel on 2019-03-12');
    expect(returnTo.pathname).toBe('/match');
    expect(returnTo.searchParams.get('musicBrainzFor')).toBe(String(UNMATCHED.id));
    expect(findSeed(Number(UNMATCHED.id), returnTo.searchParams.get('seed') ?? '')).toMatchObject({
      path: UNMATCHED.relativePath,
    });
    expect(screen.getByText(/MusicBrainz opened in a new tab/)).toBeInTheDocument();
  });

  it('says why a video could not be read, and still sends the link as typed', async () => {
    install(undefined, undefined, undefined, undefined, () =>
      jsonResponse(
        {
          title: 'The YouTube source is off',
          detail: 'Wondarr reads YouTube only while the YouTube source is on (Settings → YouTube).',
          status: 409,
        },
        409,
      ),
    );
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${UNMATCHED.relativePath}`));
    await user.click(screen.getByRole('button', { name: 'Add to MusicBrainz…' }));
    await user.type(screen.getByLabelText(/^YouTube link/), 'https://youtu.be/aBcDeFgHiJk');
    await user.click(screen.getByRole('button', { name: 'Read video' }));

    expect(await screen.findByText(/Wondarr reads YouTube only while the YouTube source is on/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^YouTube link/)).toHaveValue('https://youtu.be/aBcDeFgHiJk');
  });

  it('will not open MusicBrainz with a release date it cannot take', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText(`Review ${UNMATCHED.relativePath}`));
    await user.click(screen.getByRole('button', { name: 'Add to MusicBrainz…' }));
    await user.type(screen.getByLabelText(/^Release date/), '12 March');

    expect(screen.getByText('Use YYYY, YYYY-MM or YYYY-MM-DD, or leave it empty.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open MusicBrainz' })).toBeDisabled();
  });

  it('settles the file with the release MusicBrainz sends this browser back with', async () => {
    rememberSeed(Number(UNMATCHED.id), UNMATCHED.relativePath, 'token-a');
    resetLocation(`/match?musicBrainzFor=${UNMATCHED.id}&seed=token-a&release_mbid=${RELEASE_ID}`);
    install();

    renderApp();

    const body = await lastBody(`/api/v1/matchqueue/${UNMATCHED.id}/resolve`);

    expect(body).toMatchObject({ mbReleaseId: RELEASE_ID, mbRecordingId: null, candidateRank: null, skip: false });
    expect(await screen.findByText('Settled')).toBeInTheDocument();
    // Once is enough: the address no longer says to settle it, and the seed is used up.
    expect(window.location.search).toBe('');
    expect(findSeed(Number(UNMATCHED.id), 'token-a')).toBeNull();
    expect(sent().filter((request) => request.url.includes('/resolve'))).toHaveLength(1);
  });

  it('asks before settling a way back this browser did not start', async () => {
    resetLocation(`/match?musicBrainzFor=${UNMATCHED.id}&seed=forged&release_mbid=${RELEASE_ID}`);
    install();
    const user = userEvent.setup();

    renderApp();

    expect(await screen.findByText('Back from MusicBrainz')).toBeInTheDocument();
    // The file is named by its path once the queue shows it, though this browser never saw the seed.
    expect(await screen.findByText(`Settle ${UNMATCHED.relativePath} with it?`, { exact: false })).toBeInTheDocument();
    expect(sent().some((request) => request.url.includes('/resolve'))).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Settle' }));

    expect(await lastBody(`/api/v1/matchqueue/${UNMATCHED.id}/resolve`)).toMatchObject({ mbReleaseId: RELEASE_ID });
  });

  it('says why the release could not settle the file yet, and tries again', async () => {
    rememberSeed(Number(UNMATCHED.id), UNMATCHED.relativePath, 'token-a');
    resetLocation(`/match?musicBrainzFor=${UNMATCHED.id}&seed=token-a&release_mbid=${RELEASE_ID}`);
    install(undefined, undefined, undefined, () =>
      jsonResponse(
        {
          title: 'Not on MusicBrainz yet',
          detail: 'MusicBrainz does not show that release yet. Try again in a minute.',
          status: 404,
        },
        404,
      ),
    );
    const user = userEvent.setup();

    renderApp();

    expect(
      await screen.findByText('MusicBrainz does not show that release yet. Try again in a minute.'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    await waitFor(() => expect(sent().filter((request) => request.url.includes('/resolve'))).toHaveLength(2));
    // Not settled, so not forgotten: the seed waits for the next try.
    expect(findSeed(Number(UNMATCHED.id), 'token-a')).not.toBeNull();
  });

  it('refreshes the queue when another tab settles a file', async () => {
    const mock = install();

    renderApp();

    expect(await screen.findByText(AMBIGUOUS.relativePath)).toBeInTheDocument();

    const before = mock.calls.filter((call) => call.url.includes('/api/v1/matchqueue?')).length;

    window.dispatchEvent(new StorageEvent('storage', { key: SEED_STORAGE_KEY }));

    await waitFor(() =>
      expect(mock.calls.filter((call) => call.url.includes('/api/v1/matchqueue?')).length).toBeGreaterThan(before),
    );
  });

  it('shows the empty state when nothing is waiting', async () => {
    install(paged([]));

    renderApp();

    expect(await screen.findByText('Nothing to review — every file was identified or skipped.')).toBeInTheDocument();
  });
});
