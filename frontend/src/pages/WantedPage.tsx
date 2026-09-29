import { Stack, Tabs, Title } from '@mantine/core';
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

/** The columns both tabs share. `profileNames` turns a profile id into the name the user knows. */
function wantedColumns(profileNames: Map<string, string>): PagedColumn<SongResource>[] {
  return [
    {
      label: 'Cover',
      sortKey: null,
      width: 64,
      render: (song) => <CoverThumb url={song.albumContext?.coverUrl ?? null} />,
    },
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
}: {
  query: UseQueryResult<SongPage, Error>;
  paging: Paging;
  onPaging: (paging: Paging) => void;
  emptyMessage: string;
}) {
  const profiles = useQualityProfiles();
  const profileNames = new Map((profiles.data ?? []).map((profile) => [String(profile.id), profile.name]));

  return (
    <PagedTable
      columns={wantedColumns(profileNames)}
      rows={query.data?.records ?? []}
      totalRecords={Number(query.data?.totalRecords ?? 0)}
      paging={paging}
      onPaging={onPaging}
      isLoading={query.isPending}
      error={query.error}
      emptyMessage={emptyMessage}
      rowKey={(song) => song.id}
    />
  );
}

/**
 * Each tab mounts on its own, so only the visible list is ever requested, and each starts the other
 * one back at page one rather than carrying a sort key the other endpoint may not know.
 */
function MissingTab() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());

  return (
    <WantedList
      query={useWantedMissing(paging)}
      paging={paging}
      onPaging={setPaging}
      emptyMessage="Nothing is missing."
    />
  );
}

function CutoffTab() {
  const [paging, setPaging] = useState<Paging>(() => firstPage());

  return (
    <WantedList
      query={useWantedCutoff(paging)}
      paging={paging}
      onPaging={setPaging}
      emptyMessage="Every file meets its cutoff."
    />
  );
}

/** Wanted: the monitored songs with no file, and the ones whose file is below their cutoff. */
export function WantedPage() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const active: WantedTab = tab === 'cutoff' ? 'cutoff' : 'missing';

  return (
    <Stack gap="lg">
      <Title order={2}>Wanted</Title>

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
          <MissingTab />
        </Tabs.Panel>

        <Tabs.Panel value="cutoff" pt="md">
          <CutoffTab />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
