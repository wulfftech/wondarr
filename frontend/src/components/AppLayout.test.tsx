import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { applyEventMessage, EventStreamStatusContext } from '../app/signalr';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { theme } from '../theme/theme';
import { AppLayout } from './AppLayout';

/**
 * The shell on its own: the phone-width navbar toggle and the live-updates indicator. jsdom applies
 * no media queries, so "collapsed" is read from the stylesheet Mantine emits for the mobile breakpoint.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderLayout(connected = true, client = new QueryClient()) {
  installFetch(() => jsonResponse([]));

  return render(
    <MantineProvider theme={theme} env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>
          <EventStreamStatusContext.Provider value={connected}>
            <MemoryRouter>
              <AppLayout />
            </MemoryRouter>
          </EventStreamStatusContext.Provider>
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

/** Whether Mantine's mobile media query currently slides the navbar out of view. */
function navbarCollapsedOnMobile(): boolean {
  const css = [...document.querySelectorAll('style')].map((style) => style.textContent ?? '').join('\n');
  const mobile = /@media\s*\(max-width:[^)]*\)\s*\{\s*:root\{[^}]*\}/.exec(css)?.[0] ?? '';

  return mobile.includes('--app-shell-navbar-transform:');
}

describe('AppLayout', () => {
  it('opens and closes the navbar from the burger at phone width', async () => {
    const user = userEvent.setup();
    renderLayout();

    const burger = screen.getByRole('button', { name: 'Toggle navigation' });

    expect(navbarCollapsedOnMobile()).toBe(true);

    await user.click(burger);
    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(false));

    await user.click(burger);
    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(true));
  });

  it('closes the navbar when a link is followed', async () => {
    const user = userEvent.setup();
    renderLayout();

    await user.click(screen.getByRole('button', { name: 'Toggle navigation' }));
    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(false));

    await user.click(screen.getByRole('link', { name: 'Wanted' }));

    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(true));
  });

  it('shows the disconnected indicator only while live updates are down', () => {
    const { unmount } = renderLayout(true);

    expect(screen.queryByRole('status')).not.toBeInTheDocument();

    unmount();
    renderLayout(false);

    expect(screen.getByRole('status')).toHaveAccessibleName('Live updates disconnected — reconnecting…');
  });

  it('updates the Health badge from a SignalR push in the real message shape', async () => {
    const client = new QueryClient();
    renderLayout(true, client);

    // The first read: nothing wrong.
    expect(await screen.findByLabelText('0 health issues')).toBeInTheDocument();

    act(() => {
      applyEventMessage(client, {
        name: 'health',
        body: {
          action: 'sync',
          resource: [{ source: 'Slskd', type: 'error', message: 'slskd is down', wikiUrl: null }],
        },
      });
    });

    expect(await screen.findByLabelText('1 health issue')).toBeInTheDocument();
  });
});
