import { Alert, Center, Loader, Stack, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import type { ReactNode } from 'react';

/** The loading, empty and error states every data page shares. */

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <Center py="xl">
      <Stack align="center" gap="xs">
        <Loader size="sm" />
        <Text c="dimmed" size="sm">
          {label}
        </Text>
      </Stack>
    </Center>
  );
}

export function ErrorState({ title = 'Something went wrong', message }: { title?: string; message?: string }) {
  return (
    <Alert color="red" icon={<CircleAlert size={16} />} title={title}>
      {message ?? 'The request failed. Check the log in System → Status.'}
    </Alert>
  );
}

export function EmptyState({ message }: { message: string }) {
  return (
    <Center py="xl">
      <Text c="dimmed" size="sm">
        {message}
      </Text>
    </Center>
  );
}

/**
 * Picks the state to render for a query result, so pages read as one line.
 *
 * `timedOut` is React Query's `isPending` companion: a request that never settled still renders as
 * loading until it does.
 */
export function QueryState({
  isLoading,
  error,
  isEmpty,
  emptyMessage,
  errorMessage,
  children,
}: {
  isLoading: boolean;
  error: Error | null;
  isEmpty?: boolean;
  emptyMessage?: string;
  errorMessage?: string;
  children: ReactNode;
}) {
  if (isLoading) {
    return <LoadingState />;
  }

  if (error !== null) {
    return <ErrorState message={errorMessage} />;
  }

  if (isEmpty === true) {
    return <EmptyState message={emptyMessage ?? 'Nothing here yet.'} />;
  }

  return children;
}
