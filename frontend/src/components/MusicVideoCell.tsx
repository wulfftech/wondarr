import { ActionIcon, Group, Loader, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { Clapperboard, Search, Video, VideoOff } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import {
  downloadingMessage,
  isVideoBusy,
  useSearchMusicVideo,
  type MusicVideoCandidateResource,
} from '../api/musicVideos';
import type { SongResource } from '../api/songs';
import type { PagedColumn } from './PagedTable';
import { MusicVideoModal } from './MusicVideoModal';

/** The video column the Library and Wanted tables share (ADR-0014). */

interface DialogState {
  initialCandidates?: MusicVideoCandidateResource[];
  initialPartial?: boolean;
}

/** A film icon with a small magnifier on it: "find this song's video". */
function FindIcon() {
  return (
    <span style={{ position: 'relative', display: 'inline-flex' }}>
      <Video size={16} />
      <Search size={9} strokeWidth={3} style={{ position: 'absolute', right: -3, bottom: -3 }} />
    </span>
  );
}

/**
 * One song's video icon. With no video it searches: a confident match is downloaded and a toast says so,
 * anything else opens the candidates to pick from. With a video, or one on its way, it opens the manage view.
 */
export function MusicVideoCell({ song }: { song: SongResource }) {
  const songId = Number(song.id);
  const search = useSearchMusicVideo();
  const [dialog, setDialog] = useState<DialogState | null>(null);
  const state = song.musicVideo ?? null;

  const find = () =>
    search.mutate(songId, {
      onSuccess: (result) => {
        if (result.queued) {
          notifications.show({
            message: downloadingMessage(result.candidates.find((candidate) => candidate.confident)?.title),
            color: 'blue',
          });

          return;
        }

        setDialog({ initialCandidates: result.candidates, initialPartial: result.partial });
      },
      onError: (error: Error) => notifications.show({ message: error.message, color: 'red' }),
    });

  let tooltip: string;
  let label: string;
  let icon: ReactNode;
  let color: string | undefined;
  let onClick: () => void;

  if (state === null) {
    tooltip = 'Find music video';
    label = `Find music video for ${song.title}`;
    icon = search.isPending ? <Loader size={14} /> : <FindIcon />;
    onClick = find;
  } else if (isVideoBusy(state.status)) {
    tooltip = state.status === 'queued' ? 'Waiting to download' : 'Downloading';
    label = `Music video for ${song.title} is downloading`;
    icon = <Loader size={14} />;
    onClick = () => setDialog({});
  } else if (state.status === 'present') {
    tooltip = state.height === null ? 'Music video' : `${String(state.height)}p`;
    label = `Music video for ${song.title}`;
    icon = <Video size={16} fill="currentColor" />;
    onClick = () => setDialog({});
  } else {
    // The song list carries the status but not the reason; the candidate dialog shows it.
    tooltip = state.failureReason ?? 'The download failed. Pick another video.';
    label = `Music video for ${song.title} failed`;
    icon = <VideoOff size={16} />;
    color = 'red';
    // The modal opens on the failed view; nothing is searched or downloaded until the person asks.
    onClick = () => setDialog({});
  }

  return (
    <>
      <Tooltip label={tooltip}>
        <ActionIcon
          variant="default"
          color={color}
          c={color}
          aria-label={label}
          disabled={search.isPending}
          onClick={onClick}
        >
          {icon}
        </ActionIcon>
      </Tooltip>

      {dialog !== null && (
        <MusicVideoModal
          songId={songId}
          songTitle={song.title}
          songDurationMs={song.durationMs === null ? null : Number(song.durationMs)}
          initialCandidates={dialog.initialCandidates}
          initialPartial={dialog.initialPartial}
          opened
          onClose={() => setDialog(null)}
        />
      )}
    </>
  );
}

/** The column, for a table whose page says the feature is on. It sits after the Search column. */
// Built next to the cell it renders, and used by the two tables rather than as a component.
// eslint-disable-next-line react-refresh/only-export-components
export function musicVideoColumn(): PagedColumn<SongResource> {
  return {
    label: 'Music video',
    sortKey: null,
    width: 90,
    // A visible "Video" beside the icon, as the Search column says "Search"; the full name stays for screen readers.
    header: (
      <Tooltip label="Music video">
        <Group gap={4} wrap="nowrap" aria-label="Music video">
          <Clapperboard size={14} aria-hidden />
          <span>Video</span>
        </Group>
      </Tooltip>
    ),
    render: (song) => <MusicVideoCell song={song} />,
  };
}
