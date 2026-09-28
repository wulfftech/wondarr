import '@testing-library/jest-dom/vitest';
import { vi } from 'vitest';

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
