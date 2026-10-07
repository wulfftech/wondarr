import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/system/backup');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const BACKUP = {
  id: 'wondarr_backup_v0.1.0_2026.10.07_06.00.00.zip',
  name: 'wondarr_backup_v0.1.0_2026.10.07_06.00.00.zip',
  path: '/backup/manual/wondarr_backup_v0.1.0_2026.10.07_06.00.00.zip',
  type: 'manual',
  size: 2_621_440,
  time: '2026-10-07T06:00:00Z',
};

function sent(): { url: string; method: string }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method };
  });
}

function install(restore: Response = jsonResponse({ restartRequired: true })): void {
  installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/system/backup/restore/')) {
      return restore;
    }

    if (url.includes('/api/v1/system/backup')) {
      if (init?.method === 'POST') {
        return jsonResponse(BACKUP, 201);
      }

      return init?.method === 'DELETE' ? new Response(null, { status: 204 }) : jsonResponse([BACKUP]);
    }

    if (url.endsWith('/ping')) {
      return jsonResponse({ status: 'OK' });
    }

    return new Response('not found', { status: 404 });
  });
}

describe('BackupPage', () => {
  it('lists the backups with their size and type', async () => {
    install();

    renderApp();

    expect(await screen.findByText(BACKUP.name)).toBeInTheDocument();
    expect(screen.getByText('2.5 MB')).toBeInTheDocument();
    expect(screen.getByText('manual')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: `Download ${BACKUP.name}` })).toHaveAttribute(
      'href',
      expect.stringContaining(`/api/v1/system/backup/${encodeURIComponent(BACKUP.id)}/download?apikey=test-api-key`),
    );
  });

  it('backs up now', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Back up now' }));

    await waitFor(() =>
      expect(sent().some((call) => call.method === 'POST' && call.url.endsWith('/api/v1/system/backup'))).toBe(true),
    );
  });

  it('deletes a backup only after the confirmation', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: `Delete ${BACKUP.name}` }));
    expect(sent().some((call) => call.method === 'DELETE')).toBe(false);

    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() =>
      expect(
        sent().some(
          (call) =>
            call.method === 'DELETE' && call.url.endsWith(`/api/v1/system/backup/${encodeURIComponent(BACKUP.id)}`),
        ),
      ).toBe(true),
    );
  });

  it('restores after the confirmation, which says the API key changes, then waits for the restart', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: `Restore ${BACKUP.name}` }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/API key/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Restore and restart' }));

    await waitFor(() =>
      expect(
        sent().some(
          (call) =>
            call.method === 'POST' &&
            call.url.endsWith(`/api/v1/system/backup/restore/${encodeURIComponent(BACKUP.id)}`),
        ),
      ).toBe(true),
    );
    expect(await screen.findByTestId('restoring')).toBeInTheDocument();
  });

  it('shows why a restore was refused', async () => {
    install(jsonResponse({ title: 'Invalid backup', detail: 'The zip holds no wondarr.db.' }, 400));
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: `Restore ${BACKUP.name}` }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Restore and restart' }));

    expect(await screen.findByText('The zip holds no wondarr.db.')).toBeInTheDocument();
  });
});
