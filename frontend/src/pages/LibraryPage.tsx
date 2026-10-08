import {
  Alert,
  Badge,
  Button,
  Group,
  Menu,
  Modal,
  Radio,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core';
import { CircleAlert, EllipsisVertical, Plus } from 'lucide-react';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { useSongAlbum, type AlbumRefResource } from '../api/albums';
import { firstPage, type Paging } from '../api/paging';
import { readEnum, useLibraries } from '../api/profiles';
import {
  useAlbumOptions,
  useArtists,
  useDeleteSong,
  useSetAlbum,
  useSongs,
  useUpdateSong,
  type SongResource,
} from '../api/songs';
import { SongSearchButtons } from '../components/InteractiveSearchModal';
import { PagedTable, type PagedColumn } from '../components/PagedTable';
import { CoverThumb, formatDate, formatDuration } from '../components/SongCells';
import { ConvertSongModal, MoveSongModal } from './SongActionModals';
import { initCaps } from '../components/text';

/** Library: the songs Wondarr manages, with their album assignment and monitored flag. */

/** The wire spelling of `AlbumContextKind`; the API serialises enums as camelCase strings. */
type AlbumContextKindName = 'album' | 'single' | 'ep' | 'compilation' | 'pseudoSingles';

/** Which filter went the way of the monitored switch. */
type MonitoredFilter = 'all' | 'monitored' | 'unmonitored';

const MONITORED_FILTERS: { value: MonitoredFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'monitored', label: 'Monitored' },
  { value: 'unmonitored', label: 'Unmonitored' },
];

/** The album a song is filed under: a release title, or one of the two pseudo-albums. */
function AlbumCell({ song }: { song: SongResource }) {
  const context = song.albumContext;

  if (context === null) {
    return (
      <Text c="dimmed" size="sm">
        —
      </Text>
    );
  }

  if (readEnum<AlbumContextKindName>(context.kind) === 'pseudoSingles') {
    return (
      <Badge variant="light" size="sm">
        Singles
      </Badge>
    );
  }

  return (
    <Group gap={6} wrap="nowrap">
      <Text size="sm">{context.albumTitle}</Text>
      {context.isVariousArtists && (
        <Badge variant="light" size="sm">
          Various Artists
        </Badge>
      )}
    </Group>
  );
}

/** Title, with a badge per version flag the release carries (`live`, `remix`, …). */
function TitleCell({ song }: { song: SongResource }) {
  return (
    <Group gap={6} wrap="nowrap">
      <Text size="sm">{song.title}</Text>
      {song.versionFlags.map((flag) => (
        <Badge key={flag} variant="light" size="xs" color="grape">
          {initCaps(flag)}
        </Badge>
      ))}
    </Group>
  );
}

/**
 * The table's columns. The row actions are handed in rather than owned by the table, because the page
 * keeps a single dialog state for whichever song a menu was opened on.
 */
function libraryColumns(
  onUpdate: (id: number, monitored: boolean) => void,
  onOpenAlbum: (song: SongResource) => void,
  onOpenDelete: (song: SongResource) => void,
  onOpenMove: (song: SongResource) => void,
  onOpenConvert: (song: SongResource) => void,
  onRestOfAlbum: (album: AlbumRefResource) => void,
  canMove: boolean,
): PagedColumn<SongResource>[] {
  return [
    {
      label: 'Cover',
      sortKey: null,
      width: 64,
      render: (song) => <CoverThumb url={song.albumContext?.coverUrl ?? null} />,
    },
    { label: 'Title', sortKey: 'title', render: (song) => <TitleCell song={song} /> },
    { label: 'Artist', sortKey: 'artist', render: (song) => song.artistCredit },
    { label: 'Album', sortKey: 'album', render: (song) => <AlbumCell song={song} /> },
    { label: 'Duration', sortKey: null, width: 100, render: (song) => formatDuration(song.durationMs) },
    {
      label: 'Monitored',
      sortKey: null,
      width: 110,
      render: (song) => (
        <Switch
          checked={song.monitored}
          aria-label={`${song.title} monitored`}
          onChange={(event) => onUpdate(Number(song.id), event.currentTarget.checked)}
        />
      ),
    },
    { label: 'Added', sortKey: 'added', width: 120, render: (song) => formatDate(song.added) },
    {
      label: 'Search',
      sortKey: null,
      width: 100,
      render: (song) => (
        <SongSearchButtons
          songId={Number(song.id)}
          title={song.title}
          artistCredit={song.artistCredit}
          durationMs={song.durationMs === null ? null : Number(song.durationMs)}
        />
      ),
    },
    {
      label: 'Actions',
      sortKey: null,
      width: 60,
      render: (song) => (
        <SongMenu
          song={song}
          canMove={canMove}
          onOpenAlbum={onOpenAlbum}
          onOpenDelete={onOpenDelete}
          onOpenMove={onOpenMove}
          onOpenConvert={onOpenConvert}
          onRestOfAlbum={onRestOfAlbum}
        />
      ),
    },
  ];
}

/**
 * One row's menu. The release the song is pinned to is asked for only once the menu has been opened,
 * so a page of songs costs no extra requests until a menu is used; a song filed under a pseudo-album
 * has none, and then the item is simply absent.
 */
function SongMenu({
  song,
  canMove,
  onOpenAlbum,
  onOpenDelete,
  onOpenMove,
  onOpenConvert,
  onRestOfAlbum,
}: {
  song: SongResource;
  canMove: boolean;
  onOpenAlbum: (song: SongResource) => void;
  onOpenDelete: (song: SongResource) => void;
  onOpenMove: (song: SongResource) => void;
  onOpenConvert: (song: SongResource) => void;
  onRestOfAlbum: (album: AlbumRefResource) => void;
}) {
  const [opened, setOpened] = useState(false);
  const album = useSongAlbum(opened ? Number(song.id) : null);
  const albumRef = album.data;

  return (
    <Menu withinPortal>
      <Menu.Target>
        <Button
          variant="subtle"
          size="compact-sm"
          aria-label={`Actions for ${song.title}`}
          leftSection={<EllipsisVertical size={16} />}
          onClick={() => setOpened(true)}
        />
      </Menu.Target>
      <Menu.Dropdown>
        {albumRef != null && <Menu.Item onClick={() => onRestOfAlbum(albumRef)}>Add the rest of this album</Menu.Item>}
        <Menu.Item onClick={() => onOpenAlbum(song)}>Change album…</Menu.Item>
        {canMove && <Menu.Item onClick={() => onOpenMove(song)}>Move to library…</Menu.Item>}
        <Menu.Item onClick={() => onOpenConvert(song)}>Convert…</Menu.Item>
        <Menu.Item onClick={() => onOpenDelete(song)}>Delete</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/** The album picker: the releases a song could be filed under, and its artist's Singles album. */
function ChangeAlbumModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  const options = useAlbumOptions(opened && song !== null ? Number(song.id) : null);
  const setAlbum = useSetAlbum();

  const current = (options.data ?? []).find((option) => option.isCurrent)?.key ?? null;

  const choose = (key: string | null) => {
    if (key === null || song === null || key === current) {
      return;
    }

    setAlbum.mutate({ id: Number(song.id), albumKey: key }, { onSuccess: onClose });
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Change album">
      <Stack gap="md">
        {options.isPending && (
          <Text size="sm" c="dimmed">
            Loading the album options…
          </Text>
        )}

        {options.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {options.error.message}
          </Alert>
        )}

        {setAlbum.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {setAlbum.error.message}
          </Alert>
        )}

        <Radio.Group value={current} onChange={choose}>
          <Stack gap="xs">
            {(options.data ?? []).map((option) => (
              <Radio
                key={option.key}
                value={option.key}
                label={
                  <Group gap={6} wrap="nowrap">
                    <Text size="sm">{option.title}</Text>
                    {option.primaryType !== null && (
                      <Badge size="xs" variant="light">
                        {initCaps(option.primaryType)}
                      </Badge>
                    )}
                    {option.secondaryTypes.map((type) => (
                      <Badge key={type} size="xs" variant="light">
                        {initCaps(type)}
                      </Badge>
                    ))}
                    {option.date !== null && (
                      <Text size="xs" c="dimmed">
                        {option.date}
                      </Text>
                    )}
                  </Group>
                }
              />
            ))}
          </Stack>
        </Radio.Group>
      </Stack>
    </Modal>
  );
}

/** The confirmation before a song leaves the library. */
function DeleteSongModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  const remove = useDeleteSong();

  return (
    <Modal opened={opened} onClose={onClose} title="Delete song">
      <Stack gap="md">
        <Text size="sm">Delete “{song?.title ?? ''}” from the library? Any downloaded file is left where it is.</Text>

        {remove.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {remove.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button
            color="red"
            loading={remove.isPending}
            onClick={() => {
              if (song !== null) {
                remove.mutate(Number(song.id), { onSuccess: onClose });
              }
            }}
          >
            Delete
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function LibraryPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [artistId, setArtistId] = useState<string | null>(() => params.get('artistId'));
  const [monitored, setMonitored] = useState<MonitoredFilter>('all');
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [dialog, setDialog] = useState<{ song: SongResource; kind: 'album' | 'delete' | 'move' | 'convert' } | null>(
    null,
  );

  const artists = useArtists();
  const libraries = useLibraries();
  const update = useUpdateSong();
  const songs = useSongs(paging, {
    artistId: artistId === null ? undefined : Number(artistId),
    monitored: monitored === 'all' ? undefined : monitored === 'monitored',
  });

  const artistOptions = (artists.data ?? []).map((artist) => ({
    value: String(artist.id),
    label: artist.name,
  }));

  const canMove = (libraries.data ?? []).length > 1;

  const onRestOfAlbum = (album: AlbumRefResource) => {
    void navigate(`/add?album=${album.source}:${album.id}`);
  };

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Library</Title>
        <Button leftSection={<Plus size={16} />} onClick={() => void navigate('/add')}>
          Add songs
        </Button>
      </Group>

      <Group align="flex-end">
        <Select
          label="Artist"
          placeholder="Every artist"
          data={artistOptions}
          value={artistId}
          searchable
          clearable
          w={260}
          onChange={(value) => {
            setArtistId(value);
            setPaging((current) => ({ ...current, page: 1 }));
          }}
        />

        <SegmentedControl
          value={monitored}
          data={MONITORED_FILTERS.map((filter) => ({ value: filter.value, label: filter.label }))}
          onChange={(value) => {
            setMonitored(value === 'monitored' || value === 'unmonitored' ? value : 'all');
            setPaging((current) => ({ ...current, page: 1 }));
          }}
        />
      </Group>

      {update.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {update.error.message}
        </Alert>
      )}

      <PagedTable
        columns={libraryColumns(
          (id, wanted) => update.mutate({ id, monitored: wanted }),
          (song) => setDialog({ song, kind: 'album' }),
          (song) => setDialog({ song, kind: 'delete' }),
          (song) => setDialog({ song, kind: 'move' }),
          (song) => setDialog({ song, kind: 'convert' }),
          onRestOfAlbum,
          canMove,
        )}
        rows={songs.data?.records ?? []}
        totalRecords={Number(songs.data?.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={songs.isPending}
        error={songs.error}
        emptyMessage="Nothing in the library matches these filters."
        rowKey={(song) => song.id}
      />

      <ChangeAlbumModal
        song={dialog?.kind === 'album' ? dialog.song : null}
        opened={dialog?.kind === 'album'}
        onClose={() => setDialog(null)}
      />

      <DeleteSongModal
        song={dialog?.kind === 'delete' ? dialog.song : null}
        opened={dialog?.kind === 'delete'}
        onClose={() => setDialog(null)}
      />

      <MoveSongModal
        song={dialog?.kind === 'move' ? dialog.song : null}
        opened={dialog?.kind === 'move'}
        onClose={() => setDialog(null)}
      />

      <ConvertSongModal
        song={dialog?.kind === 'convert' ? dialog.song : null}
        opened={dialog?.kind === 'convert'}
        onClose={() => setDialog(null)}
      />
    </Stack>
  );
}
