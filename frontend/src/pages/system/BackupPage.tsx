import {
  ActionIcon,
  Alert,
  Button,
  Card,
  FileButton,
  Group,
  Loader,
  Modal,
  Stack,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { Archive, Download, RotateCcw, Trash2, Upload } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useAppConfig } from '../../api/context';
import {
  backupDownloadUrl,
  reloadAfterRestore,
  useBackups,
  useCreateBackup,
  useDeleteBackup,
  usePing,
  useRestoreBackup,
  useUploadRestore,
  type BackupResource,
} from '../../api/system';
import { EmptyState, ErrorState, LoadingState } from '../../components/DataState';

/** A byte count the way a file list shows it. */
function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${value.toFixed(1)} ${units[unit]}`;
}

/** What the confirmation modal is asking about. */
type Pending =
  | { kind: 'restore'; backup: BackupResource }
  | { kind: 'upload'; file: File }
  | { kind: 'delete'; backup: BackupResource };

/**
 * System → Backup: the zips the weekly task and the user made, with download, restore and delete.
 * A restore stages the backup and the app restarts to apply it, so the page waits for `/ping` to
 * answer again and then reloads — the settings, including the API key, are the backup's from then on.
 */
export function BackupPage() {
  const config = useAppConfig();
  const backups = useBackups();
  const create = useCreateBackup();
  const remove = useDeleteBackup();
  const restore = useRestoreBackup();
  const upload = useUploadRestore();
  const [pending, setPending] = useState<Pending | null>(null);
  const [restarting, setRestarting] = useState(false);
  // The app goes away before it comes back: wait for one failed ping before a good one counts.
  const wentAway = useRef(false);
  const ping = usePing(restarting);

  useEffect(() => {
    if (!restarting) {
      return;
    }

    if (ping.data === false || ping.error !== null) {
      wentAway.current = true;
    } else if (ping.data === true && wentAway.current) {
      reloadAfterRestore();
    }
  }, [restarting, ping.data, ping.error]);

  const restoreError = restore.error ?? upload.error;

  const confirm = () => {
    if (pending === null) {
      return;
    }

    if (pending.kind === 'delete') {
      remove.mutate(pending.backup.id, { onSuccess: () => setPending(null) });
      return;
    }

    const restarted = {
      onSuccess: () => {
        setPending(null);
        setRestarting(true);
      },
      onError: () => setPending(null),
    };

    if (pending.kind === 'restore') {
      restore.mutate(pending.backup.id, restarted);
    } else {
      upload.mutate(pending.file, restarted);
    }
  };

  if (restarting) {
    return (
      <Stack gap="lg">
        <Title order={2}>Backup</Title>
        <Alert color="blue" title="Restoring — Wondarr is restarting" data-testid="restoring">
          <Group gap="sm">
            <Loader size="sm" />
            <Text size="sm">The page reloads by itself when Wondarr answers again.</Text>
          </Group>
        </Alert>
      </Stack>
    );
  }

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Backup</Title>
        <Group gap="sm">
          <FileButton
            onChange={(file) => file !== null && setPending({ kind: 'upload', file })}
            accept=".zip,application/zip"
          >
            {(props) => (
              <Button {...props} variant="default" leftSection={<Upload size={16} />}>
                Restore from file…
              </Button>
            )}
          </FileButton>
          <Button leftSection={<Archive size={16} />} loading={create.isPending} onClick={() => create.mutate()}>
            Back up now
          </Button>
        </Group>
      </Group>

      {create.error !== null && <ErrorState title="The backup could not be made" message={create.error.message} />}
      {remove.error !== null && <ErrorState title="The backup could not be deleted" message={remove.error.message} />}
      {restoreError !== null && <ErrorState title="The backup could not be restored" message={restoreError.message} />}

      {backups.isPending && <LoadingState />}
      {backups.error !== null && <ErrorState message={backups.error.message} />}
      {backups.data !== undefined && backups.data.length === 0 && (
        <EmptyState message="No backups yet. The weekly task makes one; “Back up now” makes one at once." />
      )}
      {backups.data !== undefined && backups.data.length > 0 && (
        <Card withBorder padding="md">
          <Table data-testid="backup-list">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={110}>Type</Table.Th>
                <Table.Th w={100}>Size</Table.Th>
                <Table.Th w={190}>Time</Table.Th>
                <Table.Th w={120} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {backups.data.map((backup) => (
                <Table.Tr key={backup.id}>
                  <Table.Td>{backup.name}</Table.Td>
                  <Table.Td>{backup.type}</Table.Td>
                  <Table.Td>{formatSize(Number(backup.size))}</Table.Td>
                  <Table.Td>{new Date(backup.time).toLocaleString()}</Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      <Tooltip label="Download">
                        <ActionIcon
                          component="a"
                          href={backupDownloadUrl(config, backup.id)}
                          download={backup.name}
                          variant="subtle"
                          aria-label={`Download ${backup.name}`}
                        >
                          <Download size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label="Restore">
                        <ActionIcon
                          variant="subtle"
                          aria-label={`Restore ${backup.name}`}
                          onClick={() => setPending({ kind: 'restore', backup })}
                        >
                          <RotateCcw size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label="Delete">
                        <ActionIcon
                          variant="subtle"
                          color="red"
                          aria-label={`Delete ${backup.name}`}
                          onClick={() => setPending({ kind: 'delete', backup })}
                        >
                          <Trash2 size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}

      <Modal
        opened={pending !== null}
        onClose={() => setPending(null)}
        title={pending?.kind === 'delete' ? 'Delete the backup' : 'Restore the backup'}
      >
        <Stack gap="md">
          {pending?.kind === 'delete' ? (
            <Text size="sm">{`${pending.backup.name} is deleted from /config/backups. This cannot be undone.`}</Text>
          ) : (
            <Text size="sm">
              {`${pending?.kind === 'restore' ? pending.backup.name : (pending?.file.name ?? '')} replaces the database and config.yml. `}
              Wondarr restarts to apply it, and the API key and every setting become the backup&apos;s. The files it
              replaces are kept next to them as *.pre-restore.
            </Text>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPending(null)}>
              Cancel
            </Button>
            <Button
              color={pending?.kind === 'delete' ? 'red' : undefined}
              loading={remove.isPending || restore.isPending || upload.isPending}
              onClick={confirm}
            >
              {pending?.kind === 'delete' ? 'Delete' : 'Restore and restart'}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
