import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Menu,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { CircleAlert, EllipsisVertical, Plus, Search, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { useSongAlbum, type AlbumRefResource } from '../api/albums';
import { filterEntries, useCustomFilters, type CustomFilterResource } from '../api/customFilters';
import { firstPage, type Paging } from '../api/paging';
import { readEnum, useLibraries, useQualityProfiles } from '../api/profiles';
import { useArtists, useSongs, useUpdateSong, type SongResource } from '../api/songs';
import { useTags } from '../api/tags';
import { SongSearchButtons } from '../components/InteractiveSearchModal';
import { PagedTable, type PagedColumn } from '../components/PagedTable';
import { CoverThumb, formatDate, formatDuration } from '../components/SongCells';
import { ChangeAlbumModal } from '../components/song/ChangeAlbumModal';
import { ConvertSongModal } from '../components/song/ConvertSongModal';
import { DeleteSongModal } from '../components/song/DeleteSongModal';
import { MoveSongModal } from '../components/song/MoveSongModal';
import { SongLink } from '../components/song/SongLink';
import { initCaps } from '../components/text';
import {
  fromViewEntries,
  isUnfiltered,
  NO_FILTERS,
  toSongFilters,
  toViewEntries,
  type CutoffFilter,
  type FileFilter,
  type LibraryFilters,
  type MonitoredFilter,
} from './library/filters';
import { MassEditorBar } from './library/MassEditorBar';
import { LIBRARY_VIEW_TYPE, SavedViewsMenu } from './library/SavedViewsMenu';

/** Library: the songs Wondarr manages, with their album assignment and monitored flag. */

/** The wire spelling of `AlbumContextKind`; the API serialises enums as camelCase strings. */
type AlbumContextKindName = 'album' | 'single' | 'ep' | 'compilation' | 'pseudoSingles';

/** How long the search box waits after a keystroke before it asks the server, in milliseconds. */
const SEARCH_DEBOUNCE_MS = 300;

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
      <SongLink songId={song.id}>{song.title}</SongLink>
      {song.versionFlags.map((flag) => (
        <Badge key={flag} variant="light" size="xs" color="grape">
          {initCaps(flag)}
        </Badge>
      ))}
    </Group>
  );
}

/** What the checkbox column needs from the page. */
interface SelectionControl {
  /** The songs on the current page. */
  rows: SongResource[];
  /** The ids selected, across pages. */
  selected: ReadonlySet<number>;
  /** A row's checkbox was clicked; `shift` asks for the range from the last one clicked. */
  onToggle: (index: number, shift: boolean) => void;
  /** The header checkbox: select (or deselect) every song on the page. */
  onTogglePage: (selectAll: boolean) => void;
}

/** The tags a song carries, as badges that filter by the tag when clicked. */
function TagsCell({ song, onTag }: { song: SongResource; onTag: (tag: string) => void }) {
  return (
    <Group gap={4} wrap="wrap">
      {(song.tags ?? []).map((tag) => (
        <Badge
          key={tag}
          component="button"
          type="button"
          variant="light"
          size="sm"
          color="teal"
          style={{ cursor: 'pointer' }}
          aria-label={`Filter by tag ${tag}`}
          onClick={() => onTag(tag)}
        >
          {tag}
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
  selection: SelectionControl,
  showTags: boolean,
  onTag: (tag: string) => void,
): PagedColumn<SongResource>[] {
  const onPage = selection.rows.filter((song) => selection.selected.has(Number(song.id))).length;

  return [
    {
      label: 'Select',
      sortKey: null,
      width: 40,
      header: (
        <Checkbox
          aria-label="Select all on this page"
          checked={selection.rows.length > 0 && onPage === selection.rows.length}
          indeterminate={onPage > 0 && onPage < selection.rows.length}
          onChange={(event) => selection.onTogglePage(event.currentTarget.checked)}
        />
      ),
      render: (song) => {
        const index = selection.rows.indexOf(song);

        return (
          <Checkbox
            aria-label={`Select ${song.title}`}
            checked={selection.selected.has(Number(song.id))}
            // The click carries the shift key; the change event does not, so the click does the work.
            onClick={(event) => selection.onToggle(index, event.shiftKey)}
            onChange={() => undefined}
          />
        );
      },
    },
    {
      label: 'Cover',
      sortKey: null,
      width: 64,
      render: (song) => <CoverThumb url={song.albumContext?.coverUrl ?? null} />,
    },
    { label: 'Title', sortKey: 'title', render: (song) => <TitleCell song={song} /> },
    { label: 'Artist', sortKey: 'artist', render: (song) => song.artistCredit },
    { label: 'Album', sortKey: 'album', render: (song) => <AlbumCell song={song} /> },
    ...(showTags
      ? [{ label: 'Tags', sortKey: null, render: (song: SongResource) => <TagsCell song={song} onTag={onTag} /> }]
      : []),
    { label: 'Duration', sortKey: null, width: 100, render: (song) => formatDuration(song.durationMs) },
    {
      label: 'Monitored',
      sortKey: 'monitored',
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

const FILE_OPTIONS: { value: FileFilter; label: string }[] = [
  { value: 'any', label: 'Any file state' },
  { value: 'has', label: 'Has a file' },
  { value: 'missing', label: 'Missing its file' },
];

const CUTOFF_OPTIONS: { value: CutoffFilter; label: string }[] = [
  { value: 'any', label: 'Any cutoff state' },
  { value: 'met', label: 'Cutoff met' },
  { value: 'unmet', label: 'Cutoff not met' },
];

export function LibraryPage() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  // The filters live in the URL, so coming back from a song's page (or reloading) finds them again.
  const [filters, setFilters] = useState<LibraryFilters>(() =>
    fromViewEntries([...params.entries()].map(([key, value]) => ({ key, value }))),
  );
  const [termInput, setTermInput] = useState(filters.term);
  const [activeViewId, setActiveViewId] = useState<number | null>(null);
  const [paging, setPaging] = useState<Paging>(() => firstPage());
  const [selected, setSelected] = useState<ReadonlySet<number>>(() => new Set());
  const [lastClicked, setLastClicked] = useState<number | null>(null);
  const [dialog, setDialog] = useState<{ song: SongResource; kind: 'album' | 'delete' | 'move' | 'convert' } | null>(
    null,
  );

  useEffect(() => {
    const next = new URLSearchParams();

    for (const { key, value } of toViewEntries(filters)) {
      next.set(key, value);
    }

    if (next.toString() !== params.toString()) {
      setParams(next, { replace: true });
    }
    // Only a change of filters writes the URL; the URL itself is read once, on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filters]);

  const artists = useArtists();
  const libraries = useLibraries();
  const profiles = useQualityProfiles();
  const tags = useTags();
  const views = useCustomFilters(LIBRARY_VIEW_TYPE);
  const update = useUpdateSong();
  const songs = useSongs(paging, toSongFilters(filters));

  /** Any change of filters starts again from page 1 with nothing selected. */
  const changeFilters = (change: (current: LibraryFilters) => LibraryFilters, fromView: number | null = null) => {
    setFilters(change);
    setActiveViewId(fromView);
    setPaging((current) => ({ ...current, page: 1 }));
    setSelected(new Set());
    setLastClicked(null);
  };

  // The search box waits for a pause in typing before it asks the server.
  useEffect(() => {
    const timer = setTimeout(() => {
      if (termInput === filters.term) {
        return;
      }

      changeFilters((current) => ({ ...current, term: termInput }));
    }, SEARCH_DEBOUNCE_MS);

    return () => clearTimeout(timer);
    // Only a keystroke starts the wait; the other values are read as they are when it fires.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [termInput]);

  const artistOptions = (artists.data ?? []).map((artist) => ({
    value: String(artist.id),
    label: artist.name,
  }));

  const libraryList = libraries.data ?? [];
  const profileList = profiles.data ?? [];
  const tagList = (tags.data ?? []).map((tag) => tag.label);
  const canMove = libraryList.length > 1;
  const rows = songs.data?.records ?? [];
  const showTags = rows.some((song) => (song.tags ?? []).length > 0);
  const viewList = views.data ?? [];
  const activeView = viewList.find((view) => Number(view.id) === activeViewId) ?? null;

  const onRestOfAlbum = (album: AlbumRefResource) => {
    void navigate(`/add?album=${album.source}:${album.id}`);
  };

  const applyView = (view: CustomFilterResource) => {
    const next = fromViewEntries(filterEntries(view.filters));

    setTermInput(next.term);
    changeFilters(() => next, Number(view.id));
  };

  const clearFilters = () => {
    setTermInput('');
    changeFilters(() => NO_FILTERS);
  };

  const selection: SelectionControl = {
    rows,
    selected,
    onToggle: (index, shift) => {
      const song = rows[index];

      if (song === undefined) {
        return;
      }

      const wanted = !selected.has(Number(song.id));
      const from = shift && lastClicked !== null ? Math.min(lastClicked, index) : index;
      const to = shift && lastClicked !== null ? Math.max(lastClicked, index) : index;
      const next = new Set(selected);

      for (const covered of rows.slice(from, to + 1)) {
        if (wanted) {
          next.add(Number(covered.id));
        } else {
          next.delete(Number(covered.id));
        }
      }

      setSelected(next);
      setLastClicked(index);
    },
    onTogglePage: (selectAll) => {
      const next = new Set(selected);

      for (const song of rows) {
        if (selectAll) {
          next.add(Number(song.id));
        } else {
          next.delete(Number(song.id));
        }
      }

      setSelected(next);
    },
  };

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>Library</Title>
        <Group>
          <SavedViewsMenu
            views={viewList}
            active={activeView}
            current={toViewEntries(filters)}
            onApply={applyView}
            onSaved={(view) => setActiveViewId(Number(view.id))}
            onDeleted={() => setActiveViewId(null)}
          />
          <Button leftSection={<Plus size={16} />} onClick={() => void navigate('/add')}>
            Add songs
          </Button>
        </Group>
      </Group>

      <Group align="flex-end">
        <TextInput
          label="Search"
          placeholder="Title or artist"
          leftSection={<Search size={14} />}
          w={220}
          value={termInput}
          onChange={(event) => setTermInput(event.currentTarget.value)}
        />

        <Select
          label="Artist"
          placeholder="Every artist"
          data={artistOptions}
          value={filters.artistId}
          searchable
          clearable
          w={220}
          onChange={(value) => changeFilters((current) => ({ ...current, artistId: value }))}
        />

        <Select
          label="Library"
          placeholder="Every library"
          data={libraryList.map((library) => ({ value: String(library.id), label: library.name }))}
          value={filters.libraryId}
          clearable
          w={160}
          onChange={(value) => changeFilters((current) => ({ ...current, libraryId: value }))}
        />

        <Select
          label="Quality profile"
          placeholder="Every profile"
          data={profileList.map((profile) => ({ value: String(profile.id), label: profile.name }))}
          value={filters.qualityProfileId}
          clearable
          w={170}
          onChange={(value) => changeFilters((current) => ({ ...current, qualityProfileId: value }))}
        />

        <Select
          label="File"
          data={FILE_OPTIONS}
          value={filters.file}
          allowDeselect={false}
          w={170}
          onChange={(value) =>
            changeFilters((current) => ({
              ...current,
              file: value === 'has' || value === 'missing' ? value : 'any',
            }))
          }
        />

        <Select
          label="Cutoff"
          data={CUTOFF_OPTIONS}
          value={filters.cutoff}
          allowDeselect={false}
          w={170}
          onChange={(value) =>
            changeFilters((current) => ({
              ...current,
              cutoff: value === 'met' || value === 'unmet' ? value : 'any',
            }))
          }
        />

        <Select
          label="Tag"
          placeholder="Every tag"
          data={tagList}
          value={filters.tag}
          searchable
          clearable
          w={160}
          onChange={(value) => changeFilters((current) => ({ ...current, tag: value }))}
        />

        <SegmentedControl
          value={filters.monitored}
          data={MONITORED_FILTERS.map((filter) => ({ value: filter.value, label: filter.label }))}
          onChange={(value) =>
            changeFilters((current) => ({
              ...current,
              monitored: value === 'monitored' || value === 'unmonitored' ? value : 'all',
            }))
          }
        />

        <Button variant="subtle" leftSection={<X size={14} />} disabled={isUnfiltered(filters)} onClick={clearFilters}>
          Clear
        </Button>
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
          selection,
          showTags,
          (tag) => changeFilters((current) => ({ ...current, tag })),
        )}
        rows={rows}
        totalRecords={Number(songs.data?.totalRecords ?? 0)}
        paging={paging}
        onPaging={setPaging}
        isLoading={songs.isPending}
        error={songs.error}
        emptyMessage="Nothing in the library matches these filters."
        rowKey={(song) => song.id}
      />

      <MassEditorBar
        selectedIds={[...selected]}
        profiles={profileList}
        libraries={libraryList}
        knownTags={tagList}
        onClear={() => {
          setSelected(new Set());
          setLastClicked(null);
        }}
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
