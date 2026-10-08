import { Group, Stack, Switch, Tabs, Title } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { firstPage, type Paging } from '../api/paging';
import { useQualityProfiles } from '../api/profiles';
import { useWantedCutoff, useWantedMissing, type SongPage, type SongResource } from '../api/wanted';
import { SongSearchButtons } from '../components/InteractiveSearchModal';
import { PagedTable, type PagedColumn } from '../components/PagedTable';
import { CoverThumb, formatDate, formatDuration } from '../components/SongCells';

/** The two tabs of the Wanted page, and the route segment each one owns. */
type WantedTab = 'missing' | 'cutoff';

/** The `localStorage` key the cover-art toggle is remembered under. */
const SHOW_COVERS_KEY = 'wondarr.wanted.showCovers';

/**
 * Whether the tables show cover art: on unless the user turned it off. Remembered in this browser;
 * with no storage (a private window) it is on again after a reload, which is the safe way to fail.
 */
function useShowCovers(): [boolean, (show: boolean) => void] {
  const [show, setShow] = useState(() => {
    try {
      return localStorage.getItem(SHOW_COVERS_KEY) !== '0';
    } catch {
      return true;
    }
  });

  const update = (next: boolean) => {
    setShow(next);

    try {
      localStorage.setItem(SHOW_COVERS_KEY, next ? '1' : '0');
    } catch {
      // No storage: the choice lasts until the page is reloaded.
    }
  };

  return [show, update];
}

/**
 * The columns both tabs share. `profileNames` turns a profile id into the name the user knows;
 * `showCovers` adds the cover column.
 */
function wantedColumns(profileNames: Map<string, string>, showCovers: boolean): PagedColumn<SongResource>[] {
  const cover: PagedColumn<SongResource> = {
    label: 'Cover',
    sortKey: null,
    width: 64,
    render: (song) => <CoverThumb url={song.albumContext?.coverUrl ?? null} />,
  };

  return [
    ...(showCovers ? [cover] : []),
    { label: 'Title', sortKey: 'title', render: (song) => song.title },
    { label: 'Artist', sortKey: 'artist', render: (song) => song.artistCredit },
    { label: 'Album', sortKey: null, render: (song) => song.albumContext?.albumTitle ?? '—' },
    { label: 'Duration', sortKey: null, width: 100, render: (song) => formatDuration(song.durationMs) },
    {
      label: 'Quality profile',
      sortKey: null,
      render: (song) => profileNames.get(String(song.qualityProfileId)) ?? `#${song.qualityProfileId}`,
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
  ];
}

/** One tab's table. The tab owns the paging state; this renders it. */
function WantedList({
  query,
  paging,
  onPaging,
  emptyMessage,
  showCovers,
}: {
  query: UseQueryResult<SongPage, Error>;
  paging: Paging;
  onPaging: (paging: Paging) => void;
  emptyMessage: string;
  showCovers: boolean;
}) {
  const profiles = useQualityProfiles();
  const profileNames = new Map((profiles.data ?? []).map((profile) => [String(profile.id), profile.name]));

  return (
    <PagedTable
      columns={wantedColumns(profileNames, showCovers)}
      rows={query.data?.records ?? []}
      totalRecords={Number(query.data?.totalRecords ?? 0)}
      paging={paging}
      onPaging={onPaging}
      isLoading={query.isPending}
      error={query.error}
      emptyMessage={emptyMessage}
      rowKey={(song) => song.id}
      compact={!showCovers}
    />
  );
}

/**
 * Each tab mounts on its own, so only the visible list is ever requested, and each starts the other
 * one back at page one rather than carrying a sort key the other endpoint may not know.
 */
function MissingTab({ showCovers }: { showCovers: boolean }) {
  const [paging, setPaging] = useState<Paging>(() => firstPage());

  return (
    <WantedList
      query={useWantedMissing(paging)}
      paging={paging}
      onPaging={setPaging}
      emptyMessage="Nothing is missing."
      showCovers={showCovers}
    />
  );
}

function CutoffTab({ showCovers }: { showCovers: boolean }) {
  const [paging, setPaging] = useState<Paging>(() => firstPage());

  return (
    <WantedList
      query={useWantedCutoff(paging)}
      paging={paging}
      onPaging={setPaging}
      emptyMessage="Every file meets its cutoff."
      showCovers={showCovers}
    />
  );
}

/** Wanted: the monitored songs with no file, and the ones whose file is below their cutoff. */
export function WantedPage() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const active: WantedTab = tab === 'cutoff' ? 'cutoff' : 'missing';
  const [showCovers, setShowCovers] = useShowCovers();

  return (
    <Stack gap="lg">
      <Group justify="space-between" align="center">
        <Title order={2}>Wanted</Title>
        <Switch
          label="Show cover art"
          checked={showCovers}
          onChange={(event) => setShowCovers(event.currentTarget.checked)}
        />
      </Group>

      <Tabs
        value={active}
        onChange={(value) => {
          void navigate(`/wanted/${value ?? 'missing'}`);
        }}
      >
        <Tabs.List>
          <Tabs.Tab value="missing">Missing</Tabs.Tab>
          <Tabs.Tab value="cutoff">Cutoff Unmet</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="missing" pt="md">
          <MissingTab showCovers={showCovers} />
        </Tabs.Panel>

        <Tabs.Panel value="cutoff" pt="md">
          <CutoffTab showCovers={showCovers} />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
