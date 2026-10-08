import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DOWNLOAD_CLIENT_SCHEMA, DOWNLOAD_CLIENTS, HEALTH_ENTRIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/downloadclients');
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

async function lastRequest(method: string, urlPart: string): Promise<Record<string, unknown> | null> {
  const calls = sent().filter((request) => request.method === method && request.url.includes(urlPart));
  const call = calls[calls.length - 1];

  return call === undefined ? null : (JSON.parse(await call.body) as Record<string, unknown>);
}

function install(options: { remove?: () => Response; test?: () => Response } = {}): FetchMock {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/downloadclient/schema')) {
      return jsonResponse(DOWNLOAD_CLIENT_SCHEMA);
    }

    if (url.includes('/api/v1/downloadclient/test')) {
      return options.test?.() ?? jsonResponse({ success: true, error: null });
    }

    if (url.includes('/api/v1/downloadclient')) {
      if (method === 'POST') {
        return jsonResponse(DOWNLOAD_CLIENTS[0], 201);
      }

      if (method === 'PUT') {
        return jsonResponse(DOWNLOAD_CLIENTS[0]);
      }

      if (method === 'DELETE') {
        return options.remove?.() ?? new Response(null, { status: 204 });
      }

      return jsonResponse(DOWNLOAD_CLIENTS);
    }

    return new Response('not found', { status: 404 });
  });
}

async function row(name: string): Promise<HTMLElement> {
  const cells = await screen.findAllByText(name);
  const found = cells.map((cell) => cell.closest('tr')).find((candidate) => candidate !== null);

  if (found == null) {
    throw new Error(`the ${name} row was not found`);
  }

  return found;
}

async function addClient(user: UserEvent, type: string): Promise<HTMLElement> {
  await user.click(await screen.findByRole('button', { name: 'Add download client' }));
  await user.click(await screen.findByRole('radio', { name: type }));
  await user.click(screen.getByRole('button', { name: 'Continue' }));

  return screen.getByRole('dialog');
}

describe('DownloadClientsSettingsPage', () => {
  it('lists the clients with their type and protocol', async () => {
    install();

    renderApp();

    const torrent = await row('Torrent');
    expect(within(torrent).getAllByText('qBittorrent').length).toBeGreaterThan(0);
    const usenet = await row('Usenet');
    expect(within(usenet).getAllByText('SABnzbd').length).toBeGreaterThan(0);
  });

  it('adds a qBittorrent with its remote path mappings', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    const dialog = await addClient(user, 'qBittorrent');
    await user.type(within(dialog).getByLabelText('Host *'), 'qbittorrent');
    await user.click(within(dialog).getByRole('button', { name: 'Add remote path mappings row' }));
    await user.type(within(dialog).getByLabelText('Remote path mappings path in the client 1'), '/downloads');
    await user.type(within(dialog).getByLabelText('Remote path mappings path wondarr sees 1'), '/data/torrents');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await lastRequest('POST', '/api/v1/downloadclient')).not.toBeNull());
    expect(await lastRequest('POST', '/api/v1/downloadclient')).toMatchObject({
      type: 'qbittorrent',
      enabled: true,
      priority: 1,
      settings: { host: 'qbittorrent', remotePathMappings: [{ key: '/downloads', value: '/data/torrents' }] },
    });
  });

  it('round-trips the stored path mappings and keeps the masked password', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    await user.click(within(await row('Torrent')).getByRole('button', { name: 'Edit' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('Remote path mappings path in the client 1')).toHaveValue('/downloads');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await lastRequest('PUT', '/api/v1/downloadclient/3')).not.toBeNull());
    expect(await lastRequest('PUT', '/api/v1/downloadclient/3')).toMatchObject({
      settings: { password: '********', remotePathMappings: [{ key: '/downloads', value: '/data/torrents' }] },
    });
  });

  it('shows the test failure the client reports', async () => {
    const user = userEvent.setup();
    install({ test: () => jsonResponse({ success: false, error: 'SABnzbd refused the API key.' }) });
    renderApp();

    const dialog = await addClient(user, 'SABnzbd');
    await user.click(within(dialog).getByRole('button', { name: 'Test' }));

    expect(await within(dialog).findByText('SABnzbd refused the API key.')).toBeInTheDocument();
  });

  it('shows why a client still named by an indexer cannot be deleted', async () => {
    const user = userEvent.setup();
    install({
      remove: () =>
        jsonResponse(
          { title: 'Conflict', status: 409, detail: "The download client is used by the indexers: 'Usenet indexer'." },
          409,
        ),
    });
    renderApp();

    await user.click(within(await row('Usenet')).getByRole('button', { name: 'Delete' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete' }));

    expect(
      await screen.findByText("The download client is used by the indexers: 'Usenet indexer'."),
    ).toBeInTheDocument();
  });
});
