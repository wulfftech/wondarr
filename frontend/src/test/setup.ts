import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi } from 'vitest';

/**
 * Mantine-heavy pages (the Library page with its modals and per-row buttons) can take longer than
 * Testing Library's default 1 s to settle when the machine is busy; a longer wait only matters for a
 * failing test and removes a load-dependent flake.
 */
configure({ asyncUtilTimeout: 3000 });

/**
 * jsdom has no `matchMedia`, and Mantine's `auto` colour scheme reads it on every render.
 * Reporting "no dark preference" makes the resolved scheme deterministic: light.
 */
window.matchMedia = (query: string): MediaQueryList => ({
  matches: false,
  media: query,
  onchange: null,
  addListener: () => undefined,
  removeListener: () => undefined,
  addEventListener: () => undefined,
  removeEventListener: () => undefined,
  dispatchEvent: () => false,
});

/**
 * jsdom has no `ResizeObserver` either; Mantine's SegmentedControl, Tabs indicator and ScrollArea
 * construct one on mount. Nothing is ever resized in a test, so an observer that never fires is
 * exactly right.
 */
class NoopResizeObserver implements ResizeObserver {
  observe(): void {}

  unobserve(): void {}

  disconnect(): void {}
}

window.ResizeObserver = NoopResizeObserver;

/**
 * The tests must never open a socket. The real builder would try to reach a hub that no test host
 * runs; the shell only needs the connection object to exist.
 */
vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl(): this {
      return this;
    }

    withAutomaticReconnect(): this {
      return this;
    }

    build() {
      return {
        on: () => undefined,
        start: () => Promise.resolve(),
        stop: () => Promise.resolve(),
      };
    }
  }

  return { HubConnectionBuilder };
});
