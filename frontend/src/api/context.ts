import { createContext, use } from 'react';
import type { AppConfig } from './bootstrap';
import { createApiClient, type ApiClient } from './client';

export interface ApiContextValue {
  config: AppConfig;
  client: ApiClient;
}

/** Holds the bootstrap configuration and the API client for the whole tree. */
export const ApiContext = createContext<ApiContextValue | null>(null);

export function buildApiContext(config: AppConfig): ApiContextValue {
  return { config, client: createApiClient(config) };
}

function useApiContext(): ApiContextValue {
  const value = use(ApiContext);

  if (value === null) {
    throw new Error('The API context is missing; wrap the tree in <ApiProvider>.');
  }

  return value;
}

/** The bootstrap configuration. */
export function useAppConfig(): AppConfig {
  return useApiContext().config;
}

/** The openapi-fetch client, with the API key middleware already installed. */
export function useApiClient(): ApiClient {
  return useApiContext().client;
}
