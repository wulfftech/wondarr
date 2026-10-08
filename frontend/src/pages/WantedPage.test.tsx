import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, paged, QUALITY_PROFILES, SONGS, SYSTEM_STATUS } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/wanted/missing');
  // jsdom has no scrollIntoView, which Mantine's menu and combobox call as they open.
  Element.prototype.scrollIntoView = vi.fn();
});

/**
 * What the mocked `fetch` actually received. The generated client calls `fetch(request)`, so the
 * method and the body live on that `Request`, not on the second argument the shared helper records.
 */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

/** The route table the page needs: the shell, both wanted lists and the profile names. */
function install(missing: unknown = paged(SONGS), cutoff: unknown = paged(SONGS)): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/wanted/missing')) {
      return jsonResponse(missing);
    }

    if (url.includes('/api/v1/wanted/cutoff')) {
      return jsonResponse(cutoff);
    }

    if (url.includes('/api/v1/qualityprofile')) {
      return jsonResponse(QUALITY_PROFILES);
    }

    return new Response('not found', { status: 404 });
  });
}

describe('WantedPage', () => {
  it('lists the missing songs with their duration and quality profile', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Teardrop')).toBeInTheDocument();
    // Mantine's test environment renders without portals and transitions, so the same row text can
    // appear more than once in the document; what matters is that it is shown.
    for (const text of ['Massive Attack', 'Mezzanine', '6:09', 'Standard 320']) {
      // 369000 ms reads as 6:09, not 6.15 or 369.
      expect(screen.getAllByText(text).length).toBeGreaterThan(0);
    }
  });

  it('shows the cover column until the toggle turns it off, and remembers that', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Teardrop');
    expect(screen.getByRole('columnheader', { name: 'Cover' })).toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Show cover art' }));

    expect(screen.queryByRole('columnheader', { name: 'Cover' })).not.toBeInTheDocument();
    expect(localStorage.getItem('wondarr.wanted.showCovers')).toBe('0');
  });

  it('starts without the cover column when it was turned off before', async () => {
    localStorage.setItem('wondarr.wanted.showCovers', '0');
    install();

    renderApp();

    await screen.findByText('Teardrop');
    expect(screen.getByRole('switch', { name: 'Show cover art' })).not.toBeChecked();
    expect(screen.queryByRole('columnheader', { name: 'Cover' })).not.toBeInTheDocument();
  });

  it('asks for the cutoff list when the Cutoff Unmet tab is picked', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Teardrop');
    await user.click(screen.getByRole('tab', { name: 'Cutoff Unmet' }));

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('/api/v1/wanted/cutoff'))).toBe(true);
    });

    expect(window.location.pathname).toBe('/wanted/cutoff');
  });

  it('asks for the title order when the Title header is clicked', async () => {
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Teardrop');
    await user.click(screen.getByRole('button', { name: /Title/ }));

    await waitFor(() => {
      expect(mock.calls.some((call) => call.url.includes('sortKey=title'))).toBe(true);
    });
  });

  it('shows the empty state when nothing is missing', async () => {
    install(paged([]));

    renderApp();

    expect(await screen.findByText('Nothing is missing.')).toBeInTheDocument();
  });

  it('starts the automatic search when the search button is used', async () => {
    const mock = installFetch((url, init) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/command')) {
        return jsonResponse({}, init?.method === 'POST' ? 201 : 200);
      }

      if (url.includes('/api/v1/wanted/missing')) {
        return jsonResponse(paged(SONGS));
      }

      if (url.includes('/api/v1/qualityprofile')) {
        return jsonResponse(QUALITY_PROFILES);
      }

      return new Response('not found', { status: 404 });
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Search for Teardrop'));

    await waitFor(() => {
      expect(sent().some((request) => request.method === 'POST' && request.url.endsWith('/api/v1/command'))).toBe(true);
    });

    const post = sent().find((request) => request.method === 'POST');
    const body = JSON.parse(await (post?.body ?? Promise.resolve('{}'))) as { name: string; songId: number };

    expect(body).toEqual({ name: 'SongSearch', songId: 1 });
    expect(await screen.findByText('Searching for Teardrop')).toBeInTheDocument();
    expect(mock.calls.some((call) => call.url.includes('/api/v1/wanted/missing'))).toBe(true);
  });

  it('opens the interactive search when its button is used', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/release')) {
        return jsonResponse({
          searchRunId: 4,
          outcome: 'noAcceptableCandidate',
          message: 'Every candidate failed a rule.',
          releases: [
            {
              candidateId: 9001,
              sourceType: 'soulseek',
              provider: 'peer-one',
              displayName: 'Massive Attack - Teardrop.flac',
              remotePath: 'Music\\Massive Attack\\Teardrop.flac',
              extension: 'flac',
              qualityId: 20,
              qualityName: 'FLAC',
              sizeBytes: 31457280,
              durationMs: 369000,
              bitrateKbps: null,
              sampleRate: null,
              bitDepth: null,
              freeUploadSlot: true,
              queueLength: 0,
              uploadSpeed: 1048576,
              score: 420,
              scoreBreakdown: {
                title: 180,
                artist: 100,
                duration: 12,
                identity: 120,
                quality: 120,
                availability: 68,
                sourcePreference: 100,
                adjustments: [],
                adjustmentTotal: 0,
                total: 420,
                cappedForUnknownDuration: false,
              },
              rejections: [{ reason: 'durationOutOfTolerance', message: 'The candidate is 41 s longer.' }],
              accepted: false,
              parsed: { artist: null, title: null, album: null, trackNo: null, flags: [], pathFlags: [] },
              query: 'Massive Attack Teardrop',
            },
          ],
        });
      }

      if (url.includes('/api/v1/wanted/missing')) {
        return jsonResponse(paged(SONGS));
      }

      return new Response('not found', { status: 404 });
    });
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Interactive search for Teardrop'));

    expect(await screen.findByText('Massive Attack - Teardrop.flac')).toBeInTheDocument();
    expect(screen.getByText('1 candidate, none acceptable: Every candidate failed a rule.')).toBeInTheDocument();
  });

  it('shows an error state when the list fails', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      return jsonResponse({ title: 'Server error' }, 500);
    });

    renderApp();

    expect(await screen.findByText('The wanted list request failed.')).toBeInTheDocument();
  });
});
