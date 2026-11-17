import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Drawer,
  Group,
  Image,
  Modal,
  Select,
  Skeleton,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { CircleAlert, Disc3, EyeOff, Plus, Radio, RefreshCw, Undo2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import {
  DISCOVER_MAX_LIMIT,
  DISCOVER_PAGE_SIZE,
  DISCOVER_QUERY_KEY,
  useDiscover,
  useDiscoverDismissals,
  useDismissSuggestion,
  useUndoDismissal,
  type DiscoverResource,
  type DiscoverSource,
  type DiscoverSuggestion,
} from '../api/discover';
import { useLibraryChoice, type LibraryChoice } from '../api/libraryChoice';
import { useQualityProfiles } from '../api/profiles';
import { candidateAddInput, useAddSong, useSong, useSongs, useSongSearch, type SongLookupResource } from '../api/songs';
import { useQueryClient } from '@tanstack/react-query';
import { EmptyState } from '../components/DataState';
import { LibraryPicker } from '../components/LibraryPicker';
import { PreviewButton } from '../components/PreviewButton';
import { formatDuration } from '../components/SongCells';
import { CandidateRow } from './AddSongsPage';

/**
 * Discover: suggestions one at a time, from a mix of the whole library or a radio from one song.
 * Each can be previewed, added, or turned down. See ADR-0013.
 */

const SOURCE_LABELS: Record<string, string> = {
  listenbrainz: 'ListenBrainz',
  lastfm: 'Last.fm',
  deezer: 'Deezer',
};

function sourceLabel(name: string): string {
  return SOURCE_LABELS[name] ?? name;
}

/** The largest mix seed the API takes (an int32). */
const MAX_SEED = 2_147_483_647;

function randomSeed(): number {
  return Math.floor(Math.random() * MAX_SEED);
}

/** The mix seed the URL carries, or `null` when it carries none the API could read. */
function mixSeedOf(search: URLSearchParams): number | null {
  const value = search.get('mix');

  return value !== null && /^\d{1,10}$/.test(value) && Number(value) <= MAX_SEED ? Number(value) : null;
}

/** What names a suggestion for hiding and keying: its MBID, else its Deezer id, else its text. */
function suggestionKey(suggestion: DiscoverSuggestion): string {
  if (suggestion.mbRecordingId !== null && suggestion.mbRecordingId !== '') {
    return `mb:${suggestion.mbRecordingId}`;
  }

  if (suggestion.deezerId !== null) {
    return `deezer:${String(suggestion.deezerId)}`;
  }

  return `text:${suggestion.artist}\u0000${suggestion.title}`;
}

/** The cover a row (56 px) or the radio header (72 px) shows. */
function Cover({ url, size = 56 }: { url: string | null; size?: number }) {
  const [failed, setFailed] = useState(false);

  if (url === null || url === '' || failed) {
    return <Disc3 size={size} aria-hidden />;
  }

  return <Image src={url} alt="" w={size} h={size} radius="sm" fit="cover" onError={() => setFailed(true)} />;
}

/** The notices about sources that did not contribute, each with what the user can do about it. */
function SourcesNotice({ sources }: { sources: DiscoverSource[] }) {
  const lastFmOff = sources.some((source) => source.name === 'lastfm' && source.status === 'skipped');
  const failed = sources.filter((source) => source.status === 'failed').map((source) => sourceLabel(source.name));

  if (!lastFmOff && failed.length === 0) {
    return null;
  }

  return (
    <Stack gap="xs" aria-label="Sources">
      {lastFmOff && (
        <Alert color="gray" icon={<CircleAlert size={16} />}>
          Last.fm off: set a key in{' '}
          <Anchor component={Link} to="/settings/general">
            Settings → General
          </Anchor>
          .
        </Alert>
      )}
      {failed.length > 0 && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          {failed.join(' and ')} didn&apos;t answer, so this list is shorter than it could be. Refresh to try again.
        </Alert>
      )}
    </Stack>
  );
}

/** What the add buttons send: the profile the user picked, if any, and the library. */
interface AddChoices {
  profileId: number | null;
  library: LibraryChoice;
}

function addChoices(choices: AddChoices): { qualityProfileId: number | null; libraryId: number | null } {
  return {
    qualityProfileId: choices.profileId,
    // Only named when the user had a choice; one library is the API's own default anyway.
    libraryId: choices.library.visible && choices.library.value !== null ? Number(choices.library.value) : null,
  };
}

interface SuggestionRowProps {
  suggestion: DiscoverSuggestion;
  choices: AddChoices;
  onDismiss: (suggestion: DiscoverSuggestion) => void;
}

/** One suggestion: what it is, where it came from, and the four things the user can do with it. */
function SuggestionRow({ suggestion, choices, onDismiss }: SuggestionRowProps) {
  const add = useAddSong();
  const lookup = useSongSearch();
  const [addedSongId, setAddedSongId] = useState<number | null>(null);
  const [lookupOpen, setLookupOpen] = useState(false);

  const hasId = (suggestion.mbRecordingId !== null && suggestion.mbRecordingId !== '') || suggestion.deezerId !== null;

  const store = (candidate: { mbRecordingId: string | null; deezerId: number | string | null }) => {
    add.mutate(
      { ...candidateAddInput(candidate), ...addChoices(choices) },
      {
        onSuccess: (result) => {
          setAddedSongId(result.songId);
          setLookupOpen(false);
        },
      },
    );
  };

  const onAdd = () => {
    if (hasId) {
      store(suggestion);

      return;
    }

    setLookupOpen(true);
    lookup.mutate(`${suggestion.artist} - ${suggestion.title}`);
  };

  return (
    <Card withBorder padding="sm">
      <Group wrap="nowrap" align="center" gap="md">
        <Cover url={suggestion.coverUrl} />

        <Stack gap={4} flex={1} style={{ minWidth: 0 }}>
          <Text size="sm" fw={500}>
            {suggestion.title}
          </Text>
          <Group gap="md">
            <Text size="xs" c="dimmed">
              {suggestion.artist}
            </Text>
            {suggestion.album !== null && suggestion.album !== '' && (
              <Text size="xs" c="dimmed">
                {suggestion.album}
              </Text>
            )}
            {suggestion.durationMs !== null && (
              <Text size="xs" c="dimmed">
                {formatDuration(suggestion.durationMs)}
              </Text>
            )}
          </Group>
          <Group gap={4}>
            {suggestion.sources.map((name) => (
              <Badge key={name} size="xs" variant="outline">
                {sourceLabel(name)}
              </Badge>
            ))}
          </Group>
          {add.error !== null && !lookupOpen && (
            <Text size="xs" c="red">
              {add.error.message}
            </Text>
          )}
        </Stack>

        <Group gap="xs" wrap="nowrap">
          <PreviewButton
            deezerId={suggestion.deezerId}
            isrcs={suggestion.isrc === null ? [] : [suggestion.isrc]}
            artist={suggestion.artist}
            title={suggestion.title}
          />

          {addedSongId === null ? (
            <Button size="compact-sm" leftSection={<Plus size={14} />} loading={add.isPending} onClick={onAdd}>
              Add
            </Button>
          ) : (
            <Group gap={6} wrap="nowrap">
              <Text size="sm" c="dimmed">
                Added
              </Text>
              <Anchor component={Link} size="sm" to={`/song/${addedSongId}`}>
                Open song
              </Anchor>
            </Group>
          )}

          {addedSongId === null ? (
            <Tooltip label="Add the song to start a radio from it">
              <Button size="compact-sm" variant="default" leftSection={<Radio size={14} />} disabled>
                Radio from this
              </Button>
            </Tooltip>
          ) : (
            <Button
              size="compact-sm"
              variant="default"
              leftSection={<Radio size={14} />}
              component={Link}
              to={`/discover/${addedSongId}`}
            >
              Radio from this
            </Button>
          )}

          <Button
            size="compact-sm"
            variant="subtle"
            color="gray"
            leftSection={<EyeOff size={14} />}
            onClick={() => onDismiss(suggestion)}
          >
            Not interested
          </Button>
        </Group>
      </Group>

      <LookupModal
        opened={lookupOpen}
        suggestion={suggestion}
        candidates={lookup.data?.items ?? null}
        loading={lookup.isPending}
        error={lookup.error?.message ?? add.error?.message ?? null}
        pending={add.isPending}
        onClose={() => setLookupOpen(false)}
        onPick={store}
        onExisting={(songId) => {
          setAddedSongId(songId);
          setLookupOpen(false);
        }}
      />
    </Card>
  );
}

interface LookupModalProps {
  opened: boolean;
  suggestion: DiscoverSuggestion;
  candidates: SongLookupResource[] | null;
  loading: boolean;
  error: string | null;
  pending: boolean;
  onClose: () => void;
  onPick: (candidate: SongLookupResource) => void;
  onExisting: (songId: number) => void;
}

/** A suggestion with no id is looked up by name first; the user picks the recording to add. */
function LookupModal({
  opened,
  suggestion,
  candidates,
  loading,
  error,
  pending,
  onClose,
  onPick,
  onExisting,
}: LookupModalProps) {
  return (
    <Modal opened={opened} onClose={onClose} size="lg" title={`Add ${suggestion.title} – ${suggestion.artist}`}>
      <Stack gap="sm">
        {loading && (
          <Stack gap="xs" aria-label="Searching">
            <Skeleton height={64} />
            <Skeleton height={64} />
          </Stack>
        )}

        {error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {error}
          </Alert>
        )}

        {candidates !== null && candidates.length === 0 && <EmptyState message="No matches for this song." />}

        {(candidates ?? []).map((candidate) => (
          <CandidateRow
            key={candidate.mbRecordingId ?? `deezer:${String(candidate.deezerId)}`}
            candidate={candidate}
            action={
              candidate.existingSongId !== null ? (
                <Button
                  size="compact-sm"
                  variant="default"
                  onClick={() => onExisting(Number(candidate.existingSongId))}
                >
                  Already in library
                </Button>
              ) : (
                <Button size="compact-sm" loading={pending} onClick={() => onPick(candidate)}>
                  Add
                </Button>
              )
            }
          />
        ))}
      </Stack>
    </Modal>
  );
}

/** The skeleton rows shown while the list loads. */
function LoadingRows() {
  return (
    <Stack gap="xs" aria-label="Loading suggestions" aria-busy>
      {Array.from({ length: 6 }, (_, index) => (
        <Skeleton key={index} height={80} />
      ))}
    </Stack>
  );
}

/** Why a list came back empty, in terms of what the user can change. */
function emptyReason(data: DiscoverResource, radio: boolean): string {
  const answered = data.sources.some((source) => source.status === 'answered');

  if (!radio && data.seeds.length === 0) {
    return 'The mix is drawn from your library, and it has no songs yet. Add some first.';
  }

  if (!answered) {
    return 'No source answered, so there is nothing to suggest. Try again in a minute.';
  }

  return radio
    ? 'Nothing new for this song. What the sources suggested is already in your libraries or turned down.'
    : 'Nothing new in this mix. What the sources suggested is already in your libraries or turned down. Refresh for another draw.';
}

/** Picks a song of the library to start a radio from. */
function SongPicker({ onPick }: { onPick: (songId: string) => void }) {
  const [term, setTerm] = useState('');
  const [debounced] = useDebouncedValue(term, 250);
  const songs = useSongs(
    { page: 1, pageSize: 10, sortKey: '', sortDirection: 'descending' },
    { term: debounced.trim() === '' ? undefined : debounced.trim() },
  );

  const data =
    debounced.trim() === ''
      ? []
      : (songs.data?.records ?? []).map((song) => ({
          value: String(song.id),
          label: `${song.title} – ${song.artistCredit}`,
        }));

  return (
    <Select
      aria-label="Start a radio from a song"
      placeholder="Start a radio from a song…"
      searchable
      w={320}
      data={data}
      value={null}
      searchValue={term}
      onSearchChange={setTerm}
      filter={({ options }) => options}
      nothingFoundMessage={debounced.trim() === '' ? 'Type a title or an artist' : 'No songs match'}
      onChange={(value) => {
        if (value !== null) {
          onPick(value);
          setTerm('');
        }
      }}
    />
  );
}

/** The drawer of turned-down suggestions, each with an Undo. */
function DismissedDrawer() {
  const [opened, setOpened] = useState(false);
  const dismissals = useDiscoverDismissals();
  const undo = useUndoDismissal();
  const queryClient = useQueryClient();
  const rows = dismissals.data ?? [];

  return (
    <>
      <Button variant="default" size="xs" onClick={() => setOpened(true)}>
        Not interested ({rows.length})
      </Button>

      <Drawer opened={opened} onClose={() => setOpened(false)} position="right" title="Not interested">
        <Stack gap="xs">
          {rows.length === 0 && <EmptyState message="Nothing here. Songs you turn down show up in this list." />}

          {rows.map((dismissal) => (
            <Group key={String(dismissal.id)} justify="space-between" wrap="nowrap">
              <Stack gap={0} style={{ minWidth: 0 }}>
                <Text size="sm">{dismissal.title}</Text>
                <Text size="xs" c="dimmed">
                  {dismissal.artist}
                </Text>
              </Stack>
              <Button
                size="compact-sm"
                variant="default"
                leftSection={<Undo2 size={14} />}
                loading={undo.isPending && undo.variables === dismissal.id}
                onClick={() =>
                  undo.mutate(dismissal.id, {
                    onSuccess: () => void queryClient.invalidateQueries({ queryKey: DISCOVER_QUERY_KEY }),
                  })
                }
              >
                Undo
              </Button>
            </Group>
          ))}
        </Stack>
      </Drawer>
    </>
  );
}

interface DiscoverViewProps {
  /** The radio's seed song, or `null` for the mix. */
  songId: number | null;
  mixSeed: number | null;
  onRefresh: () => void;
  onPick: (songId: string) => void;
}

/** One list: the header, the controls, and the suggestions. Remounted for each seed, so its state is per list. */
function DiscoverView({ songId, mixSeed, onRefresh, onPick }: DiscoverViewProps) {
  const radio = songId !== null;
  const [limit, setLimit] = useState(DISCOVER_PAGE_SIZE);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set());
  const queryClient = useQueryClient();
  const dismiss = useDismissSuggestion();
  const undo = useUndoDismissal();
  const profiles = useQualityProfiles();
  const library = useLibraryChoice();
  const [profileId, setProfileId] = useState<string | null>(null);

  const waiting = !radio && mixSeed === null;
  const discover = useDiscover(waiting ? null : radio ? { songId, limit } : { mixSeed: mixSeed ?? undefined, limit });
  const data = discover.data;
  const seed = radio ? data?.seeds[0] : undefined;
  const seedSong = useSong(radio ? songId : null);

  const hide = (key: string, shown: boolean) =>
    setHidden((current) => {
      const next = new Set(current);

      if (shown) {
        next.delete(key);
      } else {
        next.add(key);
      }

      return next;
    });

  const onDismiss = (suggestion: DiscoverSuggestion) => {
    const key = suggestionKey(suggestion);
    hide(key, false);

    dismiss.mutate(
      { mbRecordingId: suggestion.mbRecordingId, artist: suggestion.artist, title: suggestion.title },
      {
        onSuccess: (dismissal) => {
          const id = `discover-dismissal-${String(dismissal.id)}`;

          notifications.show({
            id,
            autoClose: 8000,
            message: (
              <Group justify="space-between" wrap="nowrap">
                <Text size="sm">Not interested in {suggestion.title}</Text>
                <Button
                  size="compact-sm"
                  variant="light"
                  onClick={() => {
                    notifications.hide(id);
                    undo.mutate(dismissal.id, {
                      onSuccess: () => {
                        hide(key, true);
                        void queryClient.invalidateQueries({ queryKey: DISCOVER_QUERY_KEY });
                      },
                    });
                  }}
                >
                  Undo
                </Button>
              </Group>
            ),
          });
        },
        onError: (error) => {
          hide(key, true);
          notifications.show({ message: error.message, color: 'red' });
        },
      },
    );
  };

  const suggestions = (data?.suggestions ?? []).filter((suggestion) => !hidden.has(suggestionKey(suggestion)));
  const more = data !== undefined && Number(data.totalBeforeLimit) > Number(data.limit) && limit < DISCOVER_MAX_LIMIT;
  const choices: AddChoices = { profileId: profileId === null ? null : Number(profileId), library };

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center">
        {radio ? (
          <Group gap="md" align="center">
            <Cover url={seedSong.data?.albumContext?.coverUrl ?? null} size={72} />
            {data !== undefined && seed !== undefined ? (
              <Title order={2}>
                Radio: {seed.title} – {seed.artist}
              </Title>
            ) : (
              <Title order={2}>Radio</Title>
            )}
            <Button component={Link} to="/discover" variant="default" size="xs">
              Back to mix
            </Button>
          </Group>
        ) : (
          <Group gap="md" align="center">
            <Title order={2}>Mix from your library</Title>
            <Button variant="default" size="xs" leftSection={<RefreshCw size={14} />} onClick={onRefresh}>
              Refresh
            </Button>
          </Group>
        )}

        <Group gap="sm">
          <SongPicker onPick={onPick} />
          <DismissedDrawer />
        </Group>
      </Group>

      <Group align="flex-end" gap="sm">
        <Select
          label="Quality profile"
          placeholder="The default profile"
          clearable
          w={220}
          data={(profiles.data ?? []).map((profile) => ({ value: String(profile.id), label: profile.name }))}
          value={profileId}
          onChange={setProfileId}
        />
        <LibraryPicker choice={library} />
      </Group>

      {discover.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />} title="The suggestions could not be loaded">
          {discover.error.message}
        </Alert>
      )}

      {data !== undefined && <SourcesNotice sources={data.sources} />}

      {(waiting || discover.isPending) && discover.error === null && <LoadingRows />}

      {data !== undefined && suggestions.length === 0 && <EmptyState message={emptyReason(data, radio)} />}

      {suggestions.map((suggestion) => (
        <SuggestionRow
          key={suggestionKey(suggestion)}
          suggestion={suggestion}
          choices={choices}
          onDismiss={onDismiss}
        />
      ))}

      {more && (
        <Group justify="center">
          <Button variant="default" onClick={() => setLimit((current) => current + DISCOVER_PAGE_SIZE)}>
            Show more
          </Button>
        </Group>
      )}
    </Stack>
  );
}

/** The Discover page at `/discover` (the mix) and `/discover/:songId` (a radio). */
export function DiscoverPage() {
  const { songId } = useParams();
  const [search, setSearch] = useSearchParams();
  const navigate = useNavigate();

  const seedSong = songId !== undefined && /^\d+$/.test(songId) ? Number(songId) : null;
  const mixSeed = seedSong === null ? mixSeedOf(search) : null;

  // A mix needs a seed in the URL so a reload shows the same list; the first visit draws one.
  useEffect(() => {
    if (seedSong === null && mixSeed === null) {
      setSearch({ mix: String(randomSeed()) }, { replace: true });
    }
  }, [seedSong, mixSeed, setSearch]);

  return (
    <DiscoverView
      key={seedSong === null ? `mix:${String(mixSeed)}` : `radio:${seedSong}`}
      songId={seedSong}
      mixSeed={mixSeed}
      onRefresh={() => setSearch({ mix: String(randomSeed()) })}
      onPick={(id) => void navigate(`/discover/${id}`)}
    />
  );
}
