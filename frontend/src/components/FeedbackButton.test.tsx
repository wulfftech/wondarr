import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiProvider } from '../api/ApiProvider';
import { FEEDBACK_QUERY_KEY, useFeedbackStatus } from '../api/feedback';
import { createTestConfig, installFetch, jsonResponse, type FetchMock } from '../test/helpers';
import { theme } from '../theme/theme';
import { FeedbackButton } from './FeedbackButton';

/** The header's feedback button and its dialog, with the server answered by a route table. */

afterEach(() => {
  vi.unstubAllGlobals();
});

const STATUS = {
  tokenConfigured: true,
  autoRaise: true,
  repository: 'wulfftech/wondarr',
  repositoryUrl: 'https://github.com/wulfftech/wondarr',
  maxTextLength: 4000,
};

interface Answers {
  status?: typeof STATUS;
  result?: unknown;
  resultStatus?: number;
  /** Answers the POST instead of `result`, for a test that holds the answer back. */
  post?: () => Promise<Response>;
}

/**
 * Renders what the status query holds once it has an answer. It shares the button's query, so both render
 * the same answer in the same commit: waiting for it means the button has seen the status too.
 */
function StatusProbe() {
  const status = useFeedbackStatus();

  return status.data === undefined ? null : (
    <span data-testid="status-answered">{String(status.data.tokenConfigured)}</span>
  );
}

function renderButton({ status = STATUS, result, resultStatus = 200, post }: Answers = {}): FetchMock & {
  client: QueryClient;
} {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const fetchMock = installFetch((url, init) => {
    if (url.endsWith('/api/v1/feedback') && init?.method === 'POST') {
      return post === undefined ? jsonResponse(result, resultStatus) : post();
    }

    if (url.endsWith('/api/v1/feedback')) {
      return jsonResponse(status);
    }

    return jsonResponse({}, 404);
  });

  render(
    <MantineProvider theme={theme} env="test">
      <ApiProvider config={createTestConfig()}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/wanted?page=2']}>
            <StatusProbe />
            <FeedbackButton />
            <main>
              <h2>Wanted</h2>
            </main>
          </MemoryRouter>
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>,
  );

  return Object.assign(fetchMock, { client });
}

async function sentBody(fetchMock: FetchMock): Promise<unknown> {
  const call = fetchMock.calls.find((entry) => entry.init?.method === 'POST');
  const request = (vi.mocked(fetch).mock.calls[fetchMock.calls.indexOf(call!)] as [Request])[0];

  return (await request.clone().json()) as unknown;
}

describe('FeedbackButton', () => {
  it('is not shown until a token is set', async () => {
    renderButton({ status: { ...STATUS, tokenConfigured: false, autoRaise: false } });

    expect(await screen.findByTestId('status-answered')).toHaveTextContent('false');
    expect(screen.queryByRole('button', { name: 'Send feedback' })).not.toBeInTheDocument();
  });

  it('keeps an open dialog and its text when the token is removed meanwhile, and goes once it is closed', async () => {
    const user = userEvent.setup();
    const { client } = renderButton();

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await user.type(await screen.findByRole('textbox', { name: 'What went wrong?' }), 'Half a sentence');

    act(() => {
      client.setQueryData(FEEDBACK_QUERY_KEY, { ...STATUS, tokenConfigured: false, autoRaise: false });
    });

    // React Query tells its observers on a later tick: wait until the probe, and so the button, has the change.
    await waitFor(() => expect(screen.getByTestId('status-answered')).toHaveTextContent('false'));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'What went wrong?' })).toHaveValue('Half a sentence');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Send feedback' })).not.toBeInTheDocument();
  });

  it('sends the text with the page it was opened on, and links the new issue', async () => {
    const user = userEvent.setup();
    const fetchMock = renderButton({
      result: {
        raised: true,
        issueUrl: 'https://github.com/wulfftech/wondarr/issues/12',
        issueNumber: 12,
        reason: null,
      },
    });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));

    expect(await screen.findByText(/becomes a public issue on wulfftech\/wondarr/)).toBeInTheDocument();
    expect(screen.getByText('/wanted?page=2')).toBeInTheDocument();
    expect(screen.getByText(/\(Wanted\), and Wondarr 0\.1\.0-test\./)).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Idea' }));
    await user.type(screen.getByRole('textbox', { name: 'What would you like?' }), '  Show the bitrate  ');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    const link = await screen.findByRole('link', { name: 'issue #12' });

    expect(link).toHaveAttribute('href', 'https://github.com/wulfftech/wondarr/issues/12');
    expect(link).toHaveAttribute('target', '_blank');
    // The send button is gone; focus lands on the dialog's own Close, not on the page behind it.
    expect(document.activeElement).toHaveTextContent('Close');
    expect(await sentBody(fetchMock)).toEqual({
      kind: 'idea',
      text: 'Show the bitrate',
      page: '/wanted?page=2',
      pageTitle: 'Wanted',
    });
  });

  it('cannot send a blank text', async () => {
    const user = userEvent.setup();
    const fetchMock = renderButton();

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await screen.findByText(/becomes a public issue/);

    const send = screen.getByRole('button', { name: 'Send' });

    expect(send).toBeDisabled();

    await user.type(screen.getByRole('textbox', { name: 'What went wrong?' }), '   ');

    expect(send).toBeDisabled();
    expect(fetchMock.calls.some((call) => call.init?.method === 'POST')).toBe(false);
  });

  it('without a token hands over to the prefilled GitHub form, with the reason when a token failed', async () => {
    const user = userEvent.setup();
    const form = 'https://github.com/wulfftech/wondarr/issues/new?title=Broken&labels=bug&body=Broken';
    renderButton({
      status: { ...STATUS, autoRaise: false },
      result: { raised: false, issueUrl: form, issueNumber: null, reason: 'GitHub refused the token (401).' },
    });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    expect(await screen.findByText(/You finish it on wulfftech\/wondarr/)).toBeInTheDocument();

    await user.type(screen.getByRole('textbox', { name: 'What went wrong?' }), 'Broken');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    const finish = await screen.findByRole('link', { name: /Finish on GitHub/ });

    expect(finish).toHaveAttribute('href', form);
    expect(finish).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.getByText(/GitHub refused the token \(401\)/)).toBeInTheDocument();

    await user.click(finish);

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('sends with Ctrl+Enter', async () => {
    const user = userEvent.setup();
    const fetchMock = renderButton({
      result: { raised: true, issueUrl: 'https://github.com/wulfftech/wondarr/issues/3', issueNumber: 3, reason: null },
    });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await screen.findByText(/becomes a public issue/);
    await user.type(
      screen.getByRole('textbox', { name: 'What went wrong?' }),
      'Line one{Enter}line two{Control>}{Enter}{/Control}',
    );

    expect(await screen.findByRole('link', { name: 'issue #3' })).toBeInTheDocument();
    expect(await sentBody(fetchMock)).toMatchObject({ kind: 'bug', text: 'Line one\nline two' });
  });

  it('shows the server’s refusal and keeps the text', async () => {
    const user = userEvent.setup();
    renderButton({
      result: { errors: { text: ['Keep it to 4000 characters.'] } },
      resultStatus: 400,
    });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await screen.findByText(/becomes a public issue/);
    await user.type(screen.getByRole('textbox', { name: 'What went wrong?' }), 'Too long');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText('Keep it to 4000 characters.')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'What went wrong?' })).toHaveValue('Too long');
  });

  it('starts blank each time it opens', async () => {
    const user = userEvent.setup();
    renderButton();

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await user.type(await screen.findByRole('textbox', { name: 'What went wrong?' }), 'Draft');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));

    expect(await screen.findByRole('textbox', { name: 'What went wrong?' })).toHaveValue('');
  });

  it('stays open while sending, so the answer is never lost', async () => {
    const user = userEvent.setup();
    let answer: (response: Response) => void = () => undefined;
    renderButton({ post: () => new Promise<Response>((resolve) => (answer = resolve)) });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await screen.findByText(/becomes a public issue/);
    await user.type(screen.getByRole('textbox', { name: 'What went wrong?' }), 'Broken');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled());
    await user.keyboard('{Escape}');

    expect(screen.getByRole('dialog')).toBeInTheDocument();

    answer(
      jsonResponse({
        raised: true,
        issueUrl: 'https://github.com/wulfftech/wondarr/issues/5',
        issueNumber: 5,
        reason: null,
        mayBeRaised: false,
        issueListUrl: 'https://github.com/wulfftech/wondarr/issues',
      }),
    );

    expect(await screen.findByRole('link', { name: 'issue #5' })).toBeInTheDocument();

    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('warns that the issue may exist when GitHub took the request but never answered', async () => {
    const user = userEvent.setup();
    renderButton({
      result: {
        raised: false,
        issueUrl: 'https://github.com/wulfftech/wondarr/issues/new?title=Broken',
        issueNumber: null,
        reason: 'GitHub did not answer within 15 seconds.',
        mayBeRaised: true,
        issueListUrl: 'https://github.com/wulfftech/wondarr/issues',
      },
    });

    await user.click(await screen.findByRole('button', { name: 'Send feedback' }));
    await screen.findByText(/becomes a public issue/);
    await user.type(screen.getByRole('textbox', { name: 'What went wrong?' }), 'Broken');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText(/GitHub may have opened it anyway/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'issue list' })).toHaveAttribute(
      'href',
      'https://github.com/wulfftech/wondarr/issues',
    );
    expect(document.activeElement).toHaveTextContent('Finish on GitHub');
  });
});
