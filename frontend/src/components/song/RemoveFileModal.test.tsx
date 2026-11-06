import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../../api/ApiProvider';
import type { SongResource } from '../../api/songs';
import { HEALTH_ENTRIES, LIBRARY_SONGS, SYSTEM_STATUS } from '../../test/fixtures';
import { createTestConfig, installFetch, jsonResponse, type FetchMock } from '../../test/helpers';
import { theme } from '../../theme/theme';
import { RemoveFileModal } from './RemoveFileModal';

/** "Get Lucky" with the file a Soulseek grab gave it. */
const SONG = {
  ...LIBRARY_SONGS[0],
  file: {
    path: '/data/music/Daft Punk/Singles/Get Lucky.mp3',
    codec: 'mp3',
    container: 'mp3',
    size: 9_000_000,
    sourceType: 'soulseek',
  },
} as unknown as SongResource;

const REMOVED = { path: '/data/music/Daft Punk/Singles/Get Lucky.mp3', recycledPath: '/config/recycle/Get Lucky.mp3', blocklisted: true };

function install(remove: () => Response = () => jsonResponse(REMOVED)): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (/\/api\/v1\/song\/\d+\/file/.test(url)) {
      return remove();
    }

    if (url.endsWith('/api/v1/command')) {
      return jsonResponse({ id: 40, name: 'SongSearch', status: 'queued' }, 201);
    }

    return new Response('not found', { status: 404 });
  });
}

function renderModal(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });

  return render(
    <MantineProvider theme={theme} defaultColorScheme="auto" env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>{ui}</QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );
}

function requests(mock: FetchMock, pattern: RegExp) {
  return mock.calls.filter((call) => pattern.test(call.url));
}

describe('RemoveFileModal', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('removes the file with the download blocklisted, then searches for the song again', async () => {
    const mock = install();
    const onClose = vi.fn();
    const user = userEvent.setup();

    renderModal(<RemoveFileModal song={SONG} opened onClose={onClose} />);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('/data/music/Daft Punk/Singles/Get Lucky.mp3')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Remove file' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const removal = requests(mock, /\/api\/v1\/song\/12\/file/);
    expect(removal).toHaveLength(1);
    expect(removal[0].url).toContain('blocklist=true');
    expect(removal[0].init?.method).toBe('DELETE');
    await waitFor(() => expect(requests(mock, /\/api\/v1\/command$/)).toHaveLength(1));
  });

  it('can leave the download allowed and skip the search', async () => {
    const mock = install();
    const onClose = vi.fn();
    const user = userEvent.setup();

    renderModal(<RemoveFileModal song={SONG} opened onClose={onClose} />);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox', { name: /Blocklist the download/ }));
    await user.click(within(dialog).getByRole('checkbox', { name: 'Search for the song now' }));
    await user.click(within(dialog).getByRole('button', { name: 'Remove file' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(requests(mock, /\/api\/v1\/song\/12\/file/)[0].url).toContain('blocklist=false');
    expect(requests(mock, /\/api\/v1\/command$/)).toHaveLength(0);
  });

  it("shows the server's reason when the file cannot be removed", async () => {
    install(() =>
      jsonResponse(
        { title: 'Your own copy', status: 409, detail: 'The song’s file is in a reference library, so it is your own copy.' },
        409,
      ),
    );
    const onClose = vi.fn();
    const user = userEvent.setup();

    renderModal(<RemoveFileModal song={SONG} opened onClose={onClose} />);
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove file' }));

    expect(await screen.findByText(/reference library, so it is your own copy/)).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});
