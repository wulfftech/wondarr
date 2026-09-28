import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, IMPORT_LIST_ITEMS, paged, SYSTEM_STATUS } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/add/unresolved?importListId=5');
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

  if (found === undefined) {
    return null;
  }

  // The skip carries no body at all, so there is nothing to parse.
  const text = await found.body;

  return { url: found.url, body: text === '' ? null : JSON.parse(text) };
}

function install(write: () => Response = () => jsonResponse(IMPORT_LIST_ITEMS[0])): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    // The item endpoint takes the writes: the resolve and the skip.
    if (/\/api\/v1\/importlistitem\/\d+\//.test(url)) {
      return write();
    }

    if (url.includes('/api/v1/importlistitem')) {
      return jsonResponse(paged(IMPORT_LIST_ITEMS));
    }

    return new Response('not found', { status: 404 });
  });
}

describe('UnresolvedPage', () => {
  it('lists the lines with the candidates and their reason', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Aphex Twin - Xtal')).toBeInTheDocument();
    expect(screen.getByText('No provider matched closely enough.')).toBeInTheDocument();
    expect(screen.getByText('score 72')).toBeInTheDocument();
    expect(screen.getByText('4:53')).toBeInTheDocument();
  });

  it('resolves the line with the candidate the user picked', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Aphex Twin - Xtal');
    await user.click(screen.getAllByRole('button', { name: 'Use this' })[0]);

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/importlistitem/5/resolve');
      expect(post?.body).toEqual({ mbRecordingId: 'a1b2c3d4-0000-0000-0000-000000000001' });
    });

    // The line is gone from the list the moment it is resolved.
    await waitFor(() => expect(screen.queryByText('Aphex Twin - Xtal')).toBeNull());
  });

  it('skips the line', async () => {
    install(() => jsonResponse(IMPORT_LIST_ITEMS[1]));
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Nobody - Nothing At All');
    await user.click(screen.getAllByRole('button', { name: 'Skip' })[1]);

    await waitFor(async () => {
      const post = await lastBody('POST');

      expect(post?.url).toContain('/api/v1/importlistitem/6/skip');
    });

    await waitFor(() => expect(screen.queryByText('Nobody - Nothing At All')).toBeNull());
  });

  it('shows the empty state when nothing is unresolved', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      return jsonResponse(paged([]));
    });

    renderApp();

    expect(await screen.findByText('Nothing to review.')).toBeInTheDocument();
  });
});
