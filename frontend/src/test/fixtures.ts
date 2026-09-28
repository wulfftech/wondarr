import type { HealthEntry, TaskResource } from '../api/types';

/** Canned API payloads. They mirror what the backend emits; no test reaches the network. */

export const SYSTEM_STATUS = {
  appName: 'Compilarr',
  instanceName: 'Compilarr',
  version: '0.1.0-test',
  buildTime: '2026-01-01T00:00:00Z',
  isDebug: false,
  isProduction: true,
  isAdmin: false,
  isUserInteractive: false,
  startupPath: '/app',
  appData: '/config',
  osName: 'Linux',
  osVersion: '6.1.0',
  isNetCore: true,
  isLinux: true,
  isOsx: false,
  isWindows: false,
  isDocker: true,
  isContainerized: true,
  mode: 'console',
  branch: 'develop',
  authentication: 'none',
  databaseType: 'sqLite',
  databaseVersion: '3.46.0',
  migrationVersion: 4,
  urlBase: '',
  runtimeVersion: '10.0.0',
  runtimeName: 'netcore',
  startTime: '2026-01-01T00:00:00Z',
  packageVersion: '0.1.0-test',
  packageAuthor: 'Compilarr contributors',
  packageUpdateMechanism: 'docker',
} as const;

export const HEALTH_ENTRIES: HealthEntry[] = [
  { source: 'Database', type: 'ok', message: 'Database is up.', wikiUrl: null },
  { source: 'Config', type: 'warning', message: 'No download client is configured.', wikiUrl: null },
];

export const TASKS: TaskResource[] = [
  {
    id: 1,
    name: 'Heartbeat',
    interval: 60,
    lastExecution: '2026-01-01T00:00:00Z',
    lastStartTime: '2026-01-01T00:00:00Z',
    lastDuration: '00:00:01',
    nextExecution: '2026-01-01T01:00:00Z',
    lastResult: 'successful',
  },
];
