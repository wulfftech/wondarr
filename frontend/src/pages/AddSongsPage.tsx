import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Image,
  Progress,
  Select,
  Stack,
  Tabs,
  Text,
  Textarea,
  TextInput,
  Title,
} from '@mantine/core';
import { CircleAlert, Disc3, Plus } from 'lucide-react';
import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useQualityProfiles } from '../api/profiles';
import { readEnum } from '../api/profiles';
import { AddAlbumTab, type AlbumPrefill } from './AddAlbumTab';
import {
  candidateAddInput,
  useAddSong,
  useBulkAdd,
  useCommand,
  useImportList,
  useSongLookup,
  type BulkAddAcceptedResource,
  type CommandStatusName,
  type SongLookupResource,
} from '../api/songs';
import { EmptyState, LoadingState } from '../components/DataState';
import { PreviewButton } from '../components/PreviewButton';
import { formatDuration } from '../components/SongCells';

/**
 * Add songs: search for one with disambiguation, cover and an audition before adding it, or paste a
 * whole list and watch it resolve. The review screen lives at `/add/unresolved`.
 */

/** The longest list the paste box takes, matching the API's own limit. */
const MAX_LINES = 1000;

/** The `?album=source:id` query value as a prefill, or `null` when the URL does not carry one. */
function albumPrefill(search: URLSearchParams): AlbumPrefill | null {
  const value = search.get('album');

  if (value === null) {
    return null;
  }

  const colon = value.indexOf(':');

  return colon <= 0 ? null : { source: value.slice(0, colon), id: value.slice(colon + 1) };
}

/** The term the search box suggests, used as its placeholder. */
const SEARCH_PLACEHOLDER = 'Artist - Title, a MusicBrainz or Deezer link, or an ISRC';

/** The 64 px cover the search and review rows show. */
function LookupCover({ url }: { url: string | null }) {
  const [failed, setFailed] = useState(false);

  if (url === null || url === '' || failed) {
    return <Disc3 size={64} aria-hidden />;
  }

  return <Image src={url} alt="" w={64} h={64} radius="sm" fit="cover" onError={() => setFailed(true)} />;
}

/** The year a first-release date names, or `null` when it carries none. */
function releaseYear(date: string | null): string | null {
  return date !== null && /^\d{4}/.test(date) ? date.slice(0, 4) : null;
}

/** How a candidate is labelled: the provider that found it. */
function sourceLabel(source: string): string {
  return source === 'musicbrainz' ? 'MusicBrainz' : 'Deezer only';
}

/** One line of a lookup result: what it is, how it was found, and what the caller can do with it. */
export function CandidateRow({ candidate, action }: { candidate: SongLookupResource; action: ReactNode }) {
  const year = releaseYear(candidate.firstReleaseDate);

  return (
    <Card withBorder padding="sm">
      <Group wrap="nowrap" align="center" gap="md">
        <LookupCover url={candidate.coverUrl} />

        <Stack gap={4} flex={1}>
          <Group gap={6} wrap="nowrap">
            <Text size="sm" fw={500}>
              {candidate.title}
            </Text>
            {candidate.disambiguation !== null && candidate.disambiguation !== '' && (
              <Text size="xs" c="dimmed">
                {candidate.disambiguation}
              </Text>
            )}
          </Group>

          <Group gap="md">
            <Text size="xs" c="dimmed">
              {candidate.artistCredit}
            </Text>
            <Text size="xs" c="dimmed">
              {formatDuration(candidate.durationMs)}
            </Text>
            {year !== null && (
              <Text size="xs" c="dimmed">
                {year}
              </Text>
            )}
          </Group>

          <Group gap={4}>
            {candidate.releaseTypes.map((type) => (
              <Badge key={type} size="xs" variant="light">
                {type}
              </Badge>
            ))}
            {candidate.versionFlags.map((flag) => (
              <Badge key={flag} size="xs" variant="light" color="grape">
                {flag}
              </Badge>
            ))}
            <Badge size="xs" variant="outline">
              {sourceLabel(candidate.source)}
            </Badge>
          </Group>
        </Stack>

        <PreviewButton deezerId={candidate.deezerId} isrcs={candidate.isrcs} />

        {action}
      </Group>
    </Card>
  );
}

/** The "In library" link a row shows once its song is stored. */
function LibraryLink({ songId, artistId }: { songId: number; artistId: number | null }) {
  return (
    <Stack gap={2} align="flex-end">
      <Text size="xs" c="dimmed">
        In library
      </Text>
      <Text component={Link} size="sm" to={artistId === null ? '/library' : `/library?artistId=${artistId}`}>
        #{songId}
      </Text>
    </Stack>
  );
}

/** The search tab: one term, the ranked candidates, and an Add that never double-adds. */
function SearchTab() {
  const lookup = useSongLookup();
  const add = useAddSong();
  const [term, setTerm] = useState('');
  const [added, setAdded] = useState<Map<string, { songId: number; artistId: number | null }>>(new Map());

  const submit = (event: FormEvent) => {
    event.preventDefault();

    if (term.trim() !== '') {
      lookup.mutate(term);
    }
  };

  const onAdd = (candidate: SongLookupResource) => {
    add.mutate(candidateAddInput(candidate), {
      onSuccess: (result) =>
        setAdded((current) =>
          new Map(current).set(candidate.mbRecordingId ?? `deezer:${String(candidate.deezerId)}`, {
            songId: result.songId,
            artistId: result.primaryArtistId,
          }),
        ),
    });
  };

  const rows = lookup.data ?? [];

  return (
    <Stack gap="md">
      <form onSubmit={submit}>
        <Group align="flex-end">
          <TextInput
            label="Search"
            placeholder={SEARCH_PLACEHOLDER}
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
        <Alert color="red" icon={<CircleAlert size={16} />} title="That search could not be run">
          {lookup.error.message}
        </Alert>
      )}

      {add.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {add.error.message}
        </Alert>
      )}

      {lookup.isPending && <LoadingState label="Searching…" />}

      {lookup.isSuccess && rows.length === 0 && <EmptyState message="No matches — try 'Artist - Title'" />}

      {rows.map((candidate) => {
        const key = candidate.mbRecordingId ?? `deezer:${String(candidate.deezerId)}`;
        const stored = added.get(key);
        const existingSongId = candidate.existingSongId === null ? null : Number(candidate.existingSongId);

        if (stored !== undefined) {
          return (
            <CandidateRow
              key={key}
              candidate={candidate}
              action={<LibraryLink songId={stored.songId} artistId={stored.artistId} />}
            />
          );
        }

        if (existingSongId !== null) {
          return (
            <CandidateRow
              key={key}
              candidate={candidate}
              action={<LibraryLink songId={existingSongId} artistId={null} />}
            />
          );
        }

        return (
          <CandidateRow
            key={key}
            candidate={candidate}
            action={
              <Button size="compact-sm" onClick={() => onAdd(candidate)} loading={add.isPending}>
                Add
              </Button>
            }
          />
        );
      })}
    </Stack>
  );
}

/** What a progress message such as "Resolved 12 of 50 lines" says, or `null` when it says nothing. */
function parseProgress(message: string | null | undefined): { done: number; total: number } | null {
  const match = /(\d+)\s+of\s+(\d+)/.exec(message ?? '');

  return match === null ? null : { done: Number(match[1]), total: Number(match[2]) };
}

/** The paste tab: a block of lines, the profile they land under, and the resolve's progress. */
function PasteTab() {
  const navigate = useNavigate();
  const bulk = useBulkAdd();
  const profiles = useQualityProfiles();
  const [text, setText] = useState('');
  const [profileId, setProfileId] = useState<string | null>(null);
  const [accepted, setAccepted] = useState<BulkAddAcceptedResource | null>(null);

  const command = useCommand(accepted === null ? null : Number(accepted.commandId), { poll: true });
  const list = useImportList(accepted === null ? null : Number(accepted.importListId));

  const lines = text.split('\n').filter((line) => line.trim() !== '').length;
  const tooMany = lines > MAX_LINES;

  const firstProfile = profiles.data?.[0];
  const selectedProfile = profileId ?? (firstProfile === undefined ? null : String(firstProfile.id));

  const progress = parseProgress(command.data?.message);
  const status = readEnum<CommandStatusName>(command.data?.status);
  const running = command.data !== undefined && status !== 'completed' && status !== 'failed' && status !== 'aborted';
  const counts = list.data?.counts;

  // The summary is first read while the command is still queued (every line pending); read it again
  // once the command has finished, or the counts and the review button would stay stale.
  const finished = command.data !== undefined && !running;
  const refetchList = list.refetch;
  useEffect(() => {
    if (finished) {
      void refetchList();
    }
  }, [finished, refetchList]);

  const submit = () => {
    bulk.mutate(
      { text, qualityProfileId: selectedProfile === null ? null : Number(selectedProfile) },
      { onSuccess: setAccepted },
    );
  };

  return (
    <Stack gap="md">
      <Textarea
        label="Songs"
        placeholder="Artist - Title, one per line"
        minRows={8}
        value={text}
        error={tooMany ? `At most ${MAX_LINES} lines; this one has ${lines}.` : undefined}
        onChange={(event) => setText(event.currentTarget.value)}
      />

      <Group>
        <Text size="sm" c={tooMany ? 'red' : 'dimmed'}>
          {lines === 1 ? '1 line' : `${lines} lines`}
        </Text>
      </Group>

      <Select
        label="Quality profile"
        placeholder="The default profile"
        data={(profiles.data ?? []).map((profile) => ({
          value: String(profile.id),
          label: profile.name,
        }))}
        value={selectedProfile}
        allowDeselect={false}
        w={260}
        onChange={setProfileId}
      />

      <Group justify="flex-end">
        <Button
          leftSection={<Plus size={16} />}
          disabled={lines === 0 || tooMany}
          loading={bulk.isPending}
          onClick={submit}
        >
          Add all
        </Button>
      </Group>

      {bulk.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {bulk.error.message}
        </Alert>
      )}

      {accepted !== null && (
        <Card withBorder padding="md">
          <Stack gap="sm">
            <Text fw={600}>{running ? 'Resolving…' : 'Resolved'}</Text>

            {command.data?.message !== null && command.data?.message !== undefined && (
              <Text size="sm">{command.data.message}</Text>
            )}

            {progress !== null && (
              <Progress
                value={progress.total === 0 ? 0 : Math.round((progress.done / progress.total) * 100)}
                aria-label="Resolve progress"
              />
            )}

            {command.error !== null && (
              <Alert color="red" icon={<CircleAlert size={16} />}>
                {command.error.message}
              </Alert>
            )}

            {!running && counts !== undefined && (
              <Stack gap={2}>
                <Text size="sm">Added, including ones the library already held: {counts.added}</Text>
                <Text size="sm">Unresolved: {counts.unresolved}</Text>
                <Text size="sm">Skipped: {counts.skipped}</Text>
                <Text size="sm">Still to look at: {counts.pending}</Text>
              </Stack>
            )}

            {!running && counts !== undefined && Number(counts.unresolved) > 0 && (
              <Group>
                <Button
                  variant="light"
                  onClick={() => void navigate(`/add/unresolved?importListId=${String(accepted.importListId)}`)}
                >
                  Review {counts.unresolved} unresolved
                </Button>
              </Group>
            )}
          </Stack>
        </Card>
      )}
    </Stack>
  );
}

/** Add songs: search one at a time, open an album's tracklist, or paste a list and review it. */
export function AddSongsPage() {
  const [search] = useSearchParams();
  const prefill = albumPrefill(search);

  return (
    <Stack gap="lg">
      <Title order={2}>Add songs</Title>

      <Tabs defaultValue={prefill === null ? 'search' : 'album'}>
        <Tabs.List>
          <Tabs.Tab value="search">Search</Tabs.Tab>
          <Tabs.Tab value="album">Album</Tabs.Tab>
          <Tabs.Tab value="paste">Paste a list</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="search" pt="md">
          <SearchTab />
        </Tabs.Panel>

        <Tabs.Panel value="album" pt="md">
          <AddAlbumTab prefill={prefill} />
        </Tabs.Panel>

        <Tabs.Panel value="paste" pt="md">
          <PasteTab />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
