import { useMemo, type ReactNode } from 'react';
import type { AppConfig } from './bootstrap';
import { ApiContext, buildApiContext } from './context';

/** Provides the bootstrap configuration and the API client to the tree. */
export function ApiProvider({ config, children }: { config: AppConfig; children: ReactNode }) {
  const value = useMemo(() => buildApiContext(config), [config]);

  return <ApiContext value={value}>{children}</ApiContext>;
}
