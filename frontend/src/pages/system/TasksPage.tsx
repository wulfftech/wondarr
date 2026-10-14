import { Button, Card, Stack, Table, Text, Title } from '@mantine/core';
import { Play } from 'lucide-react';
import { useCancelCommand, useRunCommand, useTasks } from '../../api/hooks';
import { useCommands, type CommandResource } from '../../api/system';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

/** A column that is exactly as wide as its longest value; the columns without a width share the rest. */
const NOWRAP = { whiteSpace: 'nowrap' } as const;

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

/** A command's state as the Status column spells it: `cancelled` reads "Cancelled". */
function statusLabel(status: CommandResource['status']): string {
  return status.charAt(0).toUpperCase() + status.slice(1);
}

/** The colour a command's life-cycle state wears, so a failure stands out. */
function statusColour(status: CommandResource['status']): string | undefined {
  if (status === 'failed' || status === 'aborted' || status === 'orphaned') {
    return 'red';
  }

  if (status === 'cancelled') {
    return 'dimmed';
  }

  return status === 'queued' || status === 'started' ? 'blue' : undefined;
}

/**
 * Whether the list offers a Cancel button: a command that is still queued, and a running search (the one
 * running command the server lets be cancelled, because it only waits for a download slot).
 */
function canCancel(command: CommandResource): boolean {
  const status = command.status;

  return status === 'queued' || (status === 'started' && command.name === 'SongSearch');
}

/** The scheduled tasks, with the interval, the runs and a way to run one now. */
export function TasksPage() {
  const tasks = useTasks();
  const runCommand = useRunCommand();
  const cancelCommand = useCancelCommand();
  const commands = useCommands();

  return (
    <Stack gap="lg">
      <Title order={2}>Tasks</Title>

      {runCommand.error !== null && <ErrorState title="The task did not start" message={runCommand.error.message} />}
      {cancelCommand.error !== null && (
        <ErrorState title="The command was not cancelled" message={cancelCommand.error.message} />
      )}

      {tasks.isLoading && <LoadingState />}
      {tasks.error !== null && <ErrorState message={tasks.error.message} />}
      {tasks.data !== undefined && tasks.data.length === 0 && <EmptyState message="No tasks are scheduled." />}
      {tasks.data !== undefined && tasks.data.length > 0 && (
        <Card withBorder padding="md">
          <Table data-testid="task-list">
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={1} style={NOWRAP}>
                  Task
                </Table.Th>
                <Table.Th w={90}>Interval</Table.Th>
                <Table.Th w={190}>Last run</Table.Th>
                <Table.Th w={110}>Last duration</Table.Th>
                <Table.Th w={190}>Next run</Table.Th>
                <Table.Th>Last result</Table.Th>
                <Table.Th w={110} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tasks.data.map((task) => (
                <Table.Tr key={task.id}>
                  <Table.Td style={NOWRAP}>{task.name}</Table.Td>
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
                <Table.Th w={1} style={NOWRAP}>
                  Command
                </Table.Th>
                <Table.Th w={110}>Status</Table.Th>
                <Table.Th w={190}>Queued</Table.Th>
                <Table.Th w={190}>Ended</Table.Th>
                <Table.Th w={110}>Duration</Table.Th>
                <Table.Th>Message</Table.Th>
                <Table.Th w={90} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {commands.data.slice(0, 20).map((command) => (
                <Table.Tr key={command.id}>
                  <Table.Td style={NOWRAP}>{command.commandName}</Table.Td>
                  <Table.Td>
                    <Text c={statusColour(command.status)}>{statusLabel(command.status)}</Text>
                  </Table.Td>
                  <Table.Td>{formatTimestamp(command.queued)}</Table.Td>
                  <Table.Td>{formatTimestamp(command.ended)}</Table.Td>
                  <Table.Td>{orDash(command.duration)}</Table.Td>
                  <Table.Td>{orDash(command.message)}</Table.Td>
                  <Table.Td>
                    {canCancel(command) && (
                      <Button
                        size="xs"
                        variant="subtle"
                        color="red"
                        loading={cancelCommand.isPending && cancelCommand.variables === command.id}
                        onClick={() => cancelCommand.mutate(command.id)}
                      >
                        Cancel
                      </Button>
                    )}
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
