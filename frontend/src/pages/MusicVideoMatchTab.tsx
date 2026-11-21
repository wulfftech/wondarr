import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Chip,
  Group,
  Image,
  Modal,
  Pagination,
  Progress,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { CircleAlert, Video } from 'lucide-react';
import { useState } from 'react';
import {
  useBulkMusicVideoMatch,
  useMusicVideoMatchFiles,
  useMusicVideoSongSearch,
  useResolveMusicVideoMatch,
  useScanMusicVideos,
  type MusicVideoBulkAction,
  type MusicVideoMatchCandidateResource,
  type MusicVideoMatchRowResource,
  type MusicVideoMatchSongResource,
  type MusicVideoMatchState,
} from '../api/musicVideoMatch';
import { firstPage, type Paging } from '../api/paging';
import { apiOrigin } from '../api/client';
import { useAppConfig } from '../api/context';
import { ErrorState, LoadingState } from '../components/DataState';
import { formatDuration } from '../components/SongCells';

/**
 * The Match page's Music videos tab (ADR-0014, the 2026-11-19 amendment): the videos already in the video
 * folder that the scan could not settle. A choice only records which song a file is. The file is never
 * renamed, moved or rewritten.
 */

/** The three lists, in the words the chips use. */
const FILTERS: { value: MusicVideoMatchState; label: string }[] = [
  { value: 'ambiguous', label: 'Needs a pick' },
  { value: 'unmatched', label: 'No match' },
  { value: 'ignored', label: 'Ignored' },
];

/** The most candidates a row shows. */
const MAX_CANDIDATES = 5;

/** The rows on a page. Candidates make each one tall. */
const PAGE_SIZE = 25;

/** A name the scan read, as one line: `Artist – Title`, or whichever half there is. */
function readAs(artist: string | null, title: string | null): string | null {
  const parts = [artist, title].filter((part): part is string => part !== null && part.trim() !== '');

  return parts.length === 0 ? null : parts.join(' – ');
}

/**
 * The names the file was read as, one line for each distinct reading with the sources that gave it. A
 * reading every source agrees on is one line; only differences add lines.
 */
function readings(row: MusicVideoMatchRowResource): { sources: string[]; text: string }[] {
  const found: { sources: string[]; text: string }[] = [];
  const sources: [string, string | null][] = [
    ['File name', readAs(row.parsedArtist, row.parsedTitle)],
    ['.nfo', readAs(row.nfoArtist, row.nfoTitle)],
    ['Tags', readAs(row.tagArtist, row.tagTitle)],
  ];

  for (const [source, text] of sources) {
    if (text === null) {
      continue;
    }

    const same = found.find((reading) => reading.text.toLowerCase() === text.toLowerCase());

    if (same === undefined) {
      found.push({ sources: [source], text });
    } else {
      same.sources.push(source);
    }
  }

  return found;
}

/**
 * The container, as the file's extension says it: ffprobe names an MKV and a WebM file alike
 * ("matroska,webm"), which tells a person nothing. The probed name is the fallback.
 */
function containerLabel(row: MusicVideoMatchRowResource): string | null {
  const extension = /\.([a-z0-9]+)$/i.exec(row.path)?.[1];

  if (extension !== undefined) {
    return extension.toUpperCase();
  }

  return row.container === null || row.container === '' ? null : (row.container.split(',')[0] ?? '').toUpperCase();
}

/** The length, container and picture size, as one line of facts about the file. */
function fileFacts(row: MusicVideoMatchRowResource): string {
  const size = row.width !== null && row.height !== null ? `${row.width}×${row.height}` : null;

  return [formatDuration(row.durationMs), containerLabel(row), size].filter((part) => part !== null).join(' · ');
}

/** The thumbnail beside the file, or nothing while there is none. */
function Thumbnail({ row }: { row: MusicVideoMatchRowResource }) {
  const config = useAppConfig();
  const [failed, setFailed] = useState(false);

  if (row.thumbnailUrl === null || row.thumbnailUrl === '' || failed) {
    return null;
  }

  return (
    <Image
      src={`${apiOrigin(config)}${row.thumbnailUrl}`}
      alt=""
      w={120}
      h={68}
      radius="sm"
      fit="cover"
      onError={() => setFailed(true)}
    />
  );
}

/** A song a person can pick, and the pick that was asked for. */
interface Pick {
  row: MusicVideoMatchRowResource;
  songId: number;
  title: string;
}

/** The score as a bar. It is the raw title similarity, from 0 to 1. */
function ScoreBar({ score }: { score: number | string }) {
  const percent = Math.round(Number(score) * 100);

  return (
    <Group gap="xs" wrap="nowrap">
      <Progress value={percent} w={80} size="sm" aria-label={`Score ${percent}%`} />
      <Text size="xs" c="dimmed">
        {percent}%
      </Text>
    </Group>
  );
}

function CandidateLine({
  candidate,
  path,
  onUse,
}: {
  candidate: MusicVideoMatchCandidateResource;
  path: string;
  onUse: () => void;
}) {
  const title = candidate.title ?? 'Untitled';

  return (
    <Group justify="space-between" wrap="nowrap" align="flex-start">
      <Stack gap={2} style={{ minWidth: 0 }}>
        <Group gap="xs">
          <Text size="sm">
            {candidate.artistCredit ?? 'Unknown artist'} – {title}
          </Text>
          {candidate.songHasVideo && (
            <Badge size="sm" color="orange" variant="light">
              has a video
            </Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed">
          {[candidate.album, formatDuration(candidate.durationMs)]
            .filter((part) => part !== null && part !== '')
            .join(' · ')}
        </Text>
        <Group gap="sm">
          <ScoreBar score={candidate.score} />
          <Text size="xs" c="dimmed">
            {candidate.reason}
          </Text>
        </Group>
      </Stack>
      <Button size="xs" variant="light" aria-label={`Use ${title} for ${path}`} onClick={onUse}>
        Use this
      </Button>
    </Group>
  );
}

/** The song search for one file: words in, songs out, and a pick. */
function SongSearchModal({
  row,
  onClose,
  onPick,
}: {
  row: MusicVideoMatchRowResource;
  onClose: () => void;
  onPick: (song: MusicVideoMatchSongResource) => void;
}) {
  const [text, setText] = useState(() => readAs(row.parsedArtist, row.parsedTitle) ?? '');
  const [term, setTerm] = useState('');
  const songs = useMusicVideoSongSearch(term);

  return (
    <Modal opened onClose={onClose} title={`Find the song for ${row.path}`} size="lg">
      <Stack gap="sm">
        <Group gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            label="Artist or title"
            style={{ flex: 1 }}
            value={text}
            onChange={(event) => setText(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                setTerm(text);
              }
            }}
          />
          <Button variant="light" loading={songs.isFetching} onClick={() => setTerm(text)}>
            Search songs
          </Button>
        </Group>

        {songs.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {songs.error.message}
          </Alert>
        )}

        {songs.data?.length === 0 && (
          <Text size="sm" c="dimmed">
            No song in the library matches that.
          </Text>
        )}

        {songs.data?.map((song) => (
          <Group key={String(song.id)} justify="space-between" wrap="nowrap" align="flex-start">
            <Stack gap={2} style={{ minWidth: 0 }}>
              <Group gap="xs">
                <Text size="sm">
                  {song.artistCredit} – {song.title}
                </Text>
                {song.hasVideo && (
                  <Badge size="sm" color="orange" variant="light">
                    has a video
                  </Badge>
                )}
              </Group>
              <Text size="xs" c="dimmed">
                {[song.album, formatDuration(song.durationMs)]
                  .filter((part) => part !== null && part !== '')
                  .join(' · ')}
              </Text>
            </Stack>
            <Button
              size="xs"
              variant="light"
              aria-label={`Use the song ${song.title} for ${row.path}`}
              onClick={() => onPick(song)}
            >
              Use this
            </Button>
          </Group>
        ))}
      </Stack>
    </Modal>
  );
}

/** One file: what it is, what the scan read it as, the songs it might be, and what to do about it. */
function FileCard({
  row,
  state,
  selected,
  busy,
  onSelect,
  onUse,
  onSearch,
  onIgnore,
  onUnignore,
}: {
  row: MusicVideoMatchRowResource;
  state: MusicVideoMatchState;
  selected: boolean;
  busy: boolean;
  onSelect: (on: boolean) => void;
  onUse: (candidate: MusicVideoMatchCandidateResource) => void;
  onSearch: () => void;
  onIgnore: () => void;
  onUnignore: () => void;
}) {
  const names = readings(row);
  const ignored = state === 'ignored';

  return (
    <Card withBorder padding="sm">
      <Group align="flex-start" wrap="nowrap">
        {!ignored && (
          <Checkbox
            aria-label={`Select ${row.path}`}
            checked={selected}
            onChange={(event) => onSelect(event.currentTarget.checked)}
            mt={2}
          />
        )}

        <Thumbnail row={row} />

        <Stack gap="xs" style={{ flex: 1, minWidth: 0 }}>
          <Stack gap={2}>
            <Text size="sm" fw={500} style={{ overflowWrap: 'anywhere' }}>
              {row.path}
            </Text>
            <Text size="xs" c="dimmed">
              {fileFacts(row)}
            </Text>
            {names.map((reading) => (
              <Text key={reading.text} size="xs" c="dimmed">
                {reading.sources.join(', ')}: {reading.text}
              </Text>
            ))}
          </Stack>

          {!ignored && row.candidates.length === 0 && (
            <Text size="sm" c="dimmed">
              No song in the library looks like this file. Search for one, or ignore it.
            </Text>
          )}

          {!ignored &&
            row.candidates
              .slice(0, MAX_CANDIDATES)
              .map((candidate) => (
                <CandidateLine
                  key={String(candidate.songId)}
                  candidate={candidate}
                  path={row.path}
                  onUse={() => onUse(candidate)}
                />
              ))}

          <Group gap="xs">
            {ignored ? (
              <Button size="xs" variant="default" loading={busy} onClick={onUnignore}>
                Unignore
              </Button>
            ) : (
              <>
                <Button size="xs" variant="default" aria-label={`Search songs for ${row.path}`} onClick={onSearch}>
                  Search…
                </Button>
                <Button size="xs" variant="subtle" color="gray" loading={busy} onClick={onIgnore}>
                  Ignore
                </Button>
              </>
            )}
          </Group>
        </Stack>
      </Group>
    </Card>
  );
}

/** The Music videos tab: filter, settle one file at a time or many at once, or scan again. */
export function MusicVideoMatchTab() {
  const [state, setState] = useState<MusicVideoMatchState>('ambiguous');
  const [paging, setPaging] = useState<Paging>(() => firstPage(PAGE_SIZE));
  const [selected, setSelected] = useState<Set<number>>(() => new Set());
  const [searching, setSearching] = useState<number | null>(null);
  const [confirming, setConfirming] = useState<Pick | null>(null);
  const [workingId, setWorkingId] = useState<number | null>(null);

  const files = useMusicVideoMatchFiles(state, paging);
  const resolve = useResolveMusicVideoMatch();
  const bulk = useBulkMusicVideoMatch();
  const scan = useScanMusicVideos();

  const rows = files.data?.records ?? [];
  const total = Number(files.data?.totalRecords ?? 0);
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const searchingRow = rows.find((row) => Number(row.id) === searching) ?? null;

  const choose = (next: MusicVideoMatchState) => {
    setState(next);
    setPaging((current) => ({ ...current, page: 1 }));
    setSelected(new Set());
  };

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

  const settle = (id: number, input: Parameters<typeof resolve.mutate>[0], done: string) => {
    setWorkingId(id);
    // The toasts (done, or why it failed) belong to the hook; this only moves the tab on.
    resolve.mutate(
      { ...input, doneMessage: done },
      {
        onSuccess: () => {
          setSearching(null);
          setConfirming(null);
          setSelected((current) => {
            const next = new Set(current);

            next.delete(id);

            return next;
          });
        },
        onSettled: () => setWorkingId(null),
      },
    );
  };

  /** A song that already has a video is replaced only after the person has said so. */
  const pick = (chosen: Pick, songHasVideo: boolean) => {
    if (songHasVideo) {
      setConfirming(chosen);

      return;
    }

    settle(Number(chosen.row.id), { id: Number(chosen.row.id), songId: chosen.songId }, `Matched to ${chosen.title}`);
  };

  const runBulk = (action: MusicVideoBulkAction) =>
    bulk.mutate(
      { ids: [...selected], action },
      {
        onSuccess: () => setSelected(new Set()),
      },
    );

  const startScan = () => scan.mutate();

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-end">
        <Chip.Group multiple={false} value={state} onChange={(value) => choose(value)}>
          <Group gap="xs">
            {FILTERS.map((filter) => (
              <Chip key={filter.value} value={filter.value}>
                {filter.label}
              </Chip>
            ))}
          </Group>
        </Chip.Group>

        {state !== 'ignored' && (
          <Group gap="xs">
            <Button variant="default" onClick={() => setSelected(new Set(rows.map((row) => Number(row.id))))}>
              Select all on this page
            </Button>
            <Button loading={bulk.isPending} disabled={selected.size === 0} onClick={() => runBulk('acceptTop')}>
              Accept top match
            </Button>
            <Button
              variant="light"
              loading={bulk.isPending}
              disabled={selected.size === 0}
              onClick={() => runBulk('ignore')}
            >
              Ignore selected
            </Button>
          </Group>
        )}
      </Group>

      {files.isPending && <LoadingState />}

      {files.error !== null && <ErrorState message={files.error.message} />}

      {files.data !== undefined && rows.length === 0 && (
        <Card withBorder padding="lg">
          <Stack gap="sm" align="center">
            <Video size={32} aria-hidden />
            <Text>{state === 'ignored' ? 'No video is ignored.' : 'Every video in the folder is matched'}</Text>
            {state !== 'ignored' && (
              <Button variant="default" loading={scan.isPending} onClick={startScan}>
                Scan now
              </Button>
            )}
          </Stack>
        </Card>
      )}

      {rows.map((row) => (
        <FileCard
          key={String(row.id)}
          row={row}
          state={state}
          selected={selected.has(Number(row.id))}
          busy={workingId === Number(row.id)}
          onSelect={(on) => toggle(Number(row.id), on)}
          onUse={(candidate) =>
            pick(
              { row, songId: Number(candidate.songId), title: candidate.title ?? 'that song' },
              candidate.songHasVideo,
            )
          }
          onSearch={() => setSearching(Number(row.id))}
          onIgnore={() => settle(Number(row.id), { id: Number(row.id), ignore: true }, `Ignored ${row.path}`)}
          onUnignore={() => settle(Number(row.id), { id: Number(row.id), unignore: true }, `Put ${row.path} back`)}
        />
      ))}

      {pages > 1 && (
        <Group justify="center">
          <Pagination
            total={pages}
            value={Math.min(paging.page, pages)}
            onChange={(page) => setPaging((current) => ({ ...current, page }))}
          />
        </Group>
      )}

      {searchingRow !== null && (
        <SongSearchModal
          row={searchingRow}
          onClose={() => setSearching(null)}
          onPick={(song) => pick({ row: searchingRow, songId: Number(song.id), title: song.title }, song.hasVideo)}
        />
      )}

      <Modal opened={confirming !== null} onClose={() => setConfirming(null)} title="Replace the video?" centered>
        {confirming !== null && (
          <Stack gap="md">
            <Text size="sm">Replace that song&apos;s video with this file?</Text>
            <Text size="sm" c="dimmed">
              {confirming.title} has a video already. It goes to the recycle bin. {confirming.row.path} stays where it
              is.
            </Text>
            <Group justify="flex-end" gap="xs">
              <Button variant="default" onClick={() => setConfirming(null)}>
                Cancel
              </Button>
              <Button
                color="red"
                loading={resolve.isPending}
                onClick={() =>
                  settle(
                    Number(confirming.row.id),
                    { id: Number(confirming.row.id), songId: confirming.songId, replace: true },
                    `Replaced the video of ${confirming.title}`,
                  )
                }
              >
                Replace
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
