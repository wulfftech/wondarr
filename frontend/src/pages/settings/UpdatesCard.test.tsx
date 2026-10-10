import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/general');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** The General page with the update setting answering as given; everything else it reads is a stub. */
function install(settings: { checkEnabled: boolean; checkEnabledLocked: boolean }, put?: () => Response) {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/update/settings')) {
      return init?.method === 'PUT' && put !== undefined ? put() : jsonResponse(settings);
    }

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/metadata/settings')) {
      return jsonResponse({ acoustIdKeySet: false, acoustIdLocked: false, lastFmKeySet: false, lastFmLocked: false });
    }

    if (url.includes('/api/v1/auth/user')) {
      return jsonResponse({ configured: false, username: null });
    }

    return new Response('not found', { status: 404 });
  });
}

describe('Settings → General → Updates', () => {
  it('explains the check and switches it off', async () => {
    const user = userEvent.setup();
    install({ checkEnabled: true, checkEnabledLocked: false }, () =>
      jsonResponse({ checkEnabled: false, checkEnabledLocked: false }),
    );

    renderApp();

    const toggle = await screen.findByRole('switch', { name: /Check for new versions on GitHub/ });

    expect(toggle).toBeChecked();
    expect(screen.getByText(/Nothing about your library is sent/)).toBeInTheDocument();

    await user.click(toggle);

    await waitFor(() => expect(toggle).not.toBeChecked());

    const put = vi
      .mocked(globalThis.fetch)
      .mock.calls.map(([input, init]) => (input instanceof Request ? input : new Request(input, init)))
      .find((request) => request.method === 'PUT' && request.url.includes('/api/v1/update/settings'));

    expect(JSON.parse(await (put?.clone().text() ?? Promise.resolve('{}')))).toEqual({ checkEnabled: false });
  });

  it('shows a setting the environment owns as read-only', async () => {
    install({ checkEnabled: false, checkEnabledLocked: true });

    renderApp();

    const toggle = await screen.findByRole('switch', { name: /Check for new versions on GitHub/ });

    expect(toggle).toBeDisabled();
    expect(screen.getByText(/APP__UPDATE__CHECK_ENABLED/)).toBeInTheDocument();
  });
});
