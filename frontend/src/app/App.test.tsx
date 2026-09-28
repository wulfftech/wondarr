import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS, TASKS } from '../test/fixtures';
import { createTestConfig, installFetch, jsonResponse, renderApp, resetLocation } from '../test/helpers';

/** The default route table: everything the shell asks for, all healthy. */
function defaultHandler(url: string, init?: RequestInit): Response {
  if (url.includes('/api/v1/system/status')) {
    return jsonResponse(SYSTEM_STATUS);
  }

  if (url.includes('/api/v1/health')) {
    return jsonResponse(HEALTH_ENTRIES);
  }

  if (url.includes('/api/v1/system/task')) {
    return jsonResponse(TASKS);
  }

  if (url.includes('/api/v1/command')) {
    return new Response(null, { status: init?.method === 'POST' ? 201 : 204 });
  }

  return new Response('not found', { status: 404 });
}

beforeEach(() => {
  resetLocation('/');
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('App', () => {
  it('navigates between all five sections', async () => {
    installFetch(defaultHandler);
    const user = userEvent.setup();

    renderApp();

    await waitFor(() => expect(screen.getByText('Arrives in Phase 1')).toBeInTheDocument());
    expect(window.location.pathname).toBe('/library');

    await user.click(screen.getByRole('link', { name: 'Wanted' }));
    await waitFor(() => expect(window.location.pathname).toBe('/wanted'));

    await user.click(screen.getByRole('link', { name: 'Activity' }));
    await waitFor(() => expect(window.location.pathname).toBe('/activity'));

    await user.click(screen.getByRole('link', { name: 'Settings' }));
    await waitFor(() => expect(window.location.pathname).toBe('/settings'));

    await user.click(screen.getByRole('link', { name: 'System' }));
    await waitFor(() => expect(window.location.pathname).toBe('/system/status'));
    expect(await screen.findByRole('heading', { name: 'Status' })).toBeInTheDocument();
  });

  it('redirects the site root to the library', async () => {
    installFetch(defaultHandler);

    renderApp();

    await waitFor(() => expect(screen.getByText('Arrives in Phase 1')).toBeInTheDocument());
    expect(window.location.pathname).toBe('/library');
  });

  it('shows a not-found page for an unknown route', async () => {
    resetLocation('/does-not-exist');
    installFetch(defaultHandler);

    renderApp();

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
  });

  it('flips the colour scheme when the theme toggle is used', async () => {
    installFetch(defaultHandler);
    const user = userEvent.setup();

    renderApp();

    await waitFor(() => expect(document.documentElement.dataset.mantineColorScheme).toBe('light'));

    await user.click(screen.getByRole('button', { name: 'Toggle the colour scheme' }));

    await waitFor(() => expect(document.documentElement.dataset.mantineColorScheme).toBe('dark'));
  });

  it('counts only the health entries that are not ok', async () => {
    installFetch(defaultHandler);

    renderApp();

    // One of the two fixture entries is a warning.
    expect(await screen.findByLabelText('1 health issue')).toHaveTextContent('1');
  });

  it('renders the configured instance name in the header', async () => {
    installFetch(defaultHandler);

    renderApp(createTestConfig({ instanceName: 'Home server' }));

    expect(await screen.findByText('Home server')).toBeInTheDocument();
  });
});
