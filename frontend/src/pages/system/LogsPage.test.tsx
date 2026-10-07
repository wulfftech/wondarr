import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS, paged } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/system/logs');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const ENTRIES = [
  {
    id: 1,
    time: '2026-10-07T06:00:02Z',
    level: 'Warning',
    logger: 'Wondarr.Sources.Slskd.SlskdHost',
    message: 'slskd (generation 2) exited unexpectedly with code 134',
    exception: null,
  },
  {
    id: 2,
    time: '2026-10-07T06:00:01Z',
    level: 'Information',
    logger: 'slskd',
    message: 'Logged in to the Soulseek server as gate-user',
    exception: null,
  },
];

const FILES = [
  {
    id: 1,
    filename: 'wondarr-20261007.json',
    lastWriteTime: '2026-10-07T06:00:02Z',
    contentsUrl: '/api/v1/log/file/wondarr-20261007.json',
  },
];

function logRequests(): URL[] {
  return vi
    .mocked(globalThis.fetch)
    .mock.calls.map(([input, init]) => new URL((input instanceof Request ? input : new Request(input, init)).url))
    .filter((url) => url.pathname.endsWith('/api/v1/log'));
}

function install(truncated = false): void {
  installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/log/file')) {
      return jsonResponse(FILES);
    }

    if (url.includes('/api/v1/log')) {
      const response = jsonResponse(paged(ENTRIES));

      if (truncated) {
        response.headers.set('X-Wondarr-Log-Truncated', 'true');
      }

      return response;
    }

    return new Response('not found', { status: 404 });
  });
}

describe('LogsPage', () => {
  it('shows the log entries, newest first, with their level and logger', async () => {
    install();

    renderApp();

    expect(await screen.findByText('slskd (generation 2) exited unexpectedly with code 134')).toBeInTheDocument();
    expect(screen.getByText('Logged in to the Soulseek server as gate-user')).toBeInTheDocument();
    expect(screen.getByText('Wondarr.Sources.Slskd.SlskdHost')).toBeInTheDocument();
  });

  it('asks for Information and above by default, and for the chosen level after a change', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Logged in to the Soulseek server as gate-user');
    expect(logRequests()[0].searchParams.get('level')).toBe('Information');

    await user.click(screen.getByTestId('log-level'));
    await user.click(await screen.findByRole('option', { name: 'Warning' }));

    await waitFor(() => expect(logRequests().some((url) => url.searchParams.get('level') === 'Warning')).toBe(true));
  });

  it('lists the log files as downloads', async () => {
    install();

    renderApp();

    const link = await screen.findByRole('link', { name: 'wondarr-20261007.json' });
    expect(link).toHaveAttribute('href', expect.stringContaining('/api/v1/log/file/wondarr-20261007.json'));
  });

  it('says when only the newest part of the log was searched', async () => {
    install(true);

    renderApp();

    expect(await screen.findByTestId('log-truncated')).toBeInTheDocument();
  });
});
