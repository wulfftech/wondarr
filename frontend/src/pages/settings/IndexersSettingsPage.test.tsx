import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DOWNLOAD_CLIENTS, HEALTH_ENTRIES, INDEXER_SCHEMA, INDEXERS, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/indexers');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
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

function install(options: { test?: () => Response } = {}): FetchMock {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/downloadclient')) {
      return jsonResponse(DOWNLOAD_CLIENTS);
    }

    if (url.includes('/api/v1/indexer/schema')) {
      return jsonResponse(INDEXER_SCHEMA);
    }

    if (url.includes('/api/v1/indexer/test')) {
      return options.test?.() ?? jsonResponse({ success: true, error: null });
    }

    if (url.includes('/api/v1/indexer')) {
      if (method === 'POST') {
        return jsonResponse(INDEXERS[0], 201);
      }

      if (method === 'PUT') {
        return jsonResponse(INDEXERS[0]);
      }

      if (method === 'DELETE') {
        return new Response(null, { status: 204 });
      }

      return jsonResponse(INDEXERS);
    }

    return new Response('not found', { status: 404 });
  });
}

async function addIndexer(user: UserEvent, type: string): Promise<HTMLElement> {
  await user.click(await screen.findByRole('button', { name: 'Add indexer' }));
  await user.click(await screen.findByRole('radio', { name: type }));
  await user.click(screen.getByRole('button', { name: 'Continue' }));

  return screen.getByRole('dialog');
}

async function row(name: string): Promise<HTMLElement> {
  const cell = (await screen.findByText(name)).closest('tr');

  if (cell === null) {
    throw new Error(`the ${name} row was not found`);
  }

  return cell;
}

describe('IndexersSettingsPage', () => {
  it('lists the indexers with their type, protocol and client', async () => {
    install();

    renderApp();

    const torrents = await row('Prowlarr torrents');
    expect(within(torrents).getByText('Torznab')).toBeInTheDocument();
    expect(within(torrents).getByText('Torrent')).toBeInTheDocument();
    expect(within(torrents).getByText('Default torrent client')).toBeInTheDocument();

    const usenet = await row('Usenet indexer');
    expect(within(usenet).getByText('Newznab')).toBeInTheDocument();
    expect(within(usenet).getByText('Usenet')).toBeInTheDocument();
    expect(within(usenet).getByText('SABnzbd')).toBeInTheDocument();
  });

  it('adds a Torznab feed on the default client, without a protocol of its own', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    const dialog = await addIndexer(user, 'Torznab');
    expect(within(dialog).queryByRole('radio', { name: 'Usenet' })).not.toBeInTheDocument();

    await user.clear(within(dialog).getByLabelText('Name'));
    await user.type(within(dialog).getByLabelText('Name'), 'My tracker');
    await user.type(within(dialog).getByLabelText('URL *'), 'http://prowlarr:9696/1/');
    await user.type(within(dialog).getByLabelText('API key'), 'secret-key');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await lastRequest('POST', '/api/v1/indexer')).not.toBeNull());
    const body = await lastRequest('POST', '/api/v1/indexer');
    expect(body).toMatchObject({
      name: 'My tracker',
      type: 'torznab',
      protocol: null,
      enabled: true,
      priority: 25,
      downloadClientId: null,
      settings: { url: 'http://prowlarr:9696/1/', apiKey: 'secret-key' },
    });
  });

  it('lets a Prowlarr row choose its protocol', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    const dialog = await addIndexer(user, 'Prowlarr');
    await user.click(within(dialog).getByRole('radio', { name: 'Usenet' }));
    await user.type(within(dialog).getByLabelText('URL *'), 'http://prowlarr:9696');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await lastRequest('POST', '/api/v1/indexer')).not.toBeNull());
    expect(await lastRequest('POST', '/api/v1/indexer')).toMatchObject({ type: 'prowlarr', protocol: 'usenet' });
  });

  it('sends a stored secret back as the mask when it is left alone', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    await user.click(within(await row('Prowlarr torrents')).getByRole('button', { name: 'Edit' }));
    const dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => expect(await lastRequest('PUT', '/api/v1/indexer/1')).not.toBeNull());
    expect(await lastRequest('PUT', '/api/v1/indexer/1')).toMatchObject({ settings: { apiKey: '********' } });
  });

  it('shows the test result: connected, or the indexer error', async () => {
    const user = userEvent.setup();
    let success = true;
    install({
      test: () =>
        jsonResponse(
          success ? { success: true, error: null } : { success: false, error: 'The indexer refused the API key.' },
        ),
    });
    renderApp();

    const dialog = await addIndexer(user, 'Torznab');
    await user.click(within(dialog).getByRole('button', { name: 'Test' }));
    expect(await within(dialog).findByText('Connected')).toBeInTheDocument();

    success = false;
    await user.click(within(dialog).getByRole('button', { name: 'Test' }));
    expect(await within(dialog).findByText('The indexer refused the API key.')).toBeInTheDocument();
  });

  it('asks before deleting', async () => {
    const user = userEvent.setup();
    install();
    renderApp();

    await user.click(within(await row('Usenet indexer')).getByRole('button', { name: 'Delete' }));
    expect(sent().some((request) => request.method === 'DELETE')).toBe(false);

    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete' }));

    await waitFor(() =>
      expect(sent().some((request) => request.method === 'DELETE' && request.url.includes('/api/v1/indexer/2'))).toBe(
        true,
      ),
    );
  });
});
