import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS, TASKS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation, type FetchMock } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/system/tasks');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function install(mutate: (mock: FetchMock, taskName: string) => void): FetchMock {
  const mock = installFetch((url) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/system/task')) {
      return jsonResponse(TASKS);
    }

    if (url.includes('/api/v1/command')) {
      return new Response(null, { status: 201 });
    }

    return new Response('not found', { status: 404 });
  });

  mutate(mock, 'Heartbeat');

  return mock;
}

describe('TasksPage', () => {
  it('lists the scheduled tasks with their next run', async () => {
    install(() => undefined);

    renderApp();

    expect(await screen.findByText('Heartbeat')).toBeInTheDocument();
    expect(screen.getByText('60 min')).toBeInTheDocument();
    expect(screen.getByText('successful')).toBeInTheDocument();
  });

  it('posts the task name when "Run now" is pressed', async () => {
    const mock = install(() => undefined);
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Run now' }));

    await waitFor(() => {
      const post = mock.calls.find((call) => call.init?.method === 'POST');

      expect(post).toBeDefined();
      expect(post?.url).toContain('/api/v1/command');
      expect(post?.init?.body).toBe(JSON.stringify({ name: 'Heartbeat' }));
    });
  });

  it('sends the API key with the command', async () => {
    const mock = install(() => undefined);
    const user = userEvent.setup();

    renderApp();

    await user.click(await screen.findByRole('button', { name: 'Run now' }));

    await waitFor(() => {
      const post = mock.calls.find((call) => call.init?.method === 'POST');
      const headers = new Headers(post?.init?.headers);

      expect(headers.get('X-Api-Key')).toBe('test-api-key');
    });
  });

  it('shows an error state when the task list fails', async () => {
    installFetch((url) => {
      if (url.includes('/api/v1/system/status')) {
        return jsonResponse(SYSTEM_STATUS);
      }

      if (url.includes('/api/v1/health')) {
        return jsonResponse(HEALTH_ENTRIES);
      }

      return jsonResponse({ title: 'Server error' }, 500);
    });

    renderApp();

    expect(await screen.findByText('The request to api/v1/system/task failed.')).toBeInTheDocument();
  });
});
