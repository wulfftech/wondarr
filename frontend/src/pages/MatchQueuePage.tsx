import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Drawer,
  Group,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert } from 'lucide-react';
import type { MantineColor } from '@mantine/core';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { firstPage, type Paging } from '../api/paging';
import { readEnum } from '../api/profiles';
import { useSongLookup, type SongLookupResource } from '../api/songs';
import {
  useBulkAcceptMatches,
  useMatchQueue,
  useReferenceLibraries,
  useResolveMatch,
  type MatchCandidateResource,
  type MatchQueueItemResource,
  type ReferenceFileStateName,
  type MatchResolveInput,
} from '../api/references';
import { PagedTable, type PagedColumn } from '../components/PagedTable';

/**
 * The Match queue (LIBRARY_OUTPUT §7.6): the files identification could not settle, what each file
 * says about itself, the ranked candidates, and the choices that settle a file — one at a time, or
 * the top candidate for many at once.
 */

/** What each state says on screen. */
const STATE_LABELS: Record<ReferenceFileStateName, string> = {
  ambiguous: 'Needs review',
  unmatched: 'No match',
};

/** How each state reads at a glance. */
const STATE_COLOURS: Record<ReferenceFileStateName, MantineColor> = {
  ambiguous: 'yellow',
  unmatched: 'gray',
};

/** The value the library filter uses for "every library". */
const ALL_LIBRARIES = 'all';

/** `m:ss`, the way a track's length is written everywhere else. */
function formatDuration(ms: number | string | null | undefined): string {
  const value = ms === null || ms === undefined ? Number.NaN : Number(ms);

  if (!Number.isFinite(value) || value < 0) {
    return '—';
  }

  const seconds = Math.round(value / 1000);

  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/**
 * How much longer a candidate runs than the file, e.g. `+3 s`. `null` when either side's length is
 * unknown: a difference from nothing is not a fact.
 */
function durationDifference(candidateMs: number | string | null, fileMs: number | string | null): string | null {
  if (candidateMs === null || fileMs === null) {
    return null;
  }

  const difference = Math.round((Number(candidateMs) - Number(fileMs)) / 1000);

  return `${difference >= 0 ? '+' : ''}${difference} s`;
}

/**
 * The score as a percentage. Candidates carry a confidence between 0 and 1 (the identifier's own
 * scale), which is what the tier thresholds are written against.
 */
function formatScore(score: number | string): string {
  return `${Math.round(Number(score) * 100)}%`;
}

/** What a file's own tags say, as one line: artist – title, then the album. */
function fileFacts(item: MatchQueueItemResource): string {
  const file = item.file;
  const base = item.relativePath.split('/').pop() ?? item.relativePath;
  const name = base.replace(/\.[^.]+$/, '');
  const credited = file.artist === null || file.title === null ? name : `${file.artist} – ${file.title}`;

  return file.album === null || file.album === '' ? credited : `${credited} · ${file.album}`;
}

/** Any MusicBrainz id or other GUID, with the space before it. */
const GUID = /\s*\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;

/**
 * Why a candidate ranked where it did, without the ids: older items were stored as
 * "search <recording id>", which says nothing a person can use.
 */
function readableReason(reason: string): string {
  return reason.replace(GUID, '').replace(/\s+,/g, ',').trim();
}

/** The search term the drawer starts with: the tags when they are there, else the file's own name. */
function searchSeed(item: MatchQueueItemResource): string {
  const file = item.file;

  if (file.artist !== null && file.artist !== '' && file.title !== null && file.title !== '') {
    return `${file.artist} – ${file.title}`;
  }

  const base = item.relativePath.split('/').pop() ?? item.relativePath;

  return base.replace(/\.[^.]+$/, '');
}

/** The state as a badge. */
function StateBadge({ state }: { state: string }) {
  const named = readEnum<ReferenceFileStateName>(state);

  return (
    <Badge color={(STATE_COLOURS as Record<string, MantineColor>)[named] ?? 'gray'} variant="light">
      {(STATE_LABELS as Record<string, string>)[named] ?? state}
    </Badge>
  );
}

/** The best candidate, with why it won and how far its length is from the file's. */
function TopCandidateCell({ item }: { item: MatchQueueItemResource }) {
  const candidate = item.candidates[0];

  if (candidate === undefined) {
    return (
      <Text size="sm" c="dimmed">
        No candidates
      </Text>
    );
  }

  const difference = durationDifference(candidate.durationMs, item.file.durationMs);

  return (
    <Stack gap={0}>
      <Text size="sm">
        {candidate.title} · {candidate.artistCredit}
      </Text>
      <Text size="xs" c="dimmed">
        {formatDuration(candidate.durationMs)}
        {difference === null ? '' : ` (${difference})`} · {formatScore(candidate.score)} ·{' '}
        {readableReason(candidate.reason)}
      </Text>
    </Stack>
  );
}

/** The drawer that settles one file: every candidate, a manual search, and skip. */
function ReviewDrawer({
  item,
  resolve,
  onClose,
}: {
  item: MatchQueueItemResource;
  resolve: ReturnType<typeof useResolveMatch>;
  onClose: () => void;
}) {
  const lookup = useSongLookup();
  const [term, setTerm] = useState(() => searchSeed(item));
  const [results, setResults] = useState<SongLookupResource[] | null>(null);

  const settle = (input: MatchResolveInput) => resolve.mutate(input, { onSuccess: () => onClose() });

  const search = () =>
    lookup.mutate(term, {
      onSuccess: (found) => setResults(found),
      onError: () => setResults([]),
    });

  return (
    <Stack gap="md">
      <Stack gap={0}>
        <Text size="sm" fw={500}>
          {item.relativePath}
        </Text>
        <Text size="xs" c="dimmed">
          {fileFacts(item)}
        </Text>
        {item.message !== null && item.message !== '' && (
          <Text size="xs" c="dimmed">
            {item.message}
          </Text>
        )}
      </Stack>

      <Text fw={600} size="sm">
        Candidates
      </Text>

      {item.candidates.length === 0 && (
        <Text size="sm" c="dimmed">
          Nothing was found for this file. Search for it below.
        </Text>
      )}

      {item.candidates.map((candidate: MatchCandidateResource) => {
        const difference = durationDifference(candidate.durationMs, item.file.durationMs);

        return (
          <Group key={candidate.rank} justify="space-between" wrap="nowrap" align="flex-start">
            <Stack gap={0}>
              <Text size="sm">
                {candidate.title} · {candidate.artistCredit}
              </Text>
              <Text size="xs" c="dimmed">
                {formatDuration(candidate.durationMs)}
                {difference === null ? '' : ` (${difference})`} · {formatScore(candidate.score)} ·{' '}
                {readableReason(candidate.reason)}
                {candidate.albumTitle === null ? '' : ` · ${candidate.albumTitle}`}
              </Text>
            </Stack>
            <Button
              size="xs"
              variant="light"
              aria-label={`Accept ${candidate.title} for ${item.relativePath}`}
              onClick={() => settle({ id: Number(item.id), candidateRank: Number(candidate.rank) })}
            >
              Accept
            </Button>
          </Group>
        );
      })}

      <Text fw={600} size="sm">
        Search
      </Text>

      <Group gap="xs" align="flex-end" wrap="nowrap">
        <TextInput
          label="Artist – title"
          style={{ flex: 1 }}
          value={term}
          onChange={(event) => setTerm(event.currentTarget.value)}
        />
        <Button variant="light" loading={lookup.isPending} onClick={search}>
          Search
        </Button>
      </Group>

      {(results ?? []).map((result, index) => (
        <Group key={`${result.source}-${index}`} justify="space-between" wrap="nowrap" align="flex-start">
          <Stack gap={0}>
            <Text size="sm">
              {result.title} · {result.artistCredit}
            </Text>
            <Text size="xs" c="dimmed">
              {result.source} · {formatDuration(result.durationMs)} · {formatScore(result.score)}
            </Text>
          </Stack>
          <Button
            size="xs"
            variant="light"
            aria-label={`Accept the ${result.source} result ${result.title}`}
            onClick={() =>
              result.mbRecordingId !== null
                ? settle({ id: Number(item.id), mbRecordingId: result.mbRecordingId })
                : settle({ id: Number(item.id), deezerId: Number(result.deezerId) })
            }
          >
            Accept
          </Button>
        </Group>
      ))}

      {results !== null && results.length === 0 && (
        <Text size="sm" c="dimmed">
          Nothing matched that search.
        </Text>
      )}

      {resolve.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {resolve.error.message}
        </Alert>
      )}

      <Group justify="space-between">
        <Button variant="default" onClick={() => settle({ id: Number(item.id), skip: true })}>
          Skip
        </Button>
        <Button variant="subtle" onClick={onClose}>
          Close
        </Button>
      </Group>
    </Stack>
  );
}

/** The Match queue: the files waiting for a decision, and the ways to make one. */
export function MatchQueuePage() {
  const [params, setParams] = useSearchParams();
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [selected, setSelected] = useState<Set<number>>(() => new Set());
  const [reviewingId, setReviewingId] = useState<number | null>(null);

  const filter = params.get('referenceLibraryId');
  const referenceLibraryId = filter === null || filter === '' ? null : Number(filter);

  const query = useMatchQueue(paging, referenceLibraryId);
  // The filter is by reference library, not by the managed library adopted songs are filed under.
  const libraries = useReferenceLibraries();
  const resolve = useResolveMatch();
  const bulk = useBulkAcceptMatches();

  const rows = query.data?.records ?? [];
  const reviewing = rows.find((row) => Number(row.id) === reviewingId) ?? null;

  const toggle = (id: number, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);

      if (on) {
        next.add(id);
      } else {
        next.delete(id);
      }

      return next;
    });

  const acceptTop = (item: MatchQueueItemResource) => {
    const candidate = item.candidates[0];

    if (candidate === undefined) {
      return;
    }

    resolve.mutate(
      { id: Number(item.id), candidateRank: Number(candidate.rank) },
      {
        onError: (error) => notifications.show({ message: error.message, color: 'red' }),
      },
    );
  };

  const acceptSelected = () =>
    bulk.mutate([...selected], {
      onSuccess: (result) => {
        setSelected(new Set());
        notifications.show({
          title: `Resolved ${result.resolved}, failed ${result.failed}`,
          message: result.errors.length === 0 ? 'Every selected file was accepted.' : result.errors.join('\n'),
          color: result.failed === 0 ? 'green' : 'orange',
        });
      },
      onError: (error) => notifications.show({ message: error.message, color: 'red' }),
    });

  const columns: PagedColumn<MatchQueueItemResource>[] = [
    {
      label: 'Select',
      sortKey: null,
      width: 40,
      render: (item) => (
        <Checkbox
          aria-label={`Select ${item.relativePath}`}
          checked={selected.has(Number(item.id))}
          onChange={(event) => toggle(Number(item.id), event.currentTarget.checked)}
        />
      ),
    },
    {
      label: 'File',
      sortKey: 'relativePath',
      render: (item) => (
        <Stack gap={0}>
          <Tooltip label={item.relativePath} multiline w={420}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
              {item.relativePath}
            </Text>
          </Tooltip>
          <Text size="xs" c="dimmed">
            {fileFacts(item)}
          </Text>
        </Stack>
      ),
    },
    {
      label: 'Format',
      sortKey: null,
      width: 80,
      render: (item) => (item.file.codec === null || item.file.codec === '' ? '—' : item.file.codec.toUpperCase()),
    },
    { label: 'Length', sortKey: null, width: 80, render: (item) => formatDuration(item.file.durationMs) },
    {
      label: 'Bitrate',
      sortKey: null,
      width: 100,
      render: (item) => (item.file.bitrate === null ? '—' : `${String(item.file.bitrate)} kbps`),
    },
    { label: 'State', sortKey: null, width: 130, render: (item) => <StateBadge state={item.state} /> },
    { label: 'Top candidate', sortKey: null, render: (item) => <TopCandidateCell item={item} /> },
    {
      label: 'Actions',
      sortKey: null,
      width: 170,
      render: (item) => (
        <Group gap="xs" wrap="nowrap">
          <Button
            size="xs"
            variant="light"
            aria-label={`Accept the top candidate for ${item.relativePath}`}
            disabled={item.candidates.length === 0}
            onClick={() => acceptTop(item)}
          >
            Accept
          </Button>
          <Button
            size="xs"
            variant="default"
            aria-label={`Review ${item.relativePath}`}
            onClick={() => setReviewingId(Number(item.id))}
          >
            Review
          </Button>
        </Group>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <Title order={2}>Match</Title>

      <Group justify="space-between" align="flex-end">
        <Select
          label="Reference library"
          w={240}
          data={[
            { value: ALL_LIBRARIES, label: 'Every library' },
            ...(libraries.data ?? []).map((library) => ({ value: String(library.id), label: library.name })),
          ]}
          value={filter === null || filter === '' ? ALL_LIBRARIES : filter}
          onChange={(value) => {
            setPaging((current) => ({ ...current, page: 1 }));

            if (value === null || value === ALL_LIBRARIES) {
              setParams({});
            } else {
              setParams({ referenceLibraryId: value });
            }
          }}
        />

        <Group gap="xs">
          <Button variant="default" onClick={() => setSelected(new Set(rows.map((row) => Number(row.id))))}>
            Select all on this page
          </Button>
          <Button loading={bulk.isPending} disabled={selected.size === 0} onClick={acceptSelected}>
            Accept top candidate for selected
          </Button>
        </Group>
      </Group>

      <PagedTable
        columns={columns}
        rows={rows}
        totalRecords={Number(query.data?.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={query.isPending}
        error={query.error}
        emptyMessage="Nothing to review — every file was identified or skipped."
        rowKey={(item) => item.id}
      />

      <Drawer
        opened={reviewing !== null}
        onClose={() => setReviewingId(null)}
        position="right"
        size="xl"
        title="Review a file"
      >
        {reviewing !== null && <ReviewDrawer item={reviewing} resolve={resolve} onClose={() => setReviewingId(null)} />}
      </Drawer>
    </Stack>
  );
}
