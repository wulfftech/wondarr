import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../../api/ApiProvider';
import { FEEDBACK_QUERY_KEY } from '../../api/feedback';
import { createTestConfig, installFetch, jsonResponse } from '../../test/helpers';
import { theme } from '../../theme/theme';
import { CLASSIC_TOKEN_URL, FeedbackSettingsCard } from './FeedbackSettingsCard';

/** The Contribute with feedback card, with the server answered by a route table. */

afterEach(() => {
  vi.unstubAllGlobals();
});

const TOKEN = 'ghp_cardtesttoken0123456789';

interface Server {
  settings?: { tokenSet: boolean; tokenLocked: boolean };
  put?: (body: { token: string }) => Response;
  test?: { ok: boolean; message: string };
}

function renderCard({ settings = { tokenSet: false, tokenLocked: false }, put, test }: Server = {}) {
  let stored = settings;
  const bodies: unknown[] = [];

  const fetchMock = installFetch(async (url, init) => {
    const request = vi.mocked(fetch).mock.calls.at(-1)?.[0];
    const body: unknown =
      request instanceof Request && init?.method !== 'GET' ? await request.clone().json() : undefined;

    if (url.endsWith('/api/v1/feedback/settings/test')) {
      bodies.push({ test: body });

      return jsonResponse(test ?? { ok: true, message: 'GitHub accepts the token.' });
    }

    if (url.endsWith('/api/v1/feedback/settings') && init?.method === 'PUT') {
      bodies.push({ put: body });
      const token = (body as { token: string }).token;

      if (put !== undefined) {
        return put(body as { token: string });
      }

      stored = { ...stored, tokenSet: token !== '' };

      return jsonResponse(stored);
    }

    if (url.endsWith('/api/v1/feedback/settings')) {
      return jsonResponse(stored);
    }

    return jsonResponse({}, 404);
  });

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={theme} env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>
          <FeedbackSettingsCard />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );

  return { fetchMock, bodies, client };
}

function tokenField(): HTMLElement {
  return screen.getByLabelText('GitHub token');
}

describe('FeedbackSettingsCard', () => {
  it('explains how to make a classic token with public_repo, and links GitHub with it filled in', async () => {
    renderCard();

    await screen.findByLabelText('GitHub token');

    const link = screen.getByRole('link', { name: 'Generate new token (classic)' });

    expect(link).toHaveAttribute('href', CLASSIC_TOKEN_URL);
    expect(link).toHaveAttribute('target', '_blank');
    expect(CLASSIC_TOKEN_URL).toContain('scopes=public_repo');
    expect(screen.getAllByText('public_repo').length).toBeGreaterThan(0);
    expect(screen.getByText(/only on repositories you own or collaborate on/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save token' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('saves a pasted token, clears the field, and refreshes the header status', async () => {
    const user = userEvent.setup();
    const { bodies, client } = renderCard();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await user.type(await screen.findByLabelText('GitHub token'), `  ${TOKEN} `);
    await user.click(screen.getByRole('button', { name: 'Save token' }));

    expect(await screen.findByText(/The feedback button is in the header now/)).toBeInTheDocument();
    expect(bodies).toContainEqual({ put: { token: TOKEN } });
    expect(tokenField()).toHaveValue('');
    expect(tokenField()).toHaveAttribute('placeholder', 'Stored — paste to replace');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: FEEDBACK_QUERY_KEY });
    expect(screen.getByRole('button', { name: 'Remove' })).toBeInTheDocument();
  });

  it('tests the typed token, or the stored one when nothing is typed', async () => {
    const user = userEvent.setup();
    const { bodies } = renderCard({
      settings: { tokenSet: true, tokenLocked: false },
      test: { ok: false, message: 'GitHub accepts the token, but it has no public_repo scope.' },
    });

    await user.click(await screen.findByRole('button', { name: 'Test' }));

    expect(await screen.findByTestId('feedback-token-test-result')).toHaveTextContent('no public_repo scope');
    expect(bodies).toContainEqual({ test: { token: null } });

    await user.type(tokenField(), TOKEN);
    await user.click(screen.getByRole('button', { name: 'Test' }));

    await waitFor(() => expect(bodies).toContainEqual({ test: { token: TOKEN } }));
  });

  it('removes the stored token', async () => {
    const user = userEvent.setup();
    const { bodies } = renderCard({ settings: { tokenSet: true, tokenLocked: false } });

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    expect(await screen.findByText(/The feedback button is gone from the header/)).toBeInTheDocument();
    expect(bodies).toContainEqual({ put: { token: '' } });
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('shows the server’s refusal', async () => {
    const user = userEvent.setup();
    renderCard({
      put: () =>
        jsonResponse({ errors: { token: ['token has spaces or line breaks in it; paste the token alone'] } }, 400),
    });

    await user.type(await screen.findByLabelText('GitHub token'), 'Bearer x');
    await user.click(screen.getByRole('button', { name: 'Save token' }));

    expect(await screen.findByText('token has spaces or line breaks in it; paste the token alone')).toBeInTheDocument();
  });

  it('never lets the browser fill a saved password into the token field', async () => {
    renderCard();

    expect(await screen.findByLabelText('GitHub token')).toHaveAttribute('autocomplete', 'new-password');
  });

  it('drops a test result once the token is edited', async () => {
    const user = userEvent.setup();
    renderCard({ settings: { tokenSet: true, tokenLocked: false } });

    await user.click(await screen.findByRole('button', { name: 'Test' }));
    expect(await screen.findByTestId('feedback-token-test-result')).toBeInTheDocument();

    await user.type(tokenField(), 'g');

    expect(screen.queryByTestId('feedback-token-test-result')).not.toBeInTheDocument();
  });

  it('says what to do when the environment sets the token to nothing', async () => {
    renderCard({ settings: { tokenSet: false, tokenLocked: true } });

    expect(await screen.findByText(/is set but empty in Wondarr/)).toBeInTheDocument();
    expect(screen.getByLabelText('GitHub token')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Test' })).toBeDisabled();
    expect(screen.queryByText(/so it cannot be changed here/)).not.toBeInTheDocument();
  });

  it('is read-only when the environment sets the token, but can still test it', async () => {
    renderCard({ settings: { tokenSet: true, tokenLocked: true } });

    expect(await screen.findByLabelText('GitHub token')).toBeDisabled();
    expect(screen.getByText(/so it cannot be changed here/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save token' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test' })).toBeEnabled();
  });
});
