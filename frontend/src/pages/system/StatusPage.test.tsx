import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchHandler } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/system/status');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('StatusPage', () => {
  it('renders the app name and version from the status response', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      return jsonResponse(HEALTH_ENTRIES);
    });

    renderApp();

    const table = await screen.findByTestId('system-status');

    expect(within(table).getByText('Compilarr')).toBeInTheDocument();
    expect(within(table).getByText('0.1.0-test')).toBeInTheDocument();
    expect(within(table).getByText('netcore 10.0.0')).toBeInTheDocument();
    expect(within(table).getByText('Linux 6.1.0')).toBeInTheDocument();
    expect(within(table).getByText('Docker')).toBeInTheDocument();
  });

  it('shows the health list with a badge per entry', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      return jsonResponse(HEALTH_ENTRIES);
    });

    renderApp();

    const list = await screen.findByTestId('health-list');

    expect(within(list).getByText('Database')).toBeInTheDocument();
    expect(within(list).getByText('Database is up.')).toBeInTheDocument();
    expect(within(list).getByText('warning')).toBeInTheDocument();
  });

  it('shows an error state when the status request fails', async () => {
    const failing: FetchHandler = (url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse({ title: 'Server error' }, 500);
      }

      return jsonResponse(HEALTH_ENTRIES);
    };

    installFetch(failing);

    renderApp();

    await waitFor(() => expect(screen.getByText('The status request failed.')).toBeInTheDocument());
    expect(screen.queryByTestId('system-status')).not.toBeInTheDocument();
  });

  it('shows an empty state when no health check ran', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      return jsonResponse([]);
    });

    renderApp();

    expect(await screen.findByText('No health checks ran.')).toBeInTheDocument();
  });
});
