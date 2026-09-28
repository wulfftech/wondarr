import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, paged, QUALITY_PROFILES, SONGS, SYSTEM_STATUS } from '../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../test/helpers';

beforeEach(() => {
  resetLocation('/wanted/missing');
});

afterEach(() => {
  vi.unstubAllGlobals();
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
    expect(screen.getByText('Massive Attack')).toBeInTheDocument();
    expect(screen.getByText('Mezzanine')).toBeInTheDocument();
    // 369000 ms reads as 6:09, not 6.15 or 369.
    expect(screen.getByText('6:09')).toBeInTheDocument();
    expect(screen.getByText('Standard 320')).toBeInTheDocument();
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
