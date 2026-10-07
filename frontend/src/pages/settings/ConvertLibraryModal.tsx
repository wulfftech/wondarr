import { Alert, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { CircleAlert } from 'lucide-react';
import { useEffect, useState } from 'react';
import {
  SONGS_QUERY_KEY,
  useCommand,
  useConvert,
  useConvertPreview,
  type CommandStatusName,
  type ConvertSongResource,
} from '../../api/songs';
import { readEnum } from '../../api/profiles';
import { ErrorState, LoadingState } from '../../components/DataState';

/**
 * Convert a library's existing files (LIBRARY_OUTPUT §7.7): the dry run of what the library's own
 * conversion rules would do to every file it holds, then the command that carries it out. The plan
 * is read from the stored file rows alone, so the user sees every outcome before any file is touched.
 */

/** The explanation of what the command does, in the order the user reads it. */
const EXPLANATION =
  "Re-encodes this library's files to match its conversion rules, one file at a time. " +
  'The originals go to the recycle bin.';

/** How many of the plan's rows are listed before the "and the rest" line takes over. */
const MAX_ROWS = 10;

/**
 * Reads a size the client may have sent as a string, short enough for the plan's one-line summary.
 * The plan's sizes are whole bytes, so the ladder is the queue page's.
 */
function formatBytes(bytes: number | string | null | undefined): string {
  const value = typeof bytes === 'string' ? Number(bytes) : bytes;

  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) {
    return '—';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let scaled = value;
  let unit = 0;

  while (scaled >= 1024 && unit < units.length - 1) {
    scaled /= 1024;
    unit += 1;
  }

  const rounded = unit === 0 || scaled >= 10 ? Math.round(scaled) : Number(scaled.toFixed(1));

  return `${rounded} ${units[unit]}`;
}

/** One song's row of the plan: the file it holds, what it becomes, and why. */
function PlanRow({ song }: { song: ConvertSongResource }) {
  const from = song.fromCodec ?? '—';
  const to = song.toCodec ?? '—';

  return (
    <Table.Tr>
      <Table.Td>{String(song.songId)}</Table.Td>
      <Table.Td>
        {from} → {to}
      </Table.Td>
      <Table.Td>{song.reason ?? ''}</Table.Td>
    </Table.Tr>
  );
}

/** The Convert existing files dialog for one library. */
export function ConvertLibraryModal({
  libraryId,
  libraryName,
  opened,
  onClose,
}: {
  libraryId: number;
  libraryName: string;
  opened: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  // The library's own rules: no songs named, no one-off rule — the plan is the whole library.
  const plan = useConvertPreview(libraryId, opened);
  const convert = useConvert();

  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCommand(commandId, { poll: true });

  const status = readEnum<CommandStatusName>(command.data?.status);
  const finished =
    command.data !== undefined && (status === 'completed' || status === 'failed' || status === 'aborted');
  const failed = status === 'failed' || status === 'aborted';
  const songs = plan.data?.songs ?? [];
  const shown = songs.slice(0, MAX_ROWS);
  const toConvert = Number(plan.data?.convert ?? 0);

  // The files are on disk now, so the songs the user is looking at are stale.
  useEffect(() => {
    if (finished) {
      void queryClient.invalidateQueries({ queryKey: SONGS_QUERY_KEY });
    }
  }, [finished, queryClient]);

  const close = () => {
    setCommandId(null);
    onClose();
  };

  const run = () => {
    convert.mutate(
      { songIds: null, libraryId, rule: null },
      { onSuccess: (accepted) => setCommandId(Number(accepted.commandId)) },
    );
  };

  return (
    <Modal opened={opened} onClose={close} title={`Convert ${libraryName}`} size="xl">
      <Stack gap="md">
        <Text size="sm">{EXPLANATION}</Text>

        {plan.isPending && <LoadingState label="Planning the conversion…" />}

        {plan.error !== null && <ErrorState message={plan.error.message} />}

        {plan.data !== undefined && (
          <>
            <Text fw={600}>
              {plan.data.convert} files would be converted, {plan.data.skip} skipped, {plan.data.refuse} refused; about{' '}
              {formatBytes(plan.data.currentSize)} → {formatBytes(plan.data.estimatedSize)}
            </Text>

            {songs.length === 0 && <Text size="sm">Nothing to convert — every file already matches the rules.</Text>}

            {songs.length > 0 && (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Song</Table.Th>
                    <Table.Th>From → to</Table.Th>
                    <Table.Th>Reason</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {shown.map((song) => (
                    <PlanRow key={String(song.songId)} song={song} />
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {songs.length > shown.length && (
              <Text size="xs" c="dimmed">
                and {songs.length - shown.length} more
              </Text>
            )}
          </>
        )}

        {convert.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {convert.error.message}
          </Alert>
        )}

        {command.data !== undefined && (
          <Text size="sm" c={failed ? 'red' : finished ? 'green' : undefined}>
            {failed ? (command.data.exception ?? command.data.message) : command.data.message}
          </Text>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            Close
          </Button>
          <Button disabled={toConvert === 0 || commandId !== null} loading={convert.isPending} onClick={run}>
            Convert
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
