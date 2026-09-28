import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { PREVIEW } from '../test/fixtures';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { PreviewButton } from './PreviewButton';

/**
 * jsdom has no media stack at all: `play` throws "Not implemented" unless it is stubbed, and the
 * shared element the buttons play through is the browser's own `Audio`.
 */

let play: Mock<() => Promise<void>>;
let pause: Mock<() => void>;

beforeEach(() => {
  play = vi.fn(() => Promise.resolve());
  pause = vi.fn();

  vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(play);
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(pause);
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

/** Renders a bare component with the providers the API hooks read. */
function renderPreview(ui: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider>
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

function installPreview(response: () => Response) {
  return installFetch((url) => (url.includes('/api/v1/preview') ? response() : new Response('', { status: 404 })));
}

describe('PreviewButton', () => {
  it('asks for a fresh url and plays it', async () => {
    const mock = installPreview(() => jsonResponse(PREVIEW));
    const user = userEvent.setup();

    renderPreview(<PreviewButton deezerId={66877419} />);

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await waitFor(() => expect(play).toHaveBeenCalled());
    expect(mock.calls.some((call) => call.url.includes('/api/v1/preview?deezerId=66877419'))).toBe(true);
  });

  it('falls back to the first ISRC when the candidate has no Deezer id', async () => {
    const mock = installPreview(() => jsonResponse(PREVIEW));
    const user = userEvent.setup();

    renderPreview(<PreviewButton deezerId={null} isrcs={['', 'USQX91300516']} />);

    await user.click(screen.getByRole('button', { name: 'Preview' }));

    await waitFor(() =>
      expect(mock.calls.some((call) => call.url.includes('/api/v1/preview?isrc=USQX91300516'))).toBe(true),
    );
  });

  it('stops the first preview when a second one starts', async () => {
    installPreview(() => jsonResponse(PREVIEW));
    const user = userEvent.setup();

    renderPreview(
      <>
        <PreviewButton deezerId={1} />
        <PreviewButton deezerId={2} />
      </>,
    );

    const [first, second] = screen.getAllByRole('button', { name: 'Preview' });

    await user.click(first);
    await waitFor(() => expect(first).toHaveAttribute('aria-pressed', 'true'));

    await user.click(second);
    await waitFor(() => expect(second).toHaveAttribute('aria-pressed', 'true'));

    expect(first).toHaveAttribute('aria-pressed', 'false');
    expect(pause).toHaveBeenCalled();
  });

  it('disables the button when the track has no preview', async () => {
    installPreview(() => jsonResponse({ title: 'Not found' }, 404));
    const user = userEvent.setup();

    renderPreview(<PreviewButton deezerId={66877419} />);

    const button = screen.getByRole('button', { name: 'Preview' });

    await user.click(button);

    await waitFor(() => expect(button).toBeDisabled());
  });

  it('disables the button when there is nothing to look the preview up by', () => {
    installPreview(() => jsonResponse(PREVIEW));

    renderPreview(<PreviewButton deezerId={null} />);

    expect(screen.getByRole('button', { name: 'Preview' })).toBeDisabled();
  });
});
