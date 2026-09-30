import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { createBrowserRouter, Navigate, RouterProvider, type RouteObject } from 'react-router';
import { ApiProvider } from '../api/ApiProvider';
import type { AppConfig } from '../api/bootstrap';
import { AppLayout } from '../components/AppLayout';
import { ActivityPage } from '../pages/ActivityPage';
import { AddSongsPage } from '../pages/AddSongsPage';
import { LibraryPage } from '../pages/LibraryPage';
import { MatchQueuePage } from '../pages/MatchQueuePage';
import { NotFoundPage } from '../pages/NotFoundPage';
import { SettingsPage } from '../pages/SettingsPage';
import { StatusPage } from '../pages/system/StatusPage';
import { TasksPage } from '../pages/system/TasksPage';
import { UnresolvedPage } from '../pages/UnresolvedPage';
import { WantedPage } from '../pages/WantedPage';
import { theme } from '../theme/theme';
import { useEventStream } from './signalr';

const ROUTES: RouteObject[] = [
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <Navigate to="/library" replace /> },
      { path: 'library', element: <LibraryPage /> },
      { path: 'add', element: <AddSongsPage /> },
      { path: 'add/unresolved', element: <UnresolvedPage /> },
      { path: 'match', element: <MatchQueuePage /> },
      { path: 'wanted', element: <WantedPage /> },
      { path: 'wanted/:tab', element: <WantedPage /> },
      { path: 'activity', element: <ActivityPage /> },
      { path: 'activity/:tab', element: <ActivityPage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'settings/:section', element: <SettingsPage /> },
      { path: 'system/status', element: <StatusPage /> },
      { path: 'system/tasks', element: <TasksPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
];

/**
 * The whole shell. Everything under the router reads its API client from `ApiProvider`, so no page
 * builds a URL of its own and the URL base keeps working.
 */
export function App({ config, env }: { config: AppConfig; env?: 'default' | 'test' }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            retry: false,
            refetchOnWindowFocus: false,
          },
        },
      }),
  );

  const router = useMemo(
    () => createBrowserRouter(ROUTES, { basename: config.urlBase === '' ? '/' : config.urlBase }),
    [config.urlBase],
  );

  useEventStream(config, queryClient);

  return (
    <MantineProvider theme={theme} defaultColorScheme="auto" env={env}>
      <Notifications />
      <ApiProvider config={config}>
        <QueryClientProvider client={queryClient}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </ApiProvider>
    </MantineProvider>
  );
}
