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
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

const AMBIGUOUS = MATCH_QUEUE_ITEMS[0];
const UNMATCHED = MATCH_QUEUE_ITEMS[1];

beforeEach(() => {
  resetLocation('/match');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
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

/** The route table the page needs: the shell, the queue, the resolve and the manual lookup. */
function install(
  items: unknown = paged(MATCH_QUEUE_ITEMS),
  bulk: () => Response = () =>
    jsonResponse({ resolved: 1, failed: 1, errors: ['Unknown artist/04 track04.mp3: nothing to accept'] }),
): FetchMock {
  return installFetch((url, init) => {
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
      return jsonResponse({ state: 'identified', songId: 12, message: null });
    }

    if (url.includes('/api/v1/matchqueue')) {
      return jsonResponse(items);
    }

    if (url.includes('/api/v1/song/lookup')) {
      return jsonResponse(LOOKUP_RESULTS);
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
    // The top candidate runs 3 s longer than the file and scored 87 %.
    // The recording id the reason was stored with is not shown.
    expect(screen.getByText(/3:47 \(\+3 s\) · 87% · search, length differs by 3 s/)).toBeInTheDocument();
    expect(screen.queryByText(/9d3f5a1e/)).not.toBeInTheDocument();
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

    expect(body).toEqual({ ids: [AMBIGUOUS.id, UNMATCHED.id] });
    expect(await screen.findByText('Resolved 1, failed 1')).toBeInTheDocument();
    expect(screen.getByText(/nothing to accept/)).toBeInTheDocument();
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

  it('shows the empty state when nothing is waiting', async () => {
    install(paged([]));

    renderApp();

    expect(await screen.findByText('Nothing to review — every file was identified or skipped.')).toBeInTheDocument();
  });
});
