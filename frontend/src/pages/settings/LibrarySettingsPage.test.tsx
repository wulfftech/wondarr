import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, LIBRARIES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/library');
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * Opens a Mantine Select by the label on its input and hands back the listbox it is showing. jsdom
 * reports the dropdown as `display: none`, so its options are read by text rather than by role.
 */
async function openSelect(user: UserEvent, label: string): Promise<HTMLElement> {
  // The label names both the input and the listbox, so the input is picked out by its tag.
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

/**
 * What the mocked `fetch` actually received. The generated client calls `fetch(request)`, so the
 * method and the body live on that `Request`, not on the second argument the shared helper records.
 */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

function install(
  preview: () => Response = () => jsonResponse({ path: 'Music/Wondarr - Get Lucky.m4a', errors: [] }),
): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/preview')) {
      return preview();
    }

    if (url.includes('/api/v1/library')) {
      // The collection answers with every library; the item endpoint with the one that was saved.
      return url.includes('/api/v1/library/') ? jsonResponse(LIBRARIES[0]) : jsonResponse(LIBRARIES);
    }

    return new Response('not found', { status: 404 });
  });
}

describe('LibrarySettingsPage', () => {
  it('shows the default library with the minimum album size for the fewest-albums policy', async () => {
    install();

    renderApp();

    expect(await screen.findByDisplayValue('Music')).toBeInTheDocument();
    expect(screen.getByDisplayValue('/data/music')).toBeInTheDocument();
    expect(screen.getByLabelText('Minimum tracks per real album')).toBeInTheDocument();
  });

  it('saves the album policy as the camelCase string the API reads', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('Music');
    await pickOption(user, 'Album policy', 'Singles only');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const put = sent().find((request) => request.method === 'PUT');

      expect(put?.url).toContain('/api/v1/library/1');

      const body: unknown = put === undefined ? null : JSON.parse(await put.body);

      expect(body).toMatchObject({ albumPolicy: 'singlesOnly', layout: 'plexamp' });
    });
  });

  it('hides the minimum album size for every other policy', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('Music');
    await pickOption(user, 'Album policy', 'Singles only');

    expect(screen.queryByLabelText('Minimum tracks per real album')).toBeNull();
  });
});

describe('LibrarySettingsPage naming template', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('previews the rendered path once typing settles', async () => {
    install();

    renderApp();

    await screen.findByDisplayValue('Music');

    const input = screen.getByLabelText('Naming template');

    vi.useFakeTimers();
    fireEvent.change(input, { target: { value: '{Artist Name}/{Track Title}' } });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(400);
    });

    expect(screen.getByText(/Preview: Music\/Wondarr - Get Lucky\.m4a/)).toBeInTheDocument();

    // The mount already previewed the stored template, so the typing one is the last call.
    const posts = sent().filter((request) => request.url.includes('/preview'));
    const post = posts[posts.length - 1];

    expect(post?.method).toBe('POST');
    expect(post?.url).toContain('/api/v1/library/1/preview');
    expect(JSON.parse((await post?.body) ?? '{}')).toEqual({
      songId: null,
      template: '{Artist Name}/{Track Title}',
    });
  });

  it('shows the error instead of the path for a template that cannot be rendered', async () => {
    install(() => jsonResponse({ path: null, errors: ['Unknown token {Nope}.'] }));

    renderApp();

    await screen.findByDisplayValue('Music');

    vi.useFakeTimers();
    fireEvent.change(screen.getByLabelText('Naming template'), { target: { value: '{Nope}' } });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(400);
    });

    expect(screen.getByText('Unknown token {Nope}.')).toBeInTheDocument();
    expect(screen.queryByText(/Preview:/)).toBeNull();
  });

  it('inserts a token from the helper at the cursor', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('Music');

    const input = screen.getByLabelText<HTMLInputElement>('Naming template');

    input.setSelectionRange(0, 0);
    await user.click(screen.getByRole('button', { name: 'Tokens' }));
    await user.click(await screen.findByText('{Release Year}'));

    expect(input).toHaveValue('{Release Year}{Album Artist Name}/{Album Title}/{track:00} - {Track Title}');
  });

  it('resets the template to the selected layout preset', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findByDisplayValue('Music');

    await user.click(screen.getByRole('button', { name: 'Reset to the Plexamp preset' }));

    expect(screen.getByLabelText('Naming template')).toHaveValue(
      '{Album Artist Name}/{Album Title}/{medium:0}{track:00} - {Track Title}',
    );
  });
});
