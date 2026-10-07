import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/** The API host the dev server proxies to (see `docs/architecture/STACK.md`). */
const apiTarget = 'http://localhost:1077';

export default defineConfig({
  // Relative asset URLs so the build works under any URL base; index.html carries <base href>.
  base: './',
  plugins: [react()],
  build: {
    outDir: '../src/Wondarr.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      '/signalr': { target: apiTarget, changeOrigin: true, ws: true },
      '/initialize.json': { target: apiTarget, changeOrigin: true },
      '/login': { target: apiTarget, changeOrigin: true },
      '/logout': { target: apiTarget, changeOrigin: true },
      '/docs': { target: apiTarget, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    // The form tests type into many fields; on a loaded machine (a backend test run beside them) the
    // default 5 s timed out a correct test.
    testTimeout: 15000,
  },
});
