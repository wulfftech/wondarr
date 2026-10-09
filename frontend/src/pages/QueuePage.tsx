import { ActionIcon, Badge, Button, Group, Menu, Modal, Progress, Stack, Switch, Text, Tooltip } from '@mantine/core';
import type { MantineColor } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import { EllipsisVertical } from 'lucide-react';
import { useState } from 'react';
import { firstPage, type Paging } from '../api/paging';
import { readEnum } from '../api/profiles';
import {
  useQueue,
  useRemoveQueueItem,
  type QueueItemStateName,
  type QueuePage as QueuePageResource,
  type QueueResource,
  type RemoveQueueItemInput,
} from '../api/queue';
import { PagedTable, type PagedColumn } from '../components/PagedTable';

/** The queue: every grab in flight, how far it has got and why one failed (ARCHITECTURE §5.6). */

/** The two removals that ask before they act; a plain removal happens on the click. */
type ConfirmableAction = 'removeBlocklist' | 'blocklistSearch';

/** How each of the three row actions reads in the menu, the dialog and its button. */
const ACTION_LABELS: Record<ConfirmableAction, string> = {
  removeBlocklist: 'Remove and blocklist',
  blocklistSearch: 'Blocklist and search again',
};

/** What each state says on screen. */
const STATE_LABELS: Record<QueueItemStateName, string> = {
  queued: 'Queued',
  remotelyQueued: 'Queued by peer',
  downloading: 'Downloading',
  completed: 'Completed',
  importing: 'Importing',
  imported: 'Imported',
  failed: 'Failed',
  cancelled: 'Cancelled',
};

/** How each state reads at a glance: a failure red, a finished import green. */
const STATE_COLOURS: Record<QueueItemStateName, MantineColor> = {
  queued: 'gray',
  remotelyQueued: 'orange',
  downloading: 'blue',
  completed: 'teal',
  importing: 'cyan',
  imported: 'green',
  failed: 'red',
  cancelled: 'gray',
};

/**
 * `12.3 MB`, or an em dash when nothing usable arrived. The API sends the sizes as int64 that a
 * client may also read as a string, so both spellings are accepted.
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

/** How long ago a grab was recorded, short enough for a table cell. */
function formatAge(iso: string | null | undefined): string {
  if (iso === null || iso === undefined || iso === '') {
    return '—';
  }

  const parsed = new Date(iso);

  if (Number.isNaN(parsed.getTime())) {
    return '—';
  }

  const seconds = Math.max(0, Math.round((Date.now() - parsed.getTime()) / 1000));

  if (seconds < 60) {
    return `${seconds}s`;
  }

  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)}m`;
  }

  if (seconds < 86400) {
    return `${Math.floor(seconds / 3600)}h`;
  }

  return `${Math.floor(seconds / 86400)}d`;
}

/** The state as a badge; a peer's own queue position is part of the label when there is one. */
function StateBadge({ item }: { item: QueueResource }) {
  const state = readEnum<QueueItemStateName>(item.state);
  const place = item.placeInQueue === null ? null : Number(item.placeInQueue);
  const named = (STATE_LABELS as Record<string, string>)[state] ?? String(item.state);
  const label = state === 'remotelyQueued' && place !== null ? `${named} (#${place})` : named;

  return (
    <Badge color={(STATE_COLOURS as Record<string, MantineColor>)[state] ?? 'gray'} variant="light">
      {label}
    </Badge>
  );
}

/** How far the transfer has got: the bar, the percentage and the sizes behind it. */
function ProgressCell({ item }: { item: QueueResource }) {
  const fraction = Number(item.progress);
  const percent = Number.isFinite(fraction) ? Math.min(100, Math.max(0, Math.round(fraction * 100))) : 0;

  return (
    <Stack gap={2} w={160}>
      <Progress value={percent} size="sm" aria-label={`${percent}% downloaded`} />
      <Text size="xs" c="dimmed">
        {percent}% · {formatBytes(item.bytesTransferred)}
        {item.sizeBytes === null ? '' : ` of ${formatBytes(item.sizeBytes)}`}
      </Text>
    </Stack>
  );
}

/** The body of one removal: what the user asked for, as the endpoint reads it. */
/** The last segment of a path inside a container, whichever slash it was written with. */
function fileNameOf(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

function removeInput(item: QueueResource, action: ConfirmableAction): RemoveQueueItemInput {
  return {
    id: Number(item.id),
    blocklist: true,
    // "Blocklist and search again" replaces the blocklisted file straight away; "remove and
    // blocklist" leaves the song for the next scheduled search.
    skipRedownload: action === 'removeBlocklist',
  };
}

function queueColumns(
  onRemove: (item: QueueResource) => void,
  onConfirm: (item: QueueResource, action: ConfirmableAction) => void,
): PagedColumn<QueueResource>[] {
  return [
    {
      label: 'Song',
      sortKey: null,
      render: (item) => (
        <Stack gap={0}>
          <Text size="sm">{item.songTitle}</Text>
          <Text size="xs" c="dimmed">
            {item.artistCredit}
          </Text>
        </Stack>
      ),
    },
    { label: 'State', sortKey: null, width: 170, render: (item) => <StateBadge item={item} /> },
    { label: 'Progress', sortKey: null, width: 180, render: (item) => <ProgressCell item={item} /> },
    {
      label: 'Source',
      sortKey: null,
      width: 150,
      render: (item) => (
        <Stack gap={0}>
          <Text size="sm">{item.sourceType}</Text>
          {item.provider !== null && (
            <Text size="xs" c="dimmed">
              {item.provider}
            </Text>
          )}
        </Stack>
      ),
    },
    {
      label: 'File',
      sortKey: null,
      render: (item) => (
        <Tooltip label={item.remotePath} multiline w={420}>
          <Stack gap={0}>
            <Text size="sm">{item.displayName}</Text>
            {item.releaseTitle ? (
              <Text size="xs" c="dimmed">
                {item.releaseTitle}
                {item.containerFile ? ` · ${fileNameOf(item.containerFile)}` : ''}
              </Text>
            ) : null}
          </Stack>
        </Tooltip>
      ),
    },
    {
      label: 'Quality',
      sortKey: null,
      width: 110,
      render: (item) => item.qualityName ?? `#${item.qualityId}`,
    },
    { label: 'Attempt', sortKey: null, width: 80, render: (item) => item.attempt },
    {
      label: 'Message',
      sortKey: null,
      render: (item) =>
        item.message === null || item.message === '' ? (
          '—'
        ) : (
          <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
            {item.message}
          </Text>
        ),
    },
    { label: 'Age', sortKey: null, width: 80, render: (item) => formatAge(item.createdAt) },
    {
      label: 'Actions',
      sortKey: null,
      width: 60,
      render: (item) => (
        <Menu withinPortal>
          <Menu.Target>
            <ActionIcon
              variant="subtle"
              aria-label={`Actions for ${item.songTitle}`}
              onClick={(event) => event.stopPropagation()}
            >
              <EllipsisVertical size={16} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item onClick={() => onRemove(item)}>Remove</Menu.Item>
            <Menu.Item onClick={() => onConfirm(item, 'removeBlocklist')}>{ACTION_LABELS.removeBlocklist}</Menu.Item>
            <Menu.Item onClick={() => onConfirm(item, 'blocklistSearch')}>{ACTION_LABELS.blocklistSearch}</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      ),
    },
  ];
}

/** The confirmation the two blocklisting removals ask for. */
function ConfirmRemovalModal({
  pending,
  error,
  busy,
  onCancel,
  onConfirm,
}: {
  pending: { item: QueueResource; action: ConfirmableAction } | null;
  error: Error | null;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const action = pending?.action ?? 'removeBlocklist';

  return (
    <Modal opened={pending !== null} onClose={onCancel} title={ACTION_LABELS[action]}>
      <Stack gap="md">
        <Text size="sm">
          {pending === null
            ? ''
            : action === 'removeBlocklist'
              ? `Stop “${pending.item.displayName}” and blocklist it, so the same file is never grabbed again?`
              : `Stop “${pending.item.displayName}”, blocklist it and search for a replacement now?`}
        </Text>

        {error !== null && (
          <Text size="sm" c="red">
            {error.message}
          </Text>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onCancel}>
            Cancel
          </Button>
          <Button color="red" loading={busy} onClick={onConfirm}>
            {ACTION_LABELS[action]}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The queue's table, its "show finished" toggle and its row-confirmation dialog. */
export function QueuePage() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [includeFinished, setIncludeFinished] = useState(false);
  const [pending, setPending] = useState<{ item: QueueResource; action: ConfirmableAction } | null>(null);
  const query: UseQueryResult<QueuePageResource, Error> = useQueue(paging, includeFinished);
  const remove = useRemoveQueueItem();

  const removeNow = (item: QueueResource) => remove.mutate({ id: Number(item.id), blocklist: false });

  const confirm = () => {
    if (pending === null) {
      return;
    }

    remove.mutate(removeInput(pending.item, pending.action), { onSuccess: () => setPending(null) });
  };

  return (
    <Stack gap="md">
      <Group justify="flex-end">
        <Switch
          label="Show finished"
          checked={includeFinished}
          onChange={(event) => {
            setIncludeFinished(event.currentTarget.checked);
            setPaging((current) => ({ ...current, page: 1 }));
          }}
        />
      </Group>

      <PagedTable
        columns={queueColumns(removeNow, (item, action) => setPending({ item, action }))}
        rows={query.data?.records ?? []}
        totalRecords={Number(query.data?.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={query.isPending}
        error={query.error}
        emptyMessage="Nothing is downloading."
        rowKey={(item) => item.id}
      />

      <ConfirmRemovalModal
        pending={pending}
        error={remove.error}
        busy={remove.isPending}
        onCancel={() => setPending(null)}
        onConfirm={confirm}
      />
    </Stack>
  );
}
