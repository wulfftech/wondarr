import { Alert, Anchor, Badge, Button, Card, Checkbox, Group, Image, Loader, Modal, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { CircleAlert, ExternalLink, Video } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import {
  isVideoBusy,
  useDeleteMusicVideo,
  useGrabMusicVideo,
  useSearchMusicVideo,
  useSongMusicVideo,
  type MusicVideoCandidateResource,
  type MusicVideoResource,
} from '../api/musicVideos';
import { Fact, FactGrid } from '../pages/song/Facts';
import { EmptyState, ErrorState, LoadingState } from './DataState';
import { formatDuration } from './SongCells';
import { formatBytes } from './song/format';

/** A song's music video: the candidates to pick from, and the view of the one it has. */

const KIND_LABELS: Record<MusicVideoCandidateResource['kind'], string> = {
  officialVideo: 'Official video',
  artTrack: 'Art track',
  userUpload: 'Upload',
  unknown: 'Video',
};

const FOUND_BY_LABELS: Record<MusicVideoResource['foundBy'], string> = {
  musicBrainz: 'MusicBrainz',
  youTubeSearch: 'YouTube search',
  manual: 'Picked by hand',
};

/** The address of a video on YouTube, for the "Open on YouTube" link. */
function youTubeUrl(videoId: string): string {
  return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`;
}

function CandidateCard({
  candidate,
  busy,
  onDownload,
}: {
  candidate: MusicVideoCandidateResource;
  busy: boolean;
  onDownload: () => void;
}) {
  return (
    <Card withBorder padding="sm">
      <Group wrap="nowrap" align="flex-start">
        <Image src={candidate.thumbnailUrl} alt="" w={120} h={68} radius="sm" fit="cover" />

        <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
          <Text fw={600} size="sm" style={{ overflowWrap: 'anywhere' }}>
            {candidate.title}
          </Text>

          <Text size="xs" c="dimmed">
            {candidate.artists.length === 0 ? 'No artist credited' : candidate.artists.join(', ')} ·{' '}
            {formatDuration(candidate.durationMs)}
          </Text>

          <Group gap={4}>
            <Badge size="sm" variant="light">
              {KIND_LABELS[candidate.kind]}
            </Badge>
            {candidate.confident && (
              <Badge size="sm" color="green" variant="light">
                Confident
              </Badge>
            )}
            {candidate.foundBy === 'musicBrainz' && (
              <Badge size="sm" color="grape" variant="light">
                From MusicBrainz
              </Badge>
            )}
          </Group>

          <Group gap="sm">
            <Button size="compact-sm" loading={busy} aria-label={`Download ${candidate.title}`} onClick={onDownload}>
              Download
            </Button>
            <Anchor
              href={youTubeUrl(candidate.videoId)}
              target="_blank"
              rel="noreferrer noopener"
              size="sm"
              aria-label={`Open ${candidate.title} on YouTube`}
            >
              Open on YouTube <ExternalLink size={12} aria-hidden style={{ verticalAlign: 'baseline' }} />
            </Anchor>
          </Group>
        </Stack>
      </Group>
    </Card>
  );
}

/** What the video file is: where it sits, its picture and codecs, its size, and who found it. */
function ManageView({
  video,
  onReplace,
  onRemoved,
}: {
  video: MusicVideoResource;
  onReplace: () => void;
  onRemoved: () => void;
}) {
  const remove = useDeleteMusicVideo();
  const [confirming, setConfirming] = useState(false);
  const [blocklist, setBlocklist] = useState(true);

  if (isVideoBusy(video.status)) {
    return (
      <Group gap="sm">
        <Loader size="sm" />
        <Text size="sm">{video.status === 'queued' ? 'Waiting to download…' : 'Downloading the music video…'}</Text>
      </Group>
    );
  }

  const resolution = video.width !== null && video.height !== null ? `${video.width}×${video.height}` : '—';
  const codecs = [video.videoCodec, video.audioCodec].filter((codec) => codec !== null && codec !== '').join(' / ');

  return (
    <Stack gap="md">
      <FactGrid>
        <Fact label="Path">{video.path ?? '—'}</Fact>
        <Fact label="Resolution">{resolution}</Fact>
        <Fact label="Codecs">{codecs === '' ? '—' : codecs}</Fact>
        <Fact label="Size">{formatBytes(video.sizeBytes)}</Fact>
        <Fact label="Found by">{FOUND_BY_LABELS[video.foundBy]}</Fact>
      </FactGrid>

      {confirming ? (
        <Stack gap="sm">
          <Text size="sm" fw={600}>
            Remove and don&apos;t pick this video again?
          </Text>

          <Checkbox
            label="Don't pick this video again for this song"
            checked={blocklist}
            onChange={(event) => setBlocklist(event.currentTarget.checked)}
          />

          {remove.error !== null && (
            <Alert color="red" icon={<CircleAlert size={16} />}>
              {remove.error.message}
            </Alert>
          )}

          <Group gap="xs">
            <Button variant="default" size="xs" onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button
              color="red"
              size="xs"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(
                  { songId: Number(video.songId), blocklist },
                  {
                    onSuccess: () => {
                      notifications.show({ message: 'Removed the music video', color: 'green' });
                      onRemoved();
                    },
                  },
                )
              }
            >
              Remove
            </Button>
          </Group>
        </Stack>
      ) : (
        <Group gap="xs">
          <Button variant="default" size="xs" onClick={onReplace}>
            Replace…
          </Button>
          <Button color="red" variant="light" size="xs" onClick={() => setConfirming(true)}>
            Remove
          </Button>
        </Group>
      )}
    </Stack>
  );
}

export interface MusicVideoPanelProps {
  /** The song whose video this is. */
  songId: number;
  /** Candidates a search already found, shown at once. */
  initialCandidates?: MusicVideoCandidateResource[];
  /** Whether the search that found `initialCandidates` had no MusicBrainz answer. */
  initialPartial?: boolean;
  /** Run the search as soon as the panel opens. */
  searchOnOpen?: boolean;
  /** Called when the panel has nothing left to show (a video was removed). */
  onRemoved?: () => void;
}

/**
 * The video's view, without a frame: the dialog wraps it for the song tables, and the song page shows it
 * inline. It shows the manage view while the song holds a video, and the ranked candidates while it holds
 * none, its download failed, or the person chose Replace…
 */
export function MusicVideoPanel({
  songId,
  initialCandidates,
  initialPartial = false,
  searchOnOpen = false,
  onRemoved,
}: MusicVideoPanelProps) {
  const video = useSongMusicVideo(songId);
  const search = useSearchMusicVideo();
  const grab = useGrabMusicVideo();
  const [picking, setPicking] = useState(initialCandidates !== undefined || searchOnOpen);
  const [partial, setPartial] = useState(initialPartial);
  const [candidates, setCandidates] = useState<MusicVideoCandidateResource[] | null>(initialCandidates ?? null);
  const [grabbing, setGrabbing] = useState<string | null>(null);
  const started = useRef(false);

  const runSearch = () => {
    setPicking(true);
    setCandidates(null);
    setPartial(false);
    search.mutate(songId, {
      onSuccess: (result) => {
        if (result.queued) {
          notifications.show({ message: 'Downloading music video', color: 'blue' });
          setPicking(false);

          return;
        }

        setPartial(result.partial);
        setCandidates(result.candidates);
      },
    });
  };

  useEffect(() => {
    if (searchOnOpen && !started.current) {
      started.current = true;
      runSearch();
    }
    // Once, on open; the search itself reads only the song id.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const download = (candidate: MusicVideoCandidateResource) => {
    setGrabbing(candidate.videoId);
    grab.mutate(
      { songId, videoId: candidate.videoId },
      {
        onSuccess: () => {
          notifications.show({ message: 'Downloading music video', color: 'blue' });
          setPicking(false);
          setCandidates(null);
        },
        onError: (error: Error) => notifications.show({ message: error.message, color: 'red' }),
        onSettled: () => setGrabbing(null),
      },
    );
  };

  if (video.isPending) {
    return <LoadingState />;
  }

  if (video.error !== null) {
    return <ErrorState message={video.error.message} />;
  }

  const held = video.data;

  if (!picking && held !== null && held !== undefined && held.status !== 'failed') {
    return <ManageView video={held} onReplace={runSearch} onRemoved={() => onRemoved?.()} />;
  }

  if (!picking) {
    return (
      <Stack gap="sm" align="flex-start">
        {held?.status === 'failed' && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            The last download failed: {held.failureReason ?? 'no reason was given'}
          </Alert>
        )}
        {held == null && <Text size="sm">No music video yet.</Text>}
        <Button size="xs" leftSection={<Video size={14} />} onClick={runSearch}>
          {held == null ? 'Find music video' : 'Find another video'}
        </Button>
      </Stack>
    );
  }

  return (
    <Stack gap="sm">
      {held?.status === 'failed' && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          The last download failed: {held.failureReason ?? 'no reason was given'}
        </Alert>
      )}

      {search.isPending && <LoadingState label="Searching MusicBrainz and YouTube Music…" />}

      {search.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {search.error.message}
        </Alert>
      )}

      {partial && candidates !== null && (
        <Alert color="yellow" icon={<CircleAlert size={16} />}>
          MusicBrainz didn&apos;t answer, so these are YouTube results only.
        </Alert>
      )}

      {candidates !== null && candidates.length === 0 && (
        <EmptyState message="No music video candidate came back for this song." />
      )}

      {candidates?.map((candidate) => (
        <CandidateCard
          key={candidate.videoId}
          candidate={candidate}
          busy={grabbing === candidate.videoId}
          onDownload={() => download(candidate)}
        />
      ))}

      {held !== null && held !== undefined && held.status !== 'failed' && (
        <Group>
          <Button variant="subtle" size="xs" onClick={() => setPicking(false)}>
            Back to the current video
          </Button>
        </Group>
      )}
    </Stack>
  );
}

export interface MusicVideoModalProps extends Omit<MusicVideoPanelProps, 'onRemoved'> {
  /** The song's title, for the heading. */
  songTitle: string;
  opened: boolean;
  onClose: () => void;
}

/** The dialog the song tables open from the video column's icon. */
export function MusicVideoModal({ songTitle, opened, onClose, ...panel }: MusicVideoModalProps) {
  return (
    <Modal opened={opened} onClose={onClose} title={`Music video for ${songTitle}`} size="lg" centered>
      <MusicVideoPanel {...panel} onRemoved={onClose} />
    </Modal>
  );
}
