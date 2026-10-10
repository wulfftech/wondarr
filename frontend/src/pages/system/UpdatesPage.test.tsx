import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UpdateStatus } from '../../api/update';
import { DISMISSED_UPDATE_KEY } from '../../components/UpdateBadge';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

const AVAILABLE: UpdateStatus = {
  currentVersion: '0.1.0',
  isDevelopmentBuild: false,
  latestVersion: '0.2.0',
  updateAvailable: true,
  releaseName: 'Wondarr 0.2.0',
  releaseUrl: 'https://github.com/wulfftech/wondarr/releases/tag/v0.2.0',
  releaseNotes: '## Added\n\n- the **update checker**\n- a <b>tag</b> and [bad](javascript:alert(1))',
  publishedAt: '2026-10-09T10:00:00Z',
  checkedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
  lastError: null,
  checkEnabled: true,
};

function install(update: UpdateStatus | (() => Response), check?: () => Response | Promise<Response>): FetchMock {
  return installFetch((url, init) => {
    if (url.includes('/api/v1/update/check') && init?.method === 'POST') {
      return check?.() ?? jsonResponse(typeof update === 'function' ? {} : update);
    }

    if (url.includes('/api/v1/update/settings')) {
      return jsonResponse({ checkEnabled: true, checkEnabledLocked: false });
    }

    if (url.includes('/api/v1/update')) {
      return typeof update === 'function' ? update() : jsonResponse(update);
    }

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    return new Response('not found', { status: 404 });
  });
}

beforeEach(() => {
  window.localStorage.clear();
  resetLocation('/system/updates');
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('UpdatesPage', () => {
  it('shows the release, its notes as rendered markdown, and how to update', async () => {
    install(AVAILABLE);

    renderApp();

    const available = await screen.findByTestId('update-available');

    expect(within(available).getByText('Wondarr 0.2.0')).toBeInTheDocument();
    expect(within(available).getByRole('link', { name: 'View this release on GitHub' })).toHaveAttribute(
      'href',
      'https://github.com/wulfftech/wondarr/releases/tag/v0.2.0',
    );

    const notes = within(available).getByTestId('markdown');
    expect(within(notes).getByRole('heading', { name: 'Added' })).toBeInTheDocument();
    expect(within(notes).getByText('update checker').tagName).toBe('STRONG');
    // The HTML in the notes is text, and the javascript: link is not a link.
    expect(notes).toHaveTextContent('<b>tag</b>');
    expect(notes.querySelector('b')).toBeNull();
    expect(notes.querySelector('a[href^="javascript"]')).toBeNull();

    const how = screen.getByTestId('how-to-update');
    expect(how).toHaveTextContent('docker compose pull && docker compose up -d');
    expect(how).toHaveTextContent(':0.1');
    expect(how).toHaveTextContent('docker run');
    expect(how).toHaveTextContent('Unraid');

    const versions = screen.getByTestId('update-versions');
    expect(versions).toHaveTextContent('0.1.0');
    expect(versions).toHaveTextContent('0.2.0');
    expect(versions).toHaveTextContent('Last checked 5 minutes ago');
  });

  it('says it is up to date when the latest release is the running one', async () => {
    install({ ...AVAILABLE, currentVersion: '0.2.0', updateAvailable: false });

    renderApp();

    expect(await screen.findByText("You're up to date.")).toBeInTheDocument();
    expect(screen.queryByTestId('update-available')).not.toBeInTheDocument();
    expect(screen.queryByTestId('how-to-update')).not.toBeInTheDocument();
  });

  it('names the development build and the latest release', async () => {
    install({ ...AVAILABLE, currentVersion: '0.0.0-develop.7', isDevelopmentBuild: true, updateAvailable: false });

    renderApp();

    expect(await screen.findByText(/You're running a development build \(0\.0\.0-develop\.7\)/)).toBeInTheDocument();
    expect(screen.getByText(/The latest release is 0\.2\.0\./)).toBeInTheDocument();
    expect(screen.queryByTestId('update-available')).not.toBeInTheDocument();
  });

  it('says the checks are off and links to the setting', async () => {
    install({ ...AVAILABLE, checkEnabled: false, updateAvailable: false, latestVersion: null, checkedAt: null });

    renderApp();

    expect(await screen.findByText('Update checks are off')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Settings → General' })).toHaveAttribute('href', '/settings/general');
    expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled();
  });

  it('shows the last error without hiding the last good answer', async () => {
    install({ ...AVAILABLE, lastError: "GitHub's rate limit; next check after 14:05." });

    renderApp();

    expect(await screen.findByTestId('update-error')).toHaveTextContent("GitHub's rate limit; next check after 14:05.");
    expect(screen.getByTestId('update-available')).toBeInTheDocument();
  });

  it('shows an error state when the status request fails', async () => {
    install(() => jsonResponse({ title: 'Server error' }, 500));

    renderApp();

    expect(await screen.findByText('The update status request failed.')).toBeInTheDocument();
  });

  it('spins only the Check now button while a check runs', async () => {
    const user = userEvent.setup();
    let finish: (response: Response) => void = () => undefined;
    install(AVAILABLE, () => new Promise<Response>((resolve) => (finish = resolve)));

    renderApp();

    await screen.findByTestId('update-available');
    const button = screen.getByRole('button', { name: 'Check now' });

    await user.click(button);

    await waitFor(() => expect(button).toHaveAttribute('data-loading', 'true'));
    // The page, the versions table and the nav stay as they were: the only loading element is the button.
    for (const element of document.querySelectorAll('[data-loading="true"]')) {
      expect(button.contains(element)).toBe(true);
    }
    expect(screen.getByTestId('update-available')).toBeInTheDocument();
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();

    finish(jsonResponse({ ...AVAILABLE, latestVersion: '0.3.0' }));

    await waitFor(() => expect(button).not.toHaveAttribute('data-loading', 'true'));
    expect(within(screen.getByTestId('update-versions')).getByText('0.3.0')).toBeInTheDocument();
  });
});

describe('the Update available badge', () => {
  it('is shown in the header when an update is available and links to the Updates page', async () => {
    install(AVAILABLE);

    renderApp();

    const badge = await screen.findByTestId('update-badge');

    expect(within(badge).getByRole('link', { name: 'Update available' })).toHaveAttribute('href', '/system/updates');
  });

  it('is hidden when the install is up to date', async () => {
    install({ ...AVAILABLE, updateAvailable: false, currentVersion: '0.2.0' });

    renderApp();

    await screen.findByText("You're up to date.");
    expect(screen.queryByTestId('update-badge')).not.toBeInTheDocument();
  });

  it('is hidden for a development build', async () => {
    install({ ...AVAILABLE, isDevelopmentBuild: true, updateAvailable: true });

    renderApp();

    await screen.findByTestId('update-versions');
    expect(screen.queryByTestId('update-badge')).not.toBeInTheDocument();
  });

  it('is dismissed for that version only, and returns for a newer one', async () => {
    const user = userEvent.setup();
    let current = AVAILABLE;
    install(
      () => jsonResponse(current),
      () => jsonResponse(current),
    );

    const { unmount } = renderApp();

    const badge = await screen.findByTestId('update-badge');
    await user.click(within(badge).getByRole('button', { name: 'Dismiss the notice about 0.2.0' }));

    expect(screen.queryByTestId('update-badge')).not.toBeInTheDocument();
    expect(window.localStorage.getItem(DISMISSED_UPDATE_KEY)).toBe('0.2.0');

    // A reload with the same version: still dismissed.
    unmount();
    renderApp();
    await screen.findByTestId('update-versions');
    expect(screen.queryByTestId('update-badge')).not.toBeInTheDocument();

    // A newer release shows it again.
    current = { ...AVAILABLE, latestVersion: '0.3.0' };
    await user.click(screen.getByRole('button', { name: 'Check now' }));

    expect(await screen.findByTestId('update-badge')).toBeInTheDocument();
  });

  it('still works when localStorage throws', async () => {
    const user = userEvent.setup();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    install(AVAILABLE);

    renderApp();

    const badge = await screen.findByTestId('update-badge');
    await user.click(within(badge).getByRole('button', { name: /Dismiss/ }));

    expect(screen.queryByTestId('update-badge')).not.toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe('the Status page Update row', () => {
  it('says what the update check found and links to the Updates page', async () => {
    resetLocation('/system/status');
    install(AVAILABLE);

    renderApp();

    const row = await screen.findByTestId('update-row');

    expect(row).toHaveTextContent('0.2.0 available');
    expect(row).toHaveAttribute('href', '/system/updates');
  });
});
