import { Alert, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { CircleAlert } from 'lucide-react';
import { useEffect, useState } from 'react';
import {
  invalidateCompaction,
  useCompactCommand,
  useCompactPlan,
  useRunCompact,
  type CompactAlbumResource,
  type CompactMoveResource,
} from '../../api/compact';
import { readEnum } from '../../api/profiles';
import { type CommandStatusName } from '../../api/songs';
import { ErrorState, LoadingState } from '../../components/DataState';

/**
 * Compact library (ADR-0007): the dry run of the album re-plan and, behind one confirmation, the
 * command that carries it out. The albums a song is filed under are sticky, so this is the only way
 * they change — which is why the user is shown every move before any file is touched.
 */

/** The explanation of what the command does, in the order the user reads it. */
const EXPLANATION =
  "Re-plans every song's album under this library's album policy, as if the library were built today. " +
  'Songs you placed on an album yourself are kept. Files move to their new album folders; Plex is told ' +
  'to forget the old albums first.';

/** How an album reads in a move: its title, and its artist when that is not the song's own. */
function albumText(album: CompactAlbumResource): string {
  return album.albumArtist === '' ? album.albumTitle : `${album.albumArtist} — ${album.albumTitle}`;
}

/** One endpoint of a move: the album, and the file's path or "album only" when none travels. */
function MoveSide({ album, path }: { album: CompactAlbumResource; path: string | null }) {
  return (
    <Stack gap={0}>
      <Text size="sm">{albumText(album)}</Text>
      <Text size="xs" c="dimmed" ff="monospace" style={{ overflowWrap: 'anywhere' }}>
        {path ?? 'album only'}
      </Text>
    </Stack>
  );
}

/** One song's move, both sides. */
function MoveRow({ move }: { move: CompactMoveResource }) {
  return (
    <Table.Tr>
      <Table.Td>
        <Stack gap={0}>
          <Text size="sm">{move.title}</Text>
          <Text size="xs" c="dimmed">
            {move.artistCredit}
          </Text>
        </Stack>
      </Table.Td>
      <Table.Td>
        <MoveSide album={move.from} path={move.fromPath} />
      </Table.Td>
      <Table.Td>
        <MoveSide album={move.to} path={move.toPath} />
      </Table.Td>
    </Table.Tr>
  );
}

/** The Compact library dialog for one library. */
export function CompactLibraryModal({
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
  const plan = useCompactPlan(libraryId, opened);
  const run = useRunCompact();

  const [confirming, setConfirming] = useState(false);
  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCompactCommand(commandId);

  const status = readEnum<CommandStatusName>(command.data?.status);
  const finished =
    command.data !== undefined && (status === 'completed' || status === 'failed' || status === 'aborted');
  const failed = status === 'failed' || status === 'aborted';
  const moves = plan.data?.moves ?? [];
  const hasMoves = moves.length > 0;

  // The moves are on disk now, so the songs and the plan the user is looking at are both stale.
  useEffect(() => {
    if (finished) {
      invalidateCompaction(queryClient);
    }
  }, [finished, queryClient]);

  const close = () => {
    setConfirming(false);
    setCommandId(null);
    onClose();
  };

  return (
    <Modal opened={opened} onClose={close} title={`Compact ${libraryName}`} size="xl">
      <Stack gap="md">
        <Text size="sm">{EXPLANATION}</Text>

        {plan.isPending && <LoadingState label="Planning the moves…" />}

        {plan.error !== null && <ErrorState message={plan.error.message} />}

        {plan.data !== undefined && (
          <>
            <Text fw={600}>
              {plan.data.albumsBefore} albums → {plan.data.albumsAfter} albums, {moves.length} songs move
            </Text>

            {!hasMoves && (
              <Text size="sm">Nothing to compact — every song is already on the album the policy would choose.</Text>
            )}

            {hasMoves && (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Song</Table.Th>
                    <Table.Th>From</Table.Th>
                    <Table.Th>To</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {moves.map((move) => (
                    <MoveRow key={String(move.songId)} move={move} />
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </>
        )}

        {run.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {run.error.message}
          </Alert>
        )}

        {command.data !== undefined && (
          <Text size="sm" c={failed ? 'red' : finished ? 'green' : undefined}>
            {failed ? (command.data.exception ?? command.data.message) : command.data.message}
          </Text>
        )}

        {confirming ? (
          <Stack gap="xs">
            <Text size="sm">Move {moves.length} files? This rescans the linked Plex library.</Text>

            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirming(false)}>
                Cancel
              </Button>
              <Button
                loading={run.isPending}
                onClick={() => run.mutate(libraryId, { onSuccess: (queued) => setCommandId(Number(queued.id)) })}
              >
                Run compaction
              </Button>
            </Group>
          </Stack>
        ) : (
          <Group justify="flex-end">
            <Button variant="default" onClick={close}>
              Close
            </Button>
            <Button disabled={!hasMoves || commandId !== null} onClick={() => setConfirming(true)}>
              Run compaction
            </Button>
          </Group>
        )}
      </Stack>
    </Modal>
  );
}
