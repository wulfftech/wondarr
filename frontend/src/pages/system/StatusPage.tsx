import { Anchor, Badge, Card, Group, Stack, Table, Text, Title } from '@mantine/core';
import { useHealth, useSystemStatus, type SystemStatus } from '../../api/hooks';
import { ErrorState, LoadingState } from '../../components/DataState';
import { isProblem, type HealthCheckOutcome } from '../../api/types';
import { initCaps, updateSummary } from '../../components/text';
import { useUpdateStatus } from '../../api/update';
import { Link } from 'react-router';

const OUTCOME_COLOR: Record<HealthCheckOutcome, string> = {
  ok: 'green',
  notice: 'blue',
  warning: 'yellow',
  error: 'red',
};

function formatTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }

  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

function orDash(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' ? '—' : value;
}

function yesNo(value: boolean | undefined): string {
  return value === true ? 'Yes' : 'No';
}

/**
 * `AuthenticationMethod` arrives as a number: the OpenAPI document publishes the enum's underlying
 * type but not its values. The names are `Wondarr.Core.Configuration.AuthenticationMethod`.
 */
const AUTHENTICATION_NAMES: Record<number, string> = {
  0: 'None',
  1: 'Forms',
  2: 'External',
};

function authenticationName(value: number): string {
  return AUTHENTICATION_NAMES[value] ?? String(value);
}

/** The fields the System → Status table shows, in order. */
function statusRows(status: SystemStatus): { label: string; value: string }[] {
  return [
    { label: 'App name', value: orDash(status.appName) },
    { label: 'Version', value: orDash(status.version) },
    { label: 'Runtime', value: `${orDash(status.runtimeName)} ${orDash(status.runtimeVersion)}` },
    { label: 'Operating system', value: `${orDash(status.osName)} ${orDash(status.osVersion)}` },
    { label: 'Docker', value: yesNo(status.isDocker) },
    { label: 'Database', value: `${orDash(status.databaseType)} ${orDash(status.databaseVersion)}` },
    { label: 'URL base', value: status.urlBase === '' ? '/' : orDash(status.urlBase) },
    { label: 'Start time', value: formatTimestamp(status.startTime) },
    { label: 'Authentication', value: authenticationName(status.authentication) },
  ];
}

/** The app identity, host and database facts, plus the health list. */
export function StatusPage() {
  const status = useSystemStatus();
  const health = useHealth();
  const update = useUpdateStatus();

  return (
    <Stack gap="lg">
      <Title order={2}>Status</Title>

      {status.isLoading && <LoadingState />}
      {status.error !== null && <ErrorState message={status.error.message} />}
      {status.data !== undefined && (
        <Card withBorder padding="md">
          <Table data-testid="system-status">
            <Table.Tbody>
              {statusRows(status.data).map((row) => (
                <Table.Tr key={row.label}>
                  <Table.Td w={220}>
                    <Text fw={500}>{row.label}</Text>
                  </Table.Td>
                  <Table.Td>{row.value}</Table.Td>
                </Table.Tr>
              ))}
              {update.data !== undefined && (
                <Table.Tr>
                  <Table.Td w={220}>
                    <Text fw={500}>Update</Text>
                  </Table.Td>
                  <Table.Td>
                    <Anchor component={Link} to="/system/updates" data-testid="update-row">
                      {updateSummary(update.data)}
                    </Anchor>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      <Group justify="space-between">
        <Title order={3}>Health</Title>
      </Group>

      {health.isLoading && <LoadingState />}
      {health.error !== null && <ErrorState message={health.error.message} />}
      {health.data !== undefined && health.data.length === 0 && <Text c="dimmed">No health checks ran.</Text>}
      {health.data !== undefined && health.data.length > 0 && (
        <Card withBorder padding="md">
          <Table data-testid="health-list">
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={180}>Source</Table.Th>
                <Table.Th w={110}>State</Table.Th>
                <Table.Th>Message</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {health.data.map((entry) => (
                <Table.Tr key={`${entry.source}-${entry.message}`}>
                  <Table.Td>{entry.source}</Table.Td>
                  <Table.Td>
                    <Badge color={OUTCOME_COLOR[entry.type]} variant="light">
                      {isProblem(entry) ? initCaps(entry.type) : 'OK'}
                    </Badge>
                  </Table.Td>
                  <Table.Td>{entry.message}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}
    </Stack>
  );
}
