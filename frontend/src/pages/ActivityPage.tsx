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
import { PagedTable, type PagedColumn } from '../components/PagedTable';
import { formatDate } from '../components/SongCells';

/** The three tabs of the Activity page. The queue itself arrives with the Soulseek source. */
type ActivityTab = 'queue' | 'history' | 'blocklist';

/** The colour each event type wears, so a failure stands out in a scrolling list. */
const EVENT_COLOURS: Record<string, MantineColor> = {
  grabbed: 'blue',
  imported: 'green',
  upgraded: 'teal',
  failed: 'red',
  verified: 'green',
  rejected: 'orange',
  deleted: 'gray',
  renamed: 'yellow',
};

/** Turns an event type the API serialised as a camelCase string into a badge. */
function EventBadge({ eventType }: { eventType: unknown }) {
  const name = typeof eventType === 'string' ? eventType : String(eventType);

  return (
    <Badge color={EVENT_COLOURS[name] ?? 'gray'} variant="light">
      {name}
    </Badge>
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
          <Text size="sm">{item.song.title}</Text>
          <Text size="xs" c="dimmed">
            {item.song.artistCredit}
          </Text>
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

function HistoryTab() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const query: UseQueryResult<HistoryPage, Error> = useHistory(paging);
  const qualities = useQualityDefinitions();
  const qualityNames = new Map((qualities.data ?? []).map((quality) => [String(quality.id), quality.name]));

  return (
    <PagedTable
      columns={historyColumns(qualityNames)}
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

function BlocklistTab() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [pending, setPending] = useState<BlocklistResource | null>(null);
  const query: UseQueryResult<BlocklistPage, Error> = useBlocklist(paging);
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
        columns={blocklistColumns(setPending)}
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
          <Text c="dimmed" size="sm">
            The queue arrives with the Soulseek source in Phase 2.
          </Text>
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
