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
    expect(screen.getByText('imported')).toBeInTheDocument();
    expect(screen.getByText('FLAC')).toBeInTheDocument();
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
