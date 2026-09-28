import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { loadAppConfig } from '../api/bootstrap';
import { App } from './App';

/**
 * Boots the shell: read `initialize.json` (which carries the URL base and the API key), then render.
 * A failure before that point leaves a plain message in the document instead of a blank page.
 */
async function start(): Promise<void> {
  const container = document.getElementById('root');

  if (container === null) {
    throw new Error('#root is missing from index.html');
  }

  try {
    const config = await loadAppConfig();

    createRoot(container).render(
      <StrictMode>
        <App config={config} />
      </StrictMode>,
    );
  } catch (error) {
    container.textContent = error instanceof Error ? error.message : 'The web UI could not start.';
  }
}

void start();
