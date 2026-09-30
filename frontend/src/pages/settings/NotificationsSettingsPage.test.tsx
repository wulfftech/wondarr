import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, NOTIFICATIONS, NOTIFICATION_SCHEMA, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/notifications');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

/** The last request whose URL and method match, so a modal's save is read rather than the list's fetch. */
async function lastRequest(method: string, urlPart: string): Promise<Record<string, unknown> | null> {
  const calls = sent().filter((request) => request.method === method && request.url.includes(urlPart));
  const call = calls[calls.length - 1];

  return call === undefined ? null : (JSON.parse(await call.body) as Record<string, unknown>);
}

/**
 * The route table the page needs. The schema is matched before the collection, because its own URL
 * starts with the collection's.
 */
function install(options: { create?: () => Response; test?: () => Response } = {}): FetchMock {
  return installFetch((url, init) => {
    const method = init?.method ?? 'GET';

    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/notification/schema')) {
      return jsonResponse(NOTIFICATION_SCHEMA);
    }

    if (url.includes('/api/v1/notification/test')) {
      return options.test?.() ?? jsonResponse({});
    }

    if (url.includes('/api/v1/notification')) {
      if (method === 'POST') {
        return options.create?.() ?? jsonResponse(NOTIFICATIONS[0], 201);
      }

      if (method === 'PUT') {
        return jsonResponse(NOTIFICATIONS[1]);
      }

      if (method === 'DELETE') {
        return jsonResponse({});
      }

      return jsonResponse(NOTIFICATIONS);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens a Mantine Select by the label on its input and hands back the listbox it is showing. */
async function openSelect(user: UserEvent, label: string): Promise<HTMLElement> {
  const input = screen.getByLabelText(label, { selector: 'input' });

  await user.click(input);

  const listbox = document.getElementById(input.getAttribute('aria-controls') ?? '');

  if (listbox === null) {
    throw new Error(`the ${label} dropdown did not open`);
  }

  return listbox;
}

/** Opens a Mantine Select and picks one of its options by label. */
async function pickOption(user: UserEvent, label: string, option: string): Promise<void> {
  const listbox = await openSelect(user, label);

  await user.click(within(listbox).getByText(option));
}

/** Opens the add dialog and picks a provider from the schema. */
async function addNotification(user: UserEvent, implementation: string): Promise<HTMLElement> {
  await user.click(screen.getByRole('button', { name: 'Add notification' }));
  await pickOption(user, 'Type', implementation);
  await user.click(screen.getByRole('button', { name: 'Continue' }));

  return screen.getByRole('dialog');
}

/** Opens one row's edit dialog. */
async function editNotification(user: UserEvent, name: string): Promise<HTMLElement> {
  const row = (await screen.findByText(name)).closest('tr');

  if (row === null) {
    throw new Error(`the ${name} row was not found`);
  }

  await user.click(within(row).getByRole('button', { name: 'Edit' }));

  return screen.getByRole('dialog');
}

describe('NotificationsSettingsPage', () => {
  it('lists the notifications with their type and the events they fire on', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Home webhook')).toBeInTheDocument();
    expect(screen.getByText('Webhook')).toBeInTheDocument();
    expect(screen.getByText('Discord alerts')).toBeInTheDocument();
    expect(screen.getByText('Discord')).toBeInTheDocument();
    expect(screen.getByText('On grab')).toBeInTheDocument();
    expect(screen.getByText('On import')).toBeInTheDocument();
    expect(screen.getByText('On download failure')).toBeInTheDocument();
  });

  it('says so when no notification exists yet', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      if (url.includes('/api/v1/notification/schema')) {
        return jsonResponse(NOTIFICATION_SCHEMA);
      }

      if (url.includes('/api/v1/notification')) {
        return jsonResponse([]);
      }

      return new Response('not found', { status: 404 });
    });

    renderApp();

    expect(await screen.findByText('No notifications yet')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add notification' })).toBeInTheDocument();
  });

  it('renders the Webhook form from the schema and posts what it holds', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await addNotification(user, 'Webhook');

    expect(within(dialog).getByLabelText('URL *')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Method', { selector: 'input' })).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Username')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Password')).toBeInTheDocument();
    // The advanced field is inside the collapsed section rather than beside the basic ones.
    expect(within(dialog).getByText('Advanced').closest('details')).not.toBeNull();

    await user.type(within(dialog).getByLabelText('Name'), 'Home webhook');
    await user.type(within(dialog).getByLabelText('URL *'), 'https://hooks.example.com/wondarr');
    await pickOption(user, 'Method', 'PUT');
    await user.click(within(dialog).getByLabelText('On grab'));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const body = await lastRequest('POST', '/api/v1/notification');

      expect(body).toMatchObject({
        name: 'Home webhook',
        implementation: 'Webhook',
        enabled: true,
        events: ['grab'],
        settings: { url: 'https://hooks.example.com/wondarr', method: 'PUT' },
      });
    });
  });

  it('shows a rejected save inside the dialog', async () => {
    install({
      create: () =>
        jsonResponse(
          {
            title: 'One or more validation errors occurred.',
            errors: { settings: ['The URL must be an absolute http or https address.'] },
          },
          400,
        ),
    });
    const user = userEvent.setup();

    renderApp();

    const dialog = await addNotification(user, 'Webhook');

    await user.type(within(dialog).getByLabelText('Name'), 'Broken');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('The URL must be an absolute http or https address.')).toBeInTheDocument();
  });

  it('sends a test message from the form and reports success', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await addNotification(user, 'Webhook');

    await user.type(within(dialog).getByLabelText('URL *'), 'https://hooks.example.com/wondarr');
    await user.click(within(dialog).getByRole('button', { name: 'Test' }));

    expect(await within(dialog).findByText('Test sent')).toBeInTheDocument();

    const body = await lastRequest('POST', '/api/v1/notification/test');

    expect(body).toMatchObject({ implementation: 'Webhook', settings: { url: 'https://hooks.example.com/wondarr' } });
  });

  it('shows the error a failed test came back with', async () => {
    install({
      test: () => jsonResponse({ errors: { settings: ['The webhook refused the message.'] } }, 400),
    });
    const user = userEvent.setup();

    renderApp();

    const dialog = await addNotification(user, 'Webhook');

    await user.type(within(dialog).getByLabelText('URL *'), 'https://hooks.example.com/wondarr');
    await user.click(within(dialog).getByRole('button', { name: 'Test' }));

    expect(await within(dialog).findByText('The webhook refused the message.')).toBeInTheDocument();
  });

  it('keeps a masked secret and sends the mask back when it is left alone', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await editNotification(user, 'Discord alerts');
    const secret = within(dialog).getByLabelText('Webhook URL *');

    // The stored value is never shown; the mask is only a placeholder.
    expect(secret).toHaveAttribute('placeholder', '********');
    expect(secret).toHaveValue('');

    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const body = await lastRequest('PUT', '/api/v1/notification/2');

      expect(body).toMatchObject({ settings: { webHookUrl: '********' } });
    });
  });

  it('sends a new secret once the user types one', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const dialog = await editNotification(user, 'Discord alerts');

    await user.type(within(dialog).getByLabelText('Webhook URL *'), 'https://discord.com/api/webhooks/1/token');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const body = await lastRequest('PUT', '/api/v1/notification/2');

      expect(body).toMatchObject({ settings: { webHookUrl: 'https://discord.com/api/webhooks/1/token' } });
    });
  });

  it('asks before deleting, then deletes', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    const row = (await screen.findByText('Home webhook')).closest('tr');

    if (row === null) {
      throw new Error('the Home webhook row was not found');
    }

    await user.click(within(row).getByRole('button', { name: 'Delete' }));

    const dialog = screen.getByRole('dialog');

    expect(within(dialog).getByText(/Wondarr stops sending to Home webhook/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(
        sent().some((request) => request.method === 'DELETE' && request.url.includes('/api/v1/notification/1')),
      ).toBe(true);
    });
  });

  it('enables a notification with the row switch', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByText('Discord alerts');
    await user.click(screen.getByLabelText('Enable Discord alerts'));

    await waitFor(async () => {
      const body = await lastRequest('PUT', '/api/v1/notification/2');

      expect(body).toMatchObject({
        name: 'Discord alerts',
        implementation: 'Discord',
        events: ['failure'],
        enabled: true,
      });
    });
  });
});
