import { Alert, Anchor, Badge, Card, Code, Group, Select, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import type { MantineColor } from '@mantine/core';
import { useEffect, useState } from 'react';
import { useAppConfig } from '../../api/context';
import { firstPage, type Paging } from '../../api/paging';
import { logFileDownloadUrl, useLogFiles, useLogs, type LogResource } from '../../api/system';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';
import { PagedTable, type PagedColumn } from '../../components/PagedTable';

/** The levels the filter offers, lowest first; the API shows the chosen one and everything above it. */
const LEVELS = ['Debug', 'Information', 'Warning', 'Error'];

/** How long the text filter waits after the last keystroke before it asks again, in milliseconds. */
const FILTER_DEBOUNCE_MS = 400;

function levelColour(level: string | null | undefined): MantineColor {
  switch ((level ?? '').toLowerCase()) {
    case 'fatal':
    case 'error':
      return 'red';
    case 'warning':
      return 'yellow';
    case 'debug':
    case 'verbose':
      return 'gray';
    default:
      return 'blue';
  }
}

function logColumns(): PagedColumn<LogResource>[] {
  return [
    { label: 'Time', sortKey: null, width: 190, render: (entry) => new Date(entry.time).toLocaleString() },
    {
      label: 'Level',
      sortKey: null,
      width: 110,
      render: (entry) => (
        <Badge variant="light" color={levelColour(entry.level)}>
          {entry.level}
        </Badge>
      ),
    },
    { label: 'Logger', sortKey: null, width: 220, render: (entry) => entry.logger ?? '—' },
    {
      label: 'Message',
      sortKey: null,
      render: (entry) => (
        <Stack gap={4}>
          <Text size="sm" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
            {entry.message}
          </Text>
          {entry.exception !== null && entry.exception !== undefined && entry.exception !== '' && (
            <details>
              <summary>
                <Text span size="xs" c="dimmed">
                  Exception
                </Text>
              </summary>
              <Code block>{entry.exception}</Code>
            </details>
          )}
        </Stack>
      ),
    },
  ];
}

/** System → Logs: the app's own log (slskd's output included), newest first, and the files to download. */
export function LogsPage() {
  const config = useAppConfig();
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [level, setLevel] = useState('Information');
  const [text, setText] = useState('');
  const [filter, setFilter] = useState('');
  const logs = useLogs(paging, { level, filter });
  const files = useLogFiles();

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setFilter(text.trim());
      setPaging((current) => ({ ...current, page: 1 }));
    }, FILTER_DEBOUNCE_MS);

    return () => window.clearTimeout(timer);
  }, [text]);

  return (
    <Stack gap="lg">
      <Title order={2}>Logs</Title>

      <Group gap="md" align="flex-end">
        <Select
          label="Level"
          data-testid="log-level"
          data={LEVELS}
          value={level}
          allowDeselect={false}
          onChange={(value) => {
            setLevel(value ?? 'Information');
            setPaging((current) => ({ ...current, page: 1 }));
          }}
          w={180}
        />
        <TextInput
          label="Filter"
          placeholder="Text in the message"
          value={text}
          onChange={(event) => setText(event.currentTarget.value)}
          w={320}
        />
      </Group>

      {logs.data?.truncated === true && (
        <Alert color="yellow" data-testid="log-truncated">
          Only the newest part of the log was searched. Download the files below for everything older.
        </Alert>
      )}

      <PagedTable
        columns={logColumns()}
        rows={logs.data?.page.records ?? []}
        totalRecords={Number(logs.data?.page.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={logs.isPending}
        error={logs.error}
        emptyMessage="Nothing logged at this level."
        rowKey={(entry) => entry.id}
      />

      <Card withBorder padding="md">
        <Stack gap="sm">
          <Title order={4}>Log files</Title>
          {files.isPending && <LoadingState />}
          {files.error !== null && <ErrorState message={files.error.message} />}
          {files.data !== undefined && files.data.length === 0 && <EmptyState message="No log files yet." />}
          {files.data !== undefined && files.data.length > 0 && (
            <Table data-testid="log-files">
              <Table.Tbody>
                {files.data.map((file) => (
                  <Table.Tr key={file.filename}>
                    <Table.Td>
                      <Anchor href={logFileDownloadUrl(config, file.filename)} download={file.filename}>
                        {file.filename}
                      </Anchor>
                    </Table.Td>
                    <Table.Td w={190}>{new Date(file.lastWriteTime).toLocaleString()}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </Stack>
      </Card>
    </Stack>
  );
}
