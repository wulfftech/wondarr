import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  BLOCKLIST_ITEMS,
  HEALTH_ENTRIES,
  HISTORY_ITEMS,
  paged,
  QUALITY_DEFINITIONS,
  SYSTEM_STATUS,
} from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/activity/history');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function install(deleteStatus = 200): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/qualitydefinition')) {
      return jsonResponse(QUALITY_DEFINITIONS);
    }

    if (url.includes('/api/v1/queue')) {
      return jsonResponse(paged([]));
    }

    if (url.includes('/api/v1/history')) {
      return jsonResponse(paged(HISTORY_ITEMS));
    }

    if (url.includes('/api/v1/blocklist') && init?.method === 'DELETE') {
      return new Response(null, { status: deleteStatus });
    }

    if (url.includes('/api/v1/blocklist')) {
      return jsonResponse(paged(BLOCKLIST_ITEMS));
    }

    return new Response('not found', { status: 404 });
  });
}

describe('ActivityPage', () => {
  it('opens on the queue, before history and the blocklist', async () => {
    resetLocation('/activity/queue');
    install();

    renderApp();

    // The queue is the first tab and the one the route without a tab name lands on.
    expect(await screen.findByRole('tab', { name: 'Queue', selected: true })).toBeInTheDocument();
    expect(await screen.findByText('Nothing is downloading.')).toBeInTheDocument();
  });

  it('lists the history with its event badge and quality', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Teardrop')).toBeInTheDocument();
    // The title opens the song's own page.
    expect(screen.getByRole('link', { name: 'Teardrop' })).toHaveAttribute('href', '/song/1');
    expect(screen.getByText('Imported')).toBeInTheDocument();
    expect(screen.getByText('FLAC')).toBeInTheDocument();
  });

  it('shows the release and the songs that rode along on a container grab', async () => {
    resetLocation('/activity/history');
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/qualitydefinition')) {
        return jsonResponse(QUALITY_DEFINITIONS);
      }

      if (url.includes('/api/v1/history')) {
        return jsonResponse(
          paged([
            {
              ...HISTORY_ITEMS[0],
              eventType: 'grabbed',
              data: { release: 'Queen - A Night at the Opera (1975) [FLAC]', bundledWith: 2 },
            },
            { ...HISTORY_ITEMS[0], id: 2, eventType: 'grabbed', data: { release: null, bundledWith: 0 } },
          ]),
        );
      }

      return new Response('not found', { status: 404 });
    });

    renderApp();

    expect(
      await screen.findByText('Queen - A Night at the Opera (1975) [FLAC] · with 2 other songs'),
    ).toBeInTheDocument();
    // The second grab has no release, so it adds no line.
    expect(screen.getAllByText(/Queen - A Night/)).toHaveLength(1);
  });

  describe('a grabbed event from a release', () => {
    /** Serves the Activity shell and one history page holding `events`. */
    function installHistory(events: unknown[]): void {
      installFetch((url) => {
        if (url.includes('/api/v1/system/status')) {
          return jsonResponse(SYSTEM_STATUS);
        }

        if (url.includes('/api/v1/health')) {
          return jsonResponse(HEALTH_ENTRIES);
        }

        if (url.includes('/api/v1/qualitydefinition')) {
          return jsonResponse(QUALITY_DEFINITIONS);
        }

        if (url.includes('/api/v1/history')) {
          return jsonResponse(paged(events));
        }

        return new Response('not found', { status: 404 });
      });
    }

    const RELEASE = 'Queen - A Night at the Opera (1975) [FLAC]';

    it('names the release and the file inside it, and leaves out "other songs" when none rode along', async () => {
      installHistory([
        {
          ...HISTORY_ITEMS[0],
          eventType: 'grabbed',
          data: { release: RELEASE, displayName: '05 - Bohemian Rhapsody.flac', bundledWith: 0 },
        },
      ]);

      renderApp();

      expect(await screen.findByText(`${RELEASE} · 05 - Bohemian Rhapsody.flac`)).toBeInTheDocument();
      expect(screen.queryByText(/other song/)).not.toBeInTheDocument();
      expect(screen.queryByText('With:')).not.toBeInTheDocument();
    });

    it('lists the other songs by name, linked to their pages, and caps the list at ten', async () => {
      const bundledSongs = Array.from({ length: 12 }, (_, index) => ({
        songId: 100 + index,
        title: `Bundled song ${index + 1}`,
      }));
      installHistory([
        {
          ...HISTORY_ITEMS[0],
          eventType: 'grabbed',
          data: { release: RELEASE, displayName: '05 - Bohemian Rhapsody.flac', bundledWith: 12, bundledSongs },
        },
      ]);

      renderApp();

      expect(
        await screen.findByText(`${RELEASE} · 05 - Bohemian Rhapsody.flac · with 12 other songs`),
      ).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Bundled song 1' })).toHaveAttribute('href', '/song/100');
      expect(screen.getByRole('link', { name: 'Bundled song 10' })).toHaveAttribute('href', '/song/109');
      // Songs 11 and 12 are folded into "and 2 more".
      expect(screen.queryByRole('link', { name: 'Bundled song 11' })).not.toBeInTheDocument();
      expect(screen.getByText('and 2 more')).toBeInTheDocument();
    });

    it('shows exactly ten names without "and … more", and a song with no page as plain text', async () => {
      const bundledSongs = [
        ...Array.from({ length: 9 }, (_, index) => ({ songId: 200 + index, title: `Kept ${index + 1}` })),
        { songId: null, title: 'Gone' },
      ];
      installHistory([
        {
          ...HISTORY_ITEMS[0],
          eventType: 'grabbed',
          data: { release: RELEASE, displayName: 'a.flac', bundledWith: 10, bundledSongs },
        },
      ]);

      renderApp();

      expect(await screen.findByText('Gone')).toBeInTheDocument();
      expect(screen.queryByRole('link', { name: 'Gone' })).not.toBeInTheDocument();
      expect(screen.queryByText(/and \d+ more/)).not.toBeInTheDocument();
    });
  });

  it('removes a blocklist entry after the confirmation modal', async () => {
    resetLocation('/activity/blocklist');
    const mock = install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Remove a1b2c3d4'));

    // The confirmation is a Mantine modal: nothing is sent until its own Remove is pressed.
    expect(mock.calls.some((call) => call.init?.method === 'DELETE')).toBe(false);

    // The modal renders in a portal, so its button is looked up inside the dialog once it exists.
    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(
        mock.calls.some(
          (candidate) => candidate.init?.method === 'DELETE' && candidate.url.endsWith('/api/v1/blocklist/7'),
        ),
      ).toBe(true);
    });
  });

  it('shows the failure when the blocklist entry cannot be removed', async () => {
    resetLocation('/activity/blocklist');
    install(500);
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByLabelText('Remove a1b2c3d4'));

    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('button', { name: 'Remove' }));

    expect(await screen.findByText(/could not be removed/i)).toBeInTheDocument();
  });
});
