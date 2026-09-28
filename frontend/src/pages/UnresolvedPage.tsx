import { Alert, Button, Card, Center, Group, Pagination, Stack, Text, TextInput, Title } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { firstPage, type Paging } from '../api/paging';
import {
  candidateAddInput,
  useImportListItems,
  useResolveItem,
  useSkipItem,
  useSongLookup,
  type ImportListCandidate,
  type ImportListItemResource,
} from '../api/songs';
import { EmptyState, ErrorState, LoadingState } from '../components/DataState';
import { formatDuration } from '../components/SongCells';
import { CandidateRow } from './AddSongsPage';

/**
 * The review screen: the pasted lines Compilarr could not place, each with the candidates it stored,
 * an inline search to find the right one by hand, and a Skip for the lines that should be dropped.
 */

/** How a candidate is labelled in the stored-candidate list: its MBID, else its Deezer track id. */
function candidateKey(candidate: ImportListCandidate): string {
  return candidate.mbRecordingId ?? `deezer:${String(candidate.deezerId)}`;
}

/** The score as a whole number; the API sends it as a double. */
function score(candidate: ImportListCandidate): number {
  return Math.round(Number(candidate.score));
}

/** One unresolved line, and the three things the user can do with it. */
function UnresolvedCard({ item, onHandled }: { item: ImportListItemResource; onHandled: (id: number) => void }) {
  const resolve = useResolveItem();
  const skip = useSkipItem();
  const lookup = useSongLookup();
  const [searching, setSearching] = useState(false);
  const [term, setTerm] = useState(item.text);

  const itemId = Number(item.id);

  const use = (candidate: ImportListCandidate) => {
    resolve.mutate({ itemId, ...candidateAddInput(candidate) }, { onSuccess: () => onHandled(itemId) });
  };

  const search = (event: FormEvent) => {
    event.preventDefault();

    if (term.trim() !== '') {
      lookup.mutate(term);
    }
  };

  const error = resolve.error ?? skip.error;

  return (
    <Card withBorder padding="md">
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start" wrap="nowrap">
          <Stack gap={2}>
            <Text fw={500}>{item.text}</Text>
            {item.reason !== null && item.reason !== '' && (
              <Text size="xs" c="dimmed">
                {item.reason}
              </Text>
            )}
          </Stack>

          <Button
            variant="default"
            size="compact-sm"
            loading={skip.isPending}
            onClick={() => skip.mutate(itemId, { onSuccess: () => onHandled(itemId) })}
          >
            Skip
          </Button>
        </Group>

        {item.candidates.length > 0 && (
          <Stack gap={4}>
            <Text size="sm" fw={500}>
              Candidates
            </Text>

            {item.candidates.map((candidate) => (
              <Group key={candidateKey(candidate)} justify="space-between" wrap="nowrap">
                <Group gap="md">
                  <Text size="sm">{candidate.title}</Text>
                  <Text size="xs" c="dimmed">
                    {candidate.artistCredit}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {formatDuration(candidate.durationMs)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    score {score(candidate)}
                  </Text>
                </Group>

                <Button size="compact-sm" variant="light" onClick={() => use(candidate)}>
                  Use this
                </Button>
              </Group>
            ))}
          </Stack>
        )}

        {error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {error.message}
          </Alert>
        )}

        <Group>
          <Button variant="default" size="compact-sm" onClick={() => setSearching((open) => !open)}>
            Search…
          </Button>
        </Group>

        {searching && (
          <Stack gap="sm">
            <form onSubmit={search}>
              <Group align="flex-end">
                <TextInput
                  label="Search"
                  value={term}
                  flex={1}
                  onChange={(event) => setTerm(event.currentTarget.value)}
                />
                <Button type="submit" loading={lookup.isPending}>
                  Search
                </Button>
              </Group>
            </form>

            {lookup.error !== null && (
              <Alert color="red" icon={<CircleAlert size={16} />}>
                {lookup.error.message}
              </Alert>
            )}

            {lookup.isSuccess && (lookup.data ?? []).length === 0 && (
              <EmptyState message="No matches — try 'Artist - Title'" />
            )}

            {(lookup.data ?? []).map((candidate) => (
              <CandidateRow
                key={candidate.mbRecordingId ?? `deezer:${String(candidate.deezerId)}`}
                candidate={candidate}
                action={
                  <Button size="compact-sm" onClick={() => use(candidate)}>
                    Use this
                  </Button>
                }
              />
            ))}
          </Stack>
        )}
      </Stack>
    </Card>
  );
}

/** The import list id in the query string, or `null` when it names none. */
function parseId(value: string | null): number | null {
  if (value === null || !/^\d+$/.test(value)) {
    return null;
  }

  return Number(value);
}

/** The lines Compilarr could not place: pick a candidate, search for one, or skip. */
export function UnresolvedPage() {
  const [params] = useSearchParams();
  const importListId = parseId(params.get('importListId'));
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [handled, setHandled] = useState<ReadonlySet<number>>(() => new Set());

  const items = useImportListItems(paging, {
    importListId: importListId ?? undefined,
    state: 'unresolved',
  });

  const rows = (items.data?.records ?? []).filter((item) => !handled.has(Number(item.id)));
  const pageCount = Math.max(1, Math.ceil(Number(items.data?.totalRecords ?? 0) / Math.max(1, paging.pageSize)));

  return (
    <Stack gap="lg">
      <Title order={2}>Unresolved</Title>

      {items.isPending && <LoadingState />}

      {items.error !== null && <ErrorState message={items.error.message} />}

      {items.isSuccess && rows.length === 0 && <EmptyState message="Nothing to review." />}

      {rows.map((item) => (
        <UnresolvedCard
          key={item.id}
          item={item}
          onHandled={(id) => setHandled((current) => new Set(current).add(id))}
        />
      ))}

      {pageCount > 1 && (
        <Center>
          <Pagination total={pageCount} value={paging.page} onChange={(page) => setPaging({ ...paging, page })} />
        </Center>
      )}
    </Stack>
  );
}
