import { screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, QUALITY_DEFINITIONS, QUALITY_PROFILES, SYSTEM_STATUS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/settings/profiles');
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

function install(write: () => Response): FetchMock {
  return installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/qualitydefinition')) {
      return jsonResponse(QUALITY_DEFINITIONS);
    }

    // The item endpoint takes the writes — the PUT that saves and the DELETE that removes.
    if (/\/api\/v1\/qualityprofile\/\d+$/.test(url)) {
      return write();
    }

    if (url.includes('/api/v1/qualityprofile')) {
      return jsonResponse(QUALITY_PROFILES);
    }

    return new Response('not found', { status: 404 });
  });
}

/** Opens the editor for the seeded profile and hands back its dialog. */
async function openEditor(user: UserEvent): Promise<HTMLElement> {
  renderApp();

  await user.click(await screen.findByRole('button', { name: 'Edit' }));

  // The editor is a Mantine modal in a portal, and it is mounted a transition after the click.
  return screen.findByRole('dialog');
}

describe('QualityProfilesPage', () => {
  it('names the profile cutoff and lists its allowed groups', async () => {
    install(() => jsonResponse(QUALITY_PROFILES[0]));

    renderApp();

    expect(await screen.findByText('Standard 320')).toBeInTheDocument();
    expect(screen.getByText('Cutoff: MP3-256')).toBeInTheDocument();
    expect(screen.getByText('Upgrades on')).toBeInTheDocument();
    expect(screen.getByText('Lossless')).toBeInTheDocument();
  });

  it('shows the qualities best first in the editor', async () => {
    install(() => jsonResponse(QUALITY_PROFILES[0]));
    const user = userEvent.setup();

    // The API orders items worst → best; the form shows the same groups reversed. Scoped to the
    // editor, because the profile's card behind it shows the allowed groups as badges too.
    const dialog = await openEditor(user);
    const labels = within(dialog).getAllByText(/^(Lossless|Mid lossy|Trash lossy)$/);

    expect(labels.map((label) => label.textContent)).toEqual(['Lossless', 'Mid lossy', 'Trash lossy']);
  });

  it('drops the cutoff quality from the options when its group is unticked', async () => {
    install(() => jsonResponse(QUALITY_PROFILES[0]));
    const user = userEvent.setup();

    const dialog = await openEditor(user);

    await user.click(await within(dialog).findByLabelText('Mid lossy allowed'));

    const listbox = await openSelect(user, 'Cutoff');

    expect(within(listbox).getByText('FLAC')).toBeInTheDocument();
    expect(within(listbox).queryByText('MP3-256')).toBeNull();
  });

  it('saves the whole profile with the duration tolerance in milliseconds', async () => {
    install(() => jsonResponse(QUALITY_PROFILES[0]));
    const user = userEvent.setup();

    const dialog = await openEditor(user);

    const tolerance = await within(dialog).findByLabelText('Duration tolerance');

    await user.clear(tolerance);
    await user.type(tolerance, '3');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(async () => {
      const put = sent().find((request) => request.method === 'PUT');

      expect(put?.url).toContain('/api/v1/qualityprofile/1');

      const body: unknown = put === undefined ? null : JSON.parse(await put.body);

      expect(body).toMatchObject({ durationToleranceMs: 3000, name: 'Standard 320' });
    });
  });

  it('shows a server validation message under the field it names', async () => {
    install(() =>
      jsonResponse(
        {
          title: 'One or more validation errors occurred.',
          errors: { cutoff: ['The cutoff quality is not allowed.'] },
        },
        400,
      ),
    );
    const user = userEvent.setup();

    const dialog = await openEditor(user);

    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('The cutoff quality is not allowed.')).toBeInTheDocument();
  });

  it('shows the conflict when a profile is still in use', async () => {
    install(() => jsonResponse({ title: 'Quality profile in use', detail: 'A song still uses this profile.' }, 409));
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    // The card's Delete opens the modal; the modal's own Delete is the one that sends the request.
    const dialog = await screen.findByRole('dialog');

    await user.click(await within(dialog).findByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/still uses this profile/i)).toBeInTheDocument();
  });
});
