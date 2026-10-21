import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { useFeedbackStatus } from '../api/feedback';
import { applyEventMessage, EventStreamStatusContext } from '../app/signalr';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { theme } from '../theme/theme';
import { AppLayout } from './AppLayout';

/**
 * The shell on its own: the phone-width navbar toggle and the live-updates indicator. jsdom applies
 * no media queries, so "collapsed" is read from the stylesheet Mantine emits for the mobile breakpoint.
 */

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const FEEDBACK_STATUS = {
  tokenConfigured: true,
  autoRaise: true,
  repository: 'wulfftech/wondarr',
  repositoryUrl: 'https://github.com/wulfftech/wondarr',
  maxTextLength: 4000,
};

function renderLayout(connected = true, client = new QueryClient()) {
  installFetch(() => jsonResponse([]));

  return renderShell(connected, client);
}

/** Shows when the feedback status has an answer; it shares the header button's query, so they render together. */
function FeedbackStatusProbe() {
  const status = useFeedbackStatus();

  return status.data === undefined ? null : <span data-testid="feedback-status-answered" />;
}

/** The shell with whatever `fetch` the test installed. */
function renderShell(connected = true, client = new QueryClient()) {
  return render(
    <MantineProvider theme={theme} env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>
          <EventStreamStatusContext.Provider value={connected}>
            <MemoryRouter>
              <FeedbackStatusProbe />
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

  it('shows the disconnected badge only after a grace period, and announces it politely', () => {
    vi.useFakeTimers();
    const { unmount } = renderLayout(true);

    act(() => {
      vi.advanceTimersByTime(5000);
    });
    expect(screen.queryByText(/Live updates disconnected/)).not.toBeInTheDocument();

    unmount();
    renderLayout(false);

    // The live region is always there, and empty until the grace period has passed.
    const region = screen.getByRole('status');

    expect(region).toHaveAttribute('aria-live', 'polite');
    expect(region).toHaveTextContent('');

    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(region).toHaveTextContent('');

    act(() => {
      vi.advanceTimersByTime(1500);
    });
    expect(region).toHaveTextContent('Live updates disconnected — reconnecting…');
  });

  it('closes the navbar when the link for the page already open is tapped', async () => {
    const user = userEvent.setup();
    renderLayout();

    await user.click(screen.getByRole('link', { name: 'Wanted' }));
    await user.click(screen.getByRole('button', { name: 'Toggle navigation' }));
    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(false));

    await user.click(screen.getByRole('link', { name: 'Wanted' }));

    await waitFor(() => expect(navbarCollapsedOnMobile()).toBe(true));
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

  it('shows no feedback button without a token', async () => {
    installFetch((url) =>
      url.endsWith('/api/v1/feedback')
        ? jsonResponse({ ...FEEDBACK_STATUS, tokenConfigured: false })
        : jsonResponse([]),
    );
    renderShell();

    await screen.findByTestId('feedback-status-answered');

    expect(screen.queryByRole('button', { name: 'Send feedback' })).not.toBeInTheDocument();
  });

  it('puts the feedback button right before the theme toggle once a token is set', async () => {
    installFetch((url) => (url.endsWith('/api/v1/feedback') ? jsonResponse(FEEDBACK_STATUS) : jsonResponse([])));
    renderShell();

    await screen.findByRole('button', { name: 'Send feedback' });

    const names = within(screen.getByRole('banner'))
      .getAllByRole('button')
      .map((button) => button.getAttribute('aria-label'));

    expect(names.indexOf('Toggle the colour scheme') - names.indexOf('Send feedback')).toBe(1);
  });
});
