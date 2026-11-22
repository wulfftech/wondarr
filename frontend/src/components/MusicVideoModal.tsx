import {
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Card,
  Checkbox,
  Group,
  Image,
  Loader,
  Modal,
  Stack,
  Text,
} from '@mantine/core';
import { CircleAlert, ExternalLink, Video } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  hasPendingReplacement,
  isVideoBusy,
  LOW_QUALITY_HEIGHT,
  nothingBetterOnYouTube,
  useDeleteMusicVideo,
  useDismissReplaceFailure,
  useGrabMusicVideo,
  useSearchMusicVideo,
  useSongMusicVideo,
  videoThumbnailUrl,
  youTubeUrl,
  type MusicVideoCandidateResource,
  type MusicVideoResource,
} from '../api/musicVideos';
import { Fact, FactGrid } from '../pages/song/Facts';
import { EmptyState, ErrorState, LoadingState } from './DataState';
import { formatDate, formatDuration } from './SongCells';
import { formatBytes } from './song/format';

/** A song's music video: the candidates to pick from, and the view of the one it has. */

const KIND_LABELS: Record<MusicVideoCandidateResource['kind'], string> = {
  officialVideo: 'Official video',
  artTrack: 'Art track',
  userUpload: 'Upload',
  audioOnly: 'Audio only',
  unknown: 'Video',
};

const FOUND_BY_LABELS: Record<MusicVideoResource['foundBy'], string> = {
  musicBrainz: 'MusicBrainz',
  youTubeSearch: 'YouTube search',
  manual: 'Picked by hand',
  existing: 'Found in your folder',
};

/** How many candidates show before "Show more". */
const FIRST_CANDIDATES = 8;

/** The difference between a video's length and the song's, signed: `+0:12`, `-1:05`. Empty when either is unknown. */
function lengthDifference(candidateMs: number | null, songMs: number | null | undefined): string {
  if (candidateMs === null || songMs === null || songMs === undefined) {
    return '';
  }

  const difference = Number(candidateMs) - Number(songMs);

  return `${difference < 0 ? '-' : '+'}${formatDuration(Math.abs(difference))}`;
}

function CandidateCard({
  candidate,
  best,
  songDurationMs,
  busy,
  onDownload,
}: {
  candidate: MusicVideoCandidateResource;
  /** Whether this is the top of the list, which earns a badge when it is a good guess. */
  best: boolean;
  songDurationMs?: number | null;
  busy: boolean;
  onDownload: () => void;
}) {
  const difference = lengthDifference(
    candidate.durationMs === null ? null : Number(candidate.durationMs),
    songDurationMs,
  );
  // The top of the list is a good guess when it is titled as the song (a strict reading of its title); Confident says
  // more. A top result that is another song of the artist earns no badge.
  const badge = !best
    ? null
    : candidate.confident
      ? 'Confident match'
      : candidate.titledAsSong === true
        ? 'Best match'
        : null;

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
            {difference === '' ? '' : ` (${difference})`}
          </Text>

          <Group gap={4}>
            {badge !== null && (
              <Badge size="sm" color="green" variant="filled">
                {badge}
              </Badge>
            )}
            <Badge size="sm" variant="light" color={candidate.kind === 'audioOnly' ? 'yellow' : undefined}>
              {KIND_LABELS[candidate.kind]}
            </Badge>
            {candidate.confident && badge === null && (
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

/** The video a row chose: its thumbnail, its title when known, and a link to it on YouTube. */
function ChosenVideo({ video, children }: { video: MusicVideoResource; children?: ReactNode }) {
  const videoId = video.youTubeVideoId;

  return (
    <Group wrap="nowrap" align="flex-start">
      {videoId !== null && <Image src={videoThumbnailUrl(videoId)} alt="" w={160} h={90} radius="sm" fit="cover" />}

      <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
        {video.videoTitle !== null && (
          <Text fw={600} size="sm" style={{ overflowWrap: 'anywhere' }}>
            {video.videoTitle}
          </Text>
        )}

        {children}

        {videoId !== null && (
          <Anchor href={youTubeUrl(videoId)} target="_blank" rel="noreferrer noopener" size="sm">
            Open on YouTube <ExternalLink size={12} aria-hidden style={{ verticalAlign: 'baseline' }} />
          </Anchor>
        )}
      </Stack>
    </Group>
  );
}

/** A video waiting for the shared download slot, or being downloaded. */
function BusyView({ video }: { video: MusicVideoResource }) {
  return (
    <Stack gap="sm">
      <ChosenVideo video={video}>
        <Group gap="xs">
          <Loader size="xs" />
          <Text size="sm">{video.status === 'queued' ? 'Waiting for another download to finish' : 'Downloading…'}</Text>
        </Group>
      </ChosenVideo>
    </Stack>
  );
}

/** A download that failed: why, which video it tried, and what to do next. Nothing starts until a button is clicked. */
function FailedView({
  video,
  onSearchAgain,
  onPickAnother,
  onRemoved,
}: {
  video: MusicVideoResource;
  onSearchAgain: () => void;
  onPickAnother: () => void;
  onRemoved: () => void;
}) {
  // The error shows inline below, so the hook does not toast it; onRemoved runs from the hook, past a remount.
  const remove = useDeleteMusicVideo({ silent: true, onRemoved });

  return (
    <Stack gap="md">
      <Alert color="red" title="The download failed" icon={<CircleAlert size={16} />}>
        {video.failureReason ?? 'No reason was given.'}
      </Alert>

      {video.youTubeVideoId !== null && (
        <Stack gap={4}>
          <Text size="xs" c="dimmed">
            The video it tried
          </Text>
          <ChosenVideo video={video} />
        </Stack>
      )}

      {remove.error !== null && (
        <Alert color="red" icon={<CircleAlert size={16} />}>
          {remove.error.message}
        </Alert>
      )}

      <Group gap="xs">
        <Button size="xs" leftSection={<Video size={14} />} onClick={onSearchAgain}>
          Search again
        </Button>
        <Button variant="default" size="xs" onClick={onPickAnother}>
          Pick another…
        </Button>
        <Button
          color="red"
          variant="light"
          size="xs"
          loading={remove.isPending}
          // The file never landed, so there is nothing to lose and nothing to blocklist.
          onClick={() => remove.mutate({ songId: Number(video.songId), blocklist: false })}
        >
          Remove
        </Button>
      </Group>
    </Stack>
  );
}

/** What the video file is: its picture, where it sits, its codecs, its size, and who found it. */
function ManageView({
  video,
  onReplace,
  onRemoved,
}: {
  video: MusicVideoResource;
  onReplace: () => void;
  onRemoved: () => void;
}) {
  const remove = useDeleteMusicVideo({ silent: true, onRemoved });
  const grab = useGrabMusicVideo();
  const dismiss = useDismissReplaceFailure();
  const [confirming, setConfirming] = useState(false);
  const [blocklist, setBlocklist] = useState(true);

  if (isVideoBusy(video.status)) {
    return <BusyView video={video} />;
  }

  // A video found in the folder has no YouTube id: nothing to blocklist, and no YouTube page to link to.
  const found = video.foundBy === 'existing';
  const resolution = video.width !== null && video.height !== null ? `${video.width}×${video.height}` : '—';
  const codecs = [video.videoCodec, video.audioCodec].filter((codec) => codec !== null && codec !== '').join(' / ');
  const lowQuality = video.height !== null && Number(video.height) < LOW_QUALITY_HEIGHT;
  const replaceFailure = typeof video.replaceFailureReason === 'string' ? video.replaceFailureReason : null;
  const videoId = video.youTubeVideoId;
  const pending = video.pendingReplacement ?? null;
  const replacing = hasPendingReplacement(video);
  // The last check found nothing better than this copy, recently: asking again would download the same file.
  const nothingBetter = nothingBetterOnYouTube(video);

  // The toast (queued, or why it was refused) belongs to the hook, which outlives this view.
  const downloadAgain = () => {
    if (videoId === null) {
      return;
    }

    grab.mutate({
      songId: Number(video.songId),
      videoId,
      ...(video.videoTitle === null ? {} : { videoTitle: video.videoTitle }),
    });
  };

  return (
    <Stack gap="md">
      {replaceFailure !== null && (
        <Alert color="red" title={`Replacing failed: ${replaceFailure}`} icon={<CircleAlert size={16} />}>
          <Group gap="xs">
            <Button size="compact-sm" onClick={onReplace}>
              Try again
            </Button>
            <Button
              size="compact-sm"
              variant="default"
              loading={dismiss.isPending}
              onClick={() => dismiss.mutate(Number(video.songId))}
            >
              Dismiss
            </Button>
          </Group>
        </Alert>
      )}

      {pending !== null && (
        <Alert color="blue" icon={<Loader size={16} />}>
          <Stack gap={2}>
            <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
              {pending.videoTitle === null || pending.videoTitle === ''
                ? 'Replacing with another video'
                : `Replacing with «${pending.videoTitle}»`}
            </Text>
            {pending.status === 'queued' && <Text size="sm">Waiting for another download to finish</Text>}
          </Stack>
        </Alert>
      )}

      <ChosenVideo video={video}>
        <Group gap={6}>
          {video.durationMs !== null && (
            <Text size="xs" c="dimmed">
              {formatDuration(video.durationMs)}
            </Text>
          )}
          {lowQuality && (
            <Badge size="sm" color="yellow" variant="light">
              {`Low quality (${String(video.height)}p)`}
            </Badge>
          )}
        </Group>
      </ChosenVideo>

      <FactGrid>
        <Fact label="Path">{video.path ?? '—'}</Fact>
        <Fact label="Resolution">{resolution}</Fact>
        {codecs !== '' && <Fact label="Codecs">{codecs}</Fact>}
        <Fact label="Size">{formatBytes(video.sizeBytes)}</Fact>
        <Fact label="Found by">{FOUND_BY_LABELS[video.foundBy]}</Fact>
      </FactGrid>

      {confirming ? (
        <Stack gap="sm">
          <Text size="sm" fw={600}>
            {found ? 'Remove this video?' : "Remove and don't pick this video again?"}
          </Text>

          {found ? (
            <Text size="sm">
              The file goes to the recycle bin, with its .nfo and thumbnail. Wondarr won&apos;t pick it up again when it
              scans the folder.
            </Text>
          ) : (
            <Checkbox
              label="Don't pick this video again for this song"
              checked={blocklist}
              onChange={(event) => setBlocklist(event.currentTarget.checked)}
            />
          )}

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
              onClick={() => remove.mutate({ songId: Number(video.songId), blocklist: blocklist && !found })}
            >
              Remove
            </Button>
          </Group>
        </Stack>
      ) : (
        <Group gap="xs">
          {/* Replace…, said for a video that is worth replacing. */}
          <Button variant={lowQuality ? 'filled' : 'default'} size="xs" disabled={replacing} onClick={onReplace}>
            {lowQuality ? 'Find a better one' : 'Replace…'}
          </Button>
          {lowQuality && videoId !== null && !nothingBetter && (
            <Button variant="default" size="xs" disabled={replacing} loading={grab.isPending} onClick={downloadAgain}>
              Download again
            </Button>
          )}
          <Button color="red" variant="light" size="xs" disabled={replacing} onClick={() => setConfirming(true)}>
            Remove
          </Button>
        </Group>
      )}

      {!confirming && found && (
        <Text size="xs" c="dimmed">
          Your current file goes to the recycle bin once the new one is in place.
        </Text>
      )}

      {!confirming && lowQuality && videoId !== null && !nothingBetter && (
        <Text size="xs" c="dimmed">
          YouTube may have a better copy of this video now.
        </Text>
      )}

      {!confirming && lowQuality && videoId !== null && nothingBetter && (
        <Text size="xs" c="dimmed">
          {`YouTube's best copy is ${String(video.bestAvailableHeight)}p (checked ${formatDate(video.bestAvailableCheckedAt)}).`}
        </Text>
      )}
    </Stack>
  );
}

export interface MusicVideoPanelProps {
  /** The song whose video this is. */
  songId: number;
  /** The song's length in milliseconds, so the candidates can say how far off they are. */
  songDurationMs?: number | null;
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
 * inline. It shows the manage view while the song holds a video, a failed view when its download failed,
 * and the ranked candidates while it holds none or the person chose Replace…, Search again or Pick another…
 */
export function MusicVideoPanel({
  songId,
  songDurationMs = null,
  initialCandidates,
  initialPartial = false,
  searchOnOpen = false,
  onRemoved,
}: MusicVideoPanelProps) {
  const video = useSongMusicVideo(songId);
  // The search error shows inline in the panel, so the hook does not toast it too.
  const search = useSearchMusicVideo({ silent: true });
  const grab = useGrabMusicVideo();
  const [picking, setPicking] = useState(initialCandidates !== undefined || searchOnOpen);
  const [partial, setPartial] = useState(initialPartial);
  const [candidates, setCandidates] = useState<MusicVideoCandidateResource[] | null>(initialCandidates ?? null);
  const [shown, setShown] = useState(FIRST_CANDIDATES);
  const [grabbing, setGrabbing] = useState<string | null>(null);
  const started = useRef(false);

  /** Searches; with `autoDownload` false it only lists, so the person picks (a failed video's "Pick another…"). */
  const runSearch = (autoDownload = true) => {
    setPicking(true);
    setCandidates(null);
    setPartial(false);
    setShown(FIRST_CANDIDATES);
    // The toast for a queued download belongs to the hook; this only moves the panel on.
    search.mutate(
      { songId, autoDownload },
      {
        onSuccess: (result) => {
          if (result.queued) {
            setPicking(false);

            return;
          }

          setPartial(result.partial);
          setCandidates(result.candidates);
        },
      },
    );
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
      { songId, videoId: candidate.videoId, videoTitle: candidate.title },
      {
        onSuccess: () => {
          setPicking(false);
          setCandidates(null);
        },
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

  const held = video.data ?? null;

  if (!picking && held !== null && held.status === 'failed') {
    return (
      <FailedView
        video={held}
        onSearchAgain={() => runSearch()}
        onPickAnother={() => runSearch(false)}
        onRemoved={() => onRemoved?.()}
      />
    );
  }

  if (!picking && held !== null) {
    return <ManageView video={held} onReplace={() => runSearch(false)} onRemoved={() => onRemoved?.()} />;
  }

  if (!picking) {
    return (
      <Stack gap="sm" align="flex-start">
        <Text size="sm">No music video yet.</Text>
        <Button size="xs" leftSection={<Video size={14} />} onClick={() => runSearch()}>
          Find music video
        </Button>
      </Stack>
    );
  }

  const visible = candidates?.slice(0, shown) ?? [];
  const hidden = (candidates?.length ?? 0) - visible.length;

  return (
    <Stack gap="sm">
      {songDurationMs !== null && (
        <Text size="sm" c="dimmed">
          Song length {formatDuration(songDurationMs)}
        </Text>
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

      {candidates !== null &&
        candidates.length > 0 &&
        !candidates.some((candidate) => candidate.titledAsSong === true) && (
          <Alert color="yellow" icon={<CircleAlert size={16} />}>
            None of these is titled as this song. Check before you download.
          </Alert>
        )}

      {visible.map((candidate, index) => (
        <CandidateCard
          key={candidate.videoId}
          candidate={candidate}
          best={index === 0}
          songDurationMs={songDurationMs}
          busy={grabbing === candidate.videoId}
          onDownload={() => download(candidate)}
        />
      ))}

      {hidden > 0 && (
        <Button variant="light" size="xs" onClick={() => setShown((count) => count + FIRST_CANDIDATES)}>
          {`Show more (${String(hidden)})`}
        </Button>
      )}

      {/* Sticky, so it stays in reach however far down the list the person has scrolled. */}
      {held !== null && (
        <Box
          style={{
            position: 'sticky',
            bottom: 0,
            zIndex: 1,
            padding: 'var(--mantine-spacing-xs) 0',
            background: 'var(--mantine-color-body)',
          }}
        >
          <Button variant="subtle" size="xs" onClick={() => setPicking(false)}>
            Back to the current video
          </Button>
        </Box>
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
