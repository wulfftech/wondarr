import { Alert, Button, Card, Container, Stack, Tabs, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError } from '../api/errors';
import { useLibraries, useQualityDefinitions, useQualityProfiles } from '../api/profiles';
import { useSongSearchCommand } from '../api/queue';
import { useSong, useSongDetails, useUpdateSong } from '../api/songs';
import { EmptyState, ErrorState, LoadingState } from '../components/DataState';
import { InteractiveSearchPanel } from '../components/InteractiveSearchModal';
import { ChangeAlbumModal } from '../components/song/ChangeAlbumModal';
import { ConvertSongModal } from '../components/song/ConvertSongModal';
import { DeleteSongModal } from '../components/song/DeleteSongModal';
import { MoveSongModal } from '../components/song/MoveSongModal';
import { BlocklistTab, HistoryTab } from './ActivityPage';
import { QueuePage } from './QueuePage';
import { AboutTab } from './song/AboutTab';
import { FileTab } from './song/FileTab';
import { LastFmTab } from './song/LastFmTab';
import { LyricsTab } from './song/LyricsTab';
import { SongHeader } from './song/SongHeader';

/** The tabs under the header; Last.fm is only offered when the details carry a Last.fm section. */
type SongTab = 'file' | 'about' | 'lastfm' | 'lyrics' | 'history' | 'queue';

const TABS: SongTab[] = ['file', 'about', 'lastfm', 'lyrics', 'history', 'queue'];

/** The dialogs the header opens. */
type SongDialog = 'album' | 'convert' | 'move' | 'delete';

/** Sets the browser tab's title for as long as the page is shown. */
function useDocumentTitle(title: string | null) {
  useEffect(() => {
    if (title === null) {
      return undefined;
    }

    const previous = document.title;

    document.title = title;

    return () => {
      document.title = previous;
    };
  }, [title]);
}

/** Shown when no song has the id in the address. */
function SongNotFound() {
  return (
    <Container size="sm" py="xl">
      <Stack align="center" gap="sm">
        <Title order={2}>Song not found</Title>
        <Text c="dimmed">No song in the library has that id. It may have been deleted.</Text>
        <Button component={Link} to="/library" variant="light">
          Back to Library
        </Button>
      </Stack>
    </Container>
  );
}

/** One song's page: its header, its file, what the outside sources know, and its history. */
export function SongPage() {
  const { id } = useParams();
  const songId = id !== undefined && /^\d+$/.test(id) ? Number(id) : null;

  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const [dialog, setDialog] = useState<SongDialog | null>(null);
  const [interactiveOpen, setInteractiveOpen] = useState(false);

  const song = useSong(songId);
  const details = useSongDetails(songId);
  const profiles = useQualityProfiles();
  const libraries = useLibraries();
  const definitions = useQualityDefinitions();
  const update = useUpdateSong();
  const search = useSongSearchCommand();

  const data = song.data;

  useDocumentTitle(data === undefined ? null : `${data.artistCredit} - ${data.title} · Wondarr`);

  if (songId === null || (song.error instanceof ApiError && song.error.status === 404)) {
    return <SongNotFound />;
  }

  if (song.isPending) {
    return <LoadingState />;
  }

  if (song.error !== null || data === undefined) {
    return <ErrorState message={song.error?.message} />;
  }

  const lastFm = details.data?.lastFm ?? null;
  const requested = params.get('tab');
  const active: SongTab =
    TABS.includes(requested as SongTab) && (requested !== 'lastfm' || lastFm !== null)
      ? (requested as SongTab)
      : 'file';

  const profileList = profiles.data ?? [];
  const qualityNames = new Map((definitions.data ?? []).map((quality) => [String(quality.id), quality.name]));
  const ranks = new Map((definitions.data ?? []).map((quality) => [String(quality.id), Number(quality.rank)]));
  const profile = profileList.find((candidate) => String(candidate.id) === String(data.qualityProfileId));
  const fileRank = data.qualityId === null ? undefined : ranks.get(String(data.qualityId));
  const cutoffRank = profile === undefined ? undefined : ranks.get(String(profile.cutoff));
  const cutoffMet = data.hasFile && fileRank !== undefined && cutoffRank !== undefined ? fileRank >= cutoffRank : null;

  const startSearch = () =>
    search.mutate(songId, {
      onSuccess: () => notifications.show({ message: `Searching for ${data.title}`, color: 'blue' }),
      onError: (error: Error) => notifications.show({ message: error.message, color: 'red' }),
    });

  const openInteractive = () => setInteractiveOpen(true);

  const close = () => setDialog(null);

  return (
    <Stack gap="lg">
      <SongHeader
        song={data}
        details={details.data}
        profiles={profileList}
        libraries={libraries.data ?? []}
        cutoffMet={cutoffMet}
        searchOpen={interactiveOpen}
        onMonitored={(monitored) => update.mutate({ id: songId, monitored })}
        onProfile={(qualityProfileId) => update.mutate({ id: songId, qualityProfileId })}
        onSearch={startSearch}
        searching={search.isPending}
        onToggleInteractive={() => (interactiveOpen ? setInteractiveOpen(false) : openInteractive())}
        onChangeAlbum={() => setDialog('album')}
        onConvert={() => setDialog('convert')}
        onMove={() => setDialog('move')}
        onDelete={() => setDialog('delete')}
      />

      {update.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {update.error.message}
        </Alert>
      )}

      {interactiveOpen && (
        <Card withBorder padding="md" component="section" aria-label="Interactive search">
          <Stack gap="sm">
            <Title order={4}>Interactive search</Title>
            <InteractiveSearchPanel
              songId={songId}
              active
              songDurationMs={data.durationMs === null ? null : Number(data.durationMs)}
              onGrabbed={() => setInteractiveOpen(false)}
            />
          </Stack>
        </Card>
      )}

      <Tabs
        value={active}
        onChange={(value) => {
          const next = new URLSearchParams(params);

          if (value === null || value === 'file') {
            next.delete('tab');
          } else {
            next.set('tab', value);
          }

          setParams(next, { replace: true });
        }}
      >
        <Tabs.List>
          <Tabs.Tab value="file">File</Tabs.Tab>
          <Tabs.Tab value="about">About</Tabs.Tab>
          {lastFm !== null && <Tabs.Tab value="lastfm">Last.fm</Tabs.Tab>}
          <Tabs.Tab value="lyrics">Lyrics</Tabs.Tab>
          <Tabs.Tab value="history">History</Tabs.Tab>
          <Tabs.Tab value="queue">Queue &amp; blocklist</Tabs.Tab>
        </Tabs.List>

        {/* Only the open tab is mounted, so nothing a tab asks for is fetched before it is opened. */}
        <Tabs.Panel value="file" pt="md">
          {active === 'file' && (
            <FileTab
              song={data}
              qualityNames={qualityNames}
              onSearch={startSearch}
              onInteractiveSearch={openInteractive}
            />
          )}
        </Tabs.Panel>

        <Tabs.Panel value="about" pt="md">
          {active === 'about' && details.isPending && <LoadingState />}
          {active === 'about' && details.error !== null && <ErrorState message={details.error.message} />}
          {active === 'about' && details.data !== undefined && <AboutTab details={details.data} />}
          {active === 'about' && details.isSuccess && details.data === undefined && (
            <EmptyState message="Nothing to show." />
          )}
        </Tabs.Panel>

        {lastFm !== null && (
          <Tabs.Panel value="lastfm" pt="md">
            {active === 'lastfm' && <LastFmTab lastFm={lastFm} />}
          </Tabs.Panel>
        )}

        <Tabs.Panel value="lyrics" pt="md">
          {active === 'lyrics' && <LyricsTab songId={songId} />}
        </Tabs.Panel>

        <Tabs.Panel value="history" pt="md">
          {active === 'history' && <HistoryTab songId={songId} />}
        </Tabs.Panel>

        <Tabs.Panel value="queue" pt="md">
          {active === 'queue' && (
            <Stack gap="xl">
              <Stack gap="sm">
                <Title order={4}>Queue</Title>
                <QueuePage songId={songId} />
              </Stack>
              <Stack gap="sm">
                <Title order={4}>Blocklist</Title>
                <BlocklistTab songId={songId} />
              </Stack>
            </Stack>
          )}
        </Tabs.Panel>
      </Tabs>

      <ChangeAlbumModal song={data} opened={dialog === 'album'} onClose={close} />
      <ConvertSongModal song={data} opened={dialog === 'convert'} onClose={close} />
      <MoveSongModal song={data} opened={dialog === 'move'} onClose={close} />
      <DeleteSongModal
        song={data}
        opened={dialog === 'delete'}
        onClose={close}
        onDeleted={() => void navigate('/library')}
      />
    </Stack>
  );
}
