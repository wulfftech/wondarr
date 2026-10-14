import { ActionIcon, Badge, Button, Group, Modal, Stack, Tabs, Text, Title, Tooltip } from '@mantine/core';
import type { MantineColor } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import { Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { firstPage, type Paging } from '../api/paging';
import { useQualityDefinitions } from '../api/profiles';
import {
  useBlocklist,
  useDeleteBlocklistItem,
  useHistory,
  type BlocklistPage,
  type BlocklistResource,
  type HistoryPage,
  type HistoryResource,
} from '../api/wanted';
import { ContainerLine } from '../components/ContainerLine';
import { PagedTable, type PagedColumn } from '../components/PagedTable';
import { formatDate } from '../components/SongCells';
import { SongLink } from '../components/song/SongLink';
import { QueuePage } from './QueuePage';
import { initCaps } from '../components/text';

/** The three tabs of the Activity page: what is downloading, what happened and what is blocked. */
type ActivityTab = 'queue' | 'history' | 'blocklist';

/** The colour each event type wears, so a failure stands out in a scrolling list. */
const EVENT_COLOURS: Record<HistoryResource['eventType'], MantineColor> = {
  grabbed: 'blue',
  imported: 'green',
  upgraded: 'teal',
  failed: 'red',
  verified: 'green',
  rejected: 'orange',
  deleted: 'gray',
  renamed: 'yellow',
  converted: 'grape',
};

/** Turns an event type the API serialised as a camelCase string into a badge. */
function EventBadge({ eventType }: { eventType: HistoryResource['eventType'] }) {
  return (
    <Badge color={EVENT_COLOURS[eventType] ?? 'gray'} variant="light">
      {initCaps(eventType)}
    </Badge>
  );
}

/** How many of the other songs a grabbed event names before it says "and N more". */
const BUNDLED_NAMES_SHOWN = 10;

/** One other song that rode along on a grab, as the history row recorded it. */
interface BundledSong {
  songId: number | null;
  title: string;
}

/** The recorded bundled songs, with anything that is not a named song left out. */
function readBundledSongs(value: unknown): BundledSong[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry: unknown): BundledSong[] => {
    const song = entry as { songId?: unknown; title?: unknown } | null;

    if (typeof song?.title !== 'string' || song.title === '') {
      return [];
    }

    return [{ songId: typeof song.songId === 'number' ? song.songId : null, title: song.title }];
  });
}

/** The other songs a grab carried: their names, linked to their pages, capped at ten. */
function BundledSongs({ songs, total }: { songs: BundledSong[]; total: number }) {
  const shown = songs.slice(0, BUNDLED_NAMES_SHOWN);
  const more = Math.max(total, songs.length) - shown.length;

  return (
    <Group gap={4} wrap="wrap">
      <Text size="xs" c="dimmed">
        With:
      </Text>
      {shown.map((song, index) => (
        <Text key={`${song.songId ?? 'x'}-${index}`} size="xs" component="span" c="dimmed">
          {song.songId === null ? song.title : <SongLink songId={song.songId}>{song.title}</SongLink>}
          {index < shown.length - 1 || more > 0 ? ',' : ''}
        </Text>
      ))}
      {more > 0 && (
        <Text size="xs" c="dimmed">
          and {more} more
        </Text>
      )}
    </Group>
  );
}

/**
 * What a grabbed event from a torrent or NZB adds: the release it came out of, the file inside it
 * (the event's display name) and the songs that rode along.
 */
function GrabDetail({ item }: { item: HistoryResource }) {
  if (item.eventType !== 'grabbed') {
    return null;
  }

  const data = item.data as {
    release?: unknown;
    displayName?: unknown;
    bundledWith?: unknown;
    bundledSongs?: unknown;
  } | null;
  const release = typeof data?.release === 'string' && data.release !== '' ? data.release : null;
  const file = typeof data?.displayName === 'string' && data.displayName !== '' ? data.displayName : null;
  const bundled = typeof data?.bundledWith === 'number' ? data.bundledWith : 0;
  const songs = readBundledSongs(data?.bundledSongs);

  if (release === null) {
    return null;
  }

  return (
    <>
      <ContainerLine release={release} file={file} bundledCount={bundled} />
      {songs.length > 0 && <BundledSongs songs={songs} total={bundled} />}
    </>
  );
}

function historyColumns(qualityNames: Map<string, string>): PagedColumn<HistoryResource>[] {
  return [
    { label: 'Date', sortKey: 'date', width: 130, render: (item) => formatDate(item.date) },
    {
      label: 'Song',
      sortKey: null,
      render: (item) => (
        <Stack gap={0}>
          <SongLink songId={item.songId}>{item.song.title}</SongLink>
          <Text size="xs" c="dimmed">
            {item.song.artistCredit}
          </Text>
          <GrabDetail item={item} />
        </Stack>
      ),
    },
    { label: 'Event', sortKey: null, width: 120, render: (item) => <EventBadge eventType={item.eventType} /> },
    {
      label: 'Quality',
      sortKey: null,
      width: 140,
      render: (item) =>
        item.qualityId === null ? '—' : (qualityNames.get(String(item.qualityId)) ?? `#${item.qualityId}`),
    },
  ];
}

function blocklistColumns(onRemove: (item: BlocklistResource) => void): PagedColumn<BlocklistResource>[] {
  return [
    { label: 'Date', sortKey: 'date', width: 130, render: (item) => formatDate(item.date) },
    {
      label: 'Song',
      sortKey: null,
      width: 90,
      render: (item) => (item.songId === null ? '—' : `#${item.songId}`),
    },
    { label: 'Source', sortKey: null, width: 120, render: (item) => item.sourceType },
    { label: 'Key', sortKey: null, render: (item) => item.blocklistKey },
    { label: 'Reason', sortKey: null, render: (item) => item.reason },
    {
      label: 'Remove',
      sortKey: null,
      width: 80,
      render: (item) => (
        <Tooltip label="Remove from the blocklist">
          <ActionIcon
            variant="subtle"
            color="red"
            aria-label={`Remove ${item.blocklistKey}`}
            onClick={() => onRemove(item)}
          >
            <Trash2 size={16} />
          </ActionIcon>
        </Tooltip>
      ),
    },
  ];
}

/** The history log; with a `songId` it is that one song's, and the Song column drops out. */
export function HistoryTab({ songId }: { songId?: number } = {}) {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const query: UseQueryResult<HistoryPage, Error> = useHistory(paging, songId === undefined ? {} : { songId });
  const qualities = useQualityDefinitions();
  const qualityNames = new Map((qualities.data ?? []).map((quality) => [String(quality.id), quality.name]));

  return (
    <PagedTable
      columns={historyColumns(qualityNames).filter((column) => songId === undefined || column.label !== 'Song')}
      rows={query.data?.records ?? []}
      totalRecords={Number(query.data?.totalRecords ?? 0)}
      paging={paging}
      onPaging={setPaging}
      isLoading={query.isPending}
      error={query.error}
      emptyMessage="Nothing has happened yet."
      rowKey={(item) => item.id}
    />
  );
}

/** The blocklist; with a `songId` it is that one song's, and the Song column drops out. */
export function BlocklistTab({ songId }: { songId?: number } = {}) {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [pending, setPending] = useState<BlocklistResource | null>(null);
  const query: UseQueryResult<BlocklistPage, Error> = useBlocklist(paging, songId);
  const remove = useDeleteBlocklistItem();

  const confirm = () => {
    if (pending === null) {
      return;
    }

    remove.mutate(Number(pending.id), { onSuccess: () => setPending(null) });
  };

  return (
    <>
      <PagedTable
        columns={blocklistColumns(setPending).filter((column) => songId === undefined || column.label !== 'Song')}
        rows={query.data?.records ?? []}
        totalRecords={Number(query.data?.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={query.isPending}
        error={query.error}
        emptyMessage="Nothing is blocked."
        rowKey={(item) => item.id}
      />

      <Modal opened={pending !== null} onClose={() => setPending(null)} title="Remove from the blocklist">
        <Stack gap="md">
          <Text size="sm">
            {pending === null
              ? ''
              : `${pending.blocklistKey} was blocked because “${pending.reason}”. Removing it lets the next search offer it again.`}
          </Text>

          {remove.error !== null && (
            <Text size="sm" c="red">
              {remove.error.message}
            </Text>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPending(null)}>
              Cancel
            </Button>
            <Button color="red" loading={remove.isPending} onClick={confirm}>
              Remove
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Activity: the queue, the history log and the blocklist. */
export function ActivityPage() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const active: ActivityTab = tab === 'history' || tab === 'blocklist' ? tab : 'queue';

  return (
    <Stack gap="lg">
      <Title order={2}>Activity</Title>

      <Tabs
        value={active}
        onChange={(value) => {
          void navigate(`/activity/${value ?? 'queue'}`);
        }}
      >
        <Tabs.List>
          <Tabs.Tab value="queue">Queue</Tabs.Tab>
          <Tabs.Tab value="history">History</Tabs.Tab>
          <Tabs.Tab value="blocklist">Blocklist</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="queue" pt="md">
          <QueuePage />
        </Tabs.Panel>

        <Tabs.Panel value="history" pt="md">
          <HistoryTab />
        </Tabs.Panel>

        <Tabs.Panel value="blocklist" pt="md">
          <BlocklistTab />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
