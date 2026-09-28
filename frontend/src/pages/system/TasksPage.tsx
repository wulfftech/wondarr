import { Button, Card, Stack, Table, Text, Title } from '@mantine/core';
import { Play } from 'lucide-react';
import { useRunCommand, useTasks } from '../../api/hooks';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

function orDash(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' ? '—' : value;
}

function formatTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }

  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/** The scheduled tasks, with the interval, the runs and a way to run one now. */
export function TasksPage() {
  const tasks = useTasks();
  const runCommand = useRunCommand();

  return (
    <Stack gap="lg">
      <Title order={2}>Tasks</Title>

      {runCommand.error !== null && <ErrorState title="The task did not start" message={runCommand.error.message} />}

      {tasks.isLoading && <LoadingState />}
      {tasks.error !== null && <ErrorState message={tasks.error.message} />}
      {tasks.data !== undefined && tasks.data.length === 0 && <EmptyState message="No tasks are scheduled." />}
      {tasks.data !== undefined && tasks.data.length > 0 && (
        <Card withBorder padding="md">
          <Table data-testid="task-list">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Task</Table.Th>
                <Table.Th w={110}>Interval</Table.Th>
                <Table.Th w={190}>Last run</Table.Th>
                <Table.Th w={190}>Next run</Table.Th>
                <Table.Th w={140}>Last result</Table.Th>
                <Table.Th w={120} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tasks.data.map((task) => (
                <Table.Tr key={task.id}>
                  <Table.Td>{task.name}</Table.Td>
                  <Table.Td>{task.interval} min</Table.Td>
                  <Table.Td>{formatTimestamp(task.lastExecution)}</Table.Td>
                  <Table.Td>{formatTimestamp(task.nextExecution)}</Table.Td>
                  <Table.Td>
                    <Text c={task.lastResult === 'failed' ? 'red' : undefined}>{orDash(task.lastResult)}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="xs"
                      variant="light"
                      leftSection={<Play size={14} />}
                      loading={runCommand.isPending && runCommand.variables?.name === task.name}
                      onClick={() => runCommand.mutate({ name: task.name })}
                    >
                      Run now
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}
    </Stack>
  );
}
