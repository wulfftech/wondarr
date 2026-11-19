import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { createTestConfig, installFetch, jsonResponse } from '../test/helpers';
import { FolderPicker } from './FolderPicker';

const ROOTS = [
  { path: '/data/downloads', unavailable: false },
  { path: '/data/music', unavailable: false },
];

/** A tiny file system the mocked API serves. */
function install() {
  const created: string[] = [];

  installFetch((url, init) => {
    const parsed = new URL(url, 'http://localhost');

    if (parsed.pathname.endsWith('/api/v1/filesystem/folder') && init?.method === 'POST') {
      created.push('x');

      return jsonResponse({ name: 'Clips', path: '/data/music/Clips' });
    }

    if (parsed.pathname.endsWith('/api/v1/filesystem')) {
      const path = parsed.searchParams.get('path');

      if (path === null) {
        return jsonResponse({ path: null, parent: null, folders: [], roots: ROOTS });
      }

      if (path === '/data/music') {
        return jsonResponse({
          path,
          parent: null,
          folders: [
            { name: 'Albums', path: '/data/music/Albums' },
            { name: 'Clips', path: '/data/music/Clips' },
          ],
          roots: ROOTS,
        });
      }

      if (path === '/data/music/Albums') {
        return jsonResponse({ path, parent: '/data/music', folders: [], roots: ROOTS });
      }

      if (path === '/data/music/Clips') {
        return jsonResponse({ path, parent: '/data/music', folders: [], roots: ROOTS });
      }

      return jsonResponse({ title: 'Not found', detail: 'That folder does not exist.' }, 404);
    }

    return new Response('not found', { status: 404 });
  });

  return created;
}

function renderPicker(onSelect = vi.fn(), onClose = vi.fn(), initialPath: string | null = null) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={queryClient}>
          <FolderPicker opened onClose={onClose} onSelect={onSelect} initialPath={initialPath} />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );

  return { onSelect, onClose };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FolderPicker', () => {
  it('offers only the roots first, with nothing to select and no way to make a folder', async () => {
    install();
    renderPicker();

    expect(await screen.findByRole('button', { name: '/data/music' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '/data/downloads' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select this folder' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'New folder…' })).not.toBeInTheDocument();
  });

  it('goes into a root and a folder, shows the breadcrumb, and selects the folder it is in', async () => {
    install();
    const user = userEvent.setup();
    const { onSelect, onClose } = renderPicker();

    await user.click(await screen.findByRole('button', { name: '/data/music' }));
    await user.click(await screen.findByRole('button', { name: 'Albums' }));

    expect(await screen.findByText('No folders in here.')).toBeInTheDocument();
    // The breadcrumb leads back up to the roots and to the mount.
    expect(screen.getByRole('button', { name: 'Mounted folders' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '/data/music' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Select this folder' }));

    expect(onSelect).toHaveBeenCalledWith('/data/music/Albums');
    expect(onClose).toHaveBeenCalled();
  });

  it('steps back up through the breadcrumb', async () => {
    install();
    const user = userEvent.setup();
    renderPicker();

    await user.click(await screen.findByRole('button', { name: '/data/music' }));
    await user.click(await screen.findByRole('button', { name: 'Albums' }));
    await user.click(await screen.findByRole('button', { name: '/data/music' }));

    expect(await screen.findByRole('button', { name: 'Clips' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Mounted folders' }));

    expect(await screen.findByRole('button', { name: '/data/downloads' })).toBeInTheDocument();
  });

  it('makes a new folder inside a mount and goes into it', async () => {
    const created = install();
    const user = userEvent.setup();
    renderPicker();

    await user.click(await screen.findByRole('button', { name: '/data/music' }));
    await user.click(await screen.findByRole('button', { name: 'New folder…' }));
    await user.type(screen.getByLabelText('New folder name'), 'Clips');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(created).toHaveLength(1));
    expect(await screen.findByText('No folders in here.')).toBeInTheDocument();
  });

  it('shows the server’s reason when a folder cannot be opened, and a way back to the roots', async () => {
    install();
    const user = userEvent.setup();
    renderPicker(vi.fn(), vi.fn(), '/data/gone');

    expect(await screen.findByText('That folder does not exist.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show mounted folders' }));

    expect(await screen.findByRole('button', { name: '/data/music' })).toBeInTheDocument();
  });

  it('greys out a root that is not responding and still offers the others', async () => {
    installFetch(() =>
      jsonResponse({
        path: null,
        parent: null,
        folders: [],
        roots: [
          { path: '/data/hung', unavailable: true },
          { path: '/data/music', unavailable: false },
        ],
      }),
    );
    renderPicker();

    expect(await screen.findByRole('button', { name: '/data/hung (not responding)' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '/data/music' })).toBeEnabled();
  });

  it('opens at the current folder and shows a Windows drive as the first step', async () => {
    installFetch((url) => {
      const path = new URL(url, 'http://localhost').searchParams.get('path');

      return jsonResponse({
        path,
        parent: 'C:\\Media',
        folders: [],
        roots: [{ path: 'C:\\', unavailable: false }],
      });
    });
    const user = userEvent.setup();
    const { onSelect } = renderPicker(vi.fn(), vi.fn(), 'C:\\Media\\Videos');

    expect(await screen.findByRole('button', { name: 'C:\\' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Media' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Select this folder' }));

    expect(onSelect).toHaveBeenCalledWith('C:\\Media\\Videos');
  });

  it('says so when nothing is mounted', async () => {
    installFetch(() => jsonResponse({ path: null, parent: null, folders: [], roots: [] }));
    renderPicker();

    expect(await screen.findByText(/No folder is mounted into this container/)).toBeInTheDocument();
  });
});
