import { Button, Card, Stack, Table, Text, Title } from '@mantine/core';
import { Play } from 'lucide-react';
import { useRunCommand, useTasks } from '../../api/hooks';
import { readEnum } from '../../api/profiles';
import type { CommandStatusName } from '../../api/songs';
import { useCommands, type CommandResource } from '../../api/system';
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

/** How often a task runs, the way the *arrs spell it: minutes under an hour, hours under a day. */
function formatInterval(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }

  if (minutes < 1440) {
    return `${minutes / 60} h`;
  }

  const days = minutes / 1440;

  return `${days} ${days === 1 ? 'day' : 'days'}`;
}

/** How long the last run took; `00:00:00` is what the API reports for a task that never has. */
function formatDuration(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' || value === '00:00:00' ? '—' : value;
}

/** The colour a command's life-cycle state wears, so a failure stands out. */
function statusColour(status: CommandResource['status']): string | undefined {
  // The API sends the status as a camelCase string although the document types it as a number.
  const name = readEnum<CommandStatusName>(status);

  if (name === 'failed' || name === 'aborted') {
    return 'red';
  }

  return name === 'queued' || name === 'started' ? 'blue' : undefined;
}

/** The scheduled tasks, with the interval, the runs and a way to run one now. */
export function TasksPage() {
  const tasks = useTasks();
  const runCommand = useRunCommand();
  const commands = useCommands();

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
                <Table.Th w={90}>Interval</Table.Th>
                <Table.Th w={190}>Last run</Table.Th>
                <Table.Th w={110}>Last duration</Table.Th>
                <Table.Th w={190}>Next run</Table.Th>
                <Table.Th w={140}>Last result</Table.Th>
                <Table.Th w={110} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tasks.data.map((task) => (
                <Table.Tr key={task.id}>
                  <Table.Td>{task.name}</Table.Td>
                  <Table.Td>{formatInterval(Number(task.interval))}</Table.Td>
                  <Table.Td>{formatTimestamp(task.lastExecution)}</Table.Td>
                  <Table.Td>{formatDuration(task.lastDuration)}</Table.Td>
                  <Table.Td>{formatTimestamp(task.nextExecution)}</Table.Td>
                  <Table.Td>
                    <Text c={task.lastResult?.startsWith('unsuccessful') === true ? 'red' : undefined}>
                      {orDash(task.lastResult)}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="xs"
                      variant="light"
                      leftSection={<Play size={14} />}
                      loading={runCommand.isPending && runCommand.variables?.name === task.taskName}
                      onClick={() => runCommand.mutate({ name: task.taskName })}
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

      <Title order={3}>Recent commands</Title>

      {commands.isLoading && <LoadingState />}
      {commands.error !== null && <ErrorState message={commands.error.message} />}
      {commands.data !== undefined && commands.data.length === 0 && <EmptyState message="No command has run yet." />}
      {commands.data !== undefined && commands.data.length > 0 && (
        <Card withBorder padding="md">
          <Table data-testid="command-list">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Command</Table.Th>
                <Table.Th w={110}>Status</Table.Th>
                <Table.Th w={190}>Queued</Table.Th>
                <Table.Th w={190}>Ended</Table.Th>
                <Table.Th w={110}>Duration</Table.Th>
                <Table.Th>Message</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {commands.data.slice(0, 20).map((command) => (
                <Table.Tr key={command.id}>
                  <Table.Td>{command.commandName}</Table.Td>
                  <Table.Td>
                    <Text c={statusColour(command.status)}>{command.status}</Text>
                  </Table.Td>
                  <Table.Td>{formatTimestamp(command.queued)}</Table.Td>
                  <Table.Td>{formatTimestamp(command.ended)}</Table.Td>
                  <Table.Td>{orDash(command.duration)}</Table.Td>
                  <Table.Td>{orDash(command.message)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}
    </Stack>
  );
}
