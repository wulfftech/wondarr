import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HEALTH_ENTRIES, SYSTEM_STATUS, TASKS } from '../../test/fixtures';
import { installFetch, jsonResponse, renderApp, resetLocation } from '../../test/helpers';

beforeEach(() => {
  resetLocation('/system/tasks');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** A task whose display name differs from its command name, the way every multi-word task's does. */
const MISSING_SEARCH = {
  ...TASKS[0],
  id: 2,
  name: 'Missing Search',
  taskName: 'MissingSearch',
  interval: 360,
  lastDuration: '00:02:05',
};

const COMMANDS = [
  {
    id: 7,
    name: 'MissingSearch',
    commandName: 'Missing Search',
    status: 'completed',
    trigger: 'manual',
    queued: '2026-01-01T00:00:00Z',
    started: '2026-01-01T00:00:01Z',
    ended: '2026-01-01T00:02:06Z',
    duration: '00:02:05',
    message: '20 songs: 18 grabbed',
    body: null,
    result: 'successful',
  },
];

/** What the mocked `fetch` received; the body lives on the `Request` the generated client passes. */
function sent(): { url: string; method: string; body: Promise<string> }[] {
  return vi.mocked(globalThis.fetch).mock.calls.map(([input, init]) => {
    const request = input instanceof Request ? input : new Request(input, init);

    return { url: request.url, method: request.method, body: request.clone().text() };
  });
}

function install(): void {
  installFetch((url, init) => {
    if (url.includes('/api/v1/system/status')) {
      return jsonResponse(SYSTEM_STATUS);
    }

    if (url.includes('/api/v1/health')) {
      return jsonResponse(HEALTH_ENTRIES);
    }

    if (url.includes('/api/v1/system/task')) {
      return jsonResponse([...TASKS, MISSING_SEARCH]);
    }

    if (url.includes('/api/v1/command')) {
      return init?.method === 'POST' ? jsonResponse({ id: 8 }, 201) : jsonResponse(COMMANDS);
    }

    return new Response('not found', { status: 404 });
  });
}

describe('TasksPage', () => {
  it('lists the scheduled tasks with their interval, last duration and next run', async () => {
    install();

    renderApp();

    expect(await screen.findByText('Heartbeat')).toBeInTheDocument();
    expect(screen.getByText('1 h')).toBeInTheDocument();
    expect(screen.getByText('6 h')).toBeInTheDocument();
    expect(screen.getByText('00:00:01')).toBeInTheDocument();
    expect(screen.getAllByText('00:02:05').length).toBeGreaterThan(0);
    expect(screen.getAllByText('successful').length).toBeGreaterThan(0);
  });

  it('keeps the Task column as narrow as its longest name', async () => {
    install();

    renderApp();

    const tasks = within(await screen.findByTestId('task-list'));

    expect(tasks.getByText('Missing Search')).toHaveStyle({ whiteSpace: 'nowrap' });
    expect(screen.getByRole('columnheader', { name: 'Task' })).toHaveStyle({ whiteSpace: 'nowrap' });
    expect(screen.getByRole('columnheader', { name: 'Command' })).toHaveStyle({ whiteSpace: 'nowrap' });
  });

  it('runs a task by its command name, not its display name', async () => {
    install();
    const user = userEvent.setup();

    renderApp();

    await screen.findAllByText('Missing Search');
    const buttons = screen.getAllByRole('button', { name: 'Run now' });
    // The second row is Missing Search; its display name has a space the command name does not.
    await user.click(buttons[1]);

    await waitFor(async () => {
      const post = sent().find((request) => request.method === 'POST' && request.url.includes('/api/v1/command'));

      expect(post).toBeDefined();
      expect(JSON.parse((await post?.body) ?? '{}')).toEqual({ name: 'MissingSearch' });
    });
  });

  it('shows the recent commands', async () => {
    install();

    renderApp();

    expect(await screen.findByText('20 songs: 18 grabbed')).toBeInTheDocument();
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

    expect((await screen.findAllByText(/failed/)).length).toBeGreaterThan(0);
  });
});
