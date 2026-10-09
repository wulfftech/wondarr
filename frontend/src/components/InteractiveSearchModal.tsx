import { ActionIcon, Badge, Button, Group, Modal, Stack, Table, Text, Tooltip } from '@mantine/core';
import type { MantineColor } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { Check, Search, UserSearch, X } from 'lucide-react';
import { Fragment, useState } from 'react';
import { ApiError } from '../api/errors';
import { readEnum } from '../api/profiles';
import {
  useGrabRelease,
  useInteractiveSearch,
  useSongSearchCommand,
  type InteractiveSearchResource,
  type ReleaseResource,
  type SearchOutcomeName,
  type ScoreBreakdown,
} from '../api/queue';
import { EmptyState, ErrorState, LoadingState } from './DataState';
import { formatDuration } from './SongCells';

/**
 * The interactive search (ARCHITECTURE §5.6): every candidate one song's search saw, with the score
 * behind it and the rules it failed, so the user can grab one by hand — including one the engine
 * rejected, which is why a rejected row asks before it grabs.
 */

/** Where a candidate's score puts it, as the badge's colour. */
const GOOD_ENOUGH_SCORE = 850;

/** How far a candidate's length may differ from the song's before it is called out. */
const DURATION_TOLERANCE_MS = 3000;

export interface InteractiveSearchModalProps {
  /** The song to search for. */
  songId: number | null;
  /** The song's title, for the dialog's heading. */
  songTitle: string;
  /** The song's artist, for the dialog's heading. */
  artistCredit: string;
  /** Whether the dialog is open; the search only runs while it is. */
  opened: boolean;
  /** Closes the dialog. */
  onClose: () => void;
  /**
   * The song's own length in milliseconds, when it is known. The list highlights a candidate that
   * differs from it by more than {@link DURATION_TOLERANCE_MS}; without it nothing is highlighted.
   */
  songDurationMs?: number | null;
}

/** How each search outcome reads as a sentence. */
const OUTCOME_TEXT: Partial<Record<SearchOutcomeName, string>> = {
  grabbed: 'Grabbed a candidate',
  noResults: 'No source returned anything',
  sourceUnavailable: 'A source was unavailable',
  failed: 'The search failed',
  cancelled: 'The search was cancelled',
};

/** `12.3 MB`, or an em dash when the source reported no size. */
function formatMegabytes(bytes: number | string | null): string {
  const value = typeof bytes === 'string' ? Number(bytes) : bytes;

  if (value === null || !Number.isFinite(value) || value < 0) {
    return '—';
  }

  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

/** The peer's advertised upload speed, in the unit a person reads it in. */
function formatSpeed(bytesPerSecond: number | string | null): string {
  const value = typeof bytesPerSecond === 'string' ? Number(bytesPerSecond) : bytesPerSecond;

  if (value === null || !Number.isFinite(value) || value <= 0) {
    return '—';
  }

  const kilobytes = value / 1024;

  return kilobytes >= 1024 ? `${(kilobytes / 1024).toFixed(1)} MB/s` : `${Math.round(kilobytes)} KB/s`;
}

/** The one line above the table: how many candidates came back and how the run ended. */
function summaryLine(result: InteractiveSearchResource): string {
  const count = result.releases.length;
  const outcome = readEnum<SearchOutcomeName>(result.outcome);
  const message = result.message === null || result.message === '' ? null : result.message;
  const tail = message === null ? '.' : `: ${message}`;

  if (outcome === 'noAcceptableCandidate') {
    return `${count} ${count === 1 ? 'candidate' : 'candidates'}, none acceptable${tail}`;
  }

  return `${OUTCOME_TEXT[outcome] ?? 'The search finished'}${tail}`;
}

/** The badge a candidate's score wears: green when the engine would grab it on its own. */
function ScoreBadge({ release }: { release: ReleaseResource }) {
  const score = Number(release.score);
  const colour: MantineColor = score >= GOOD_ENOUGH_SCORE ? 'green' : release.accepted ? 'yellow' : 'gray';

  return (
    <Badge color={colour} variant="light" aria-label={`Score ${score}`}>
      {score}
    </Badge>
  );
}

/** The peer's upload side: a free slot, how many are queued ahead and the speed. */
function PeerCell({ release }: { release: ReleaseResource }) {
  return (
    <Group gap={8} wrap="nowrap">
      {release.freeUploadSlot === null ? (
        <Text size="sm" c="dimmed">
          —
        </Text>
      ) : (
        <Tooltip label={release.freeUploadSlot ? 'Free upload slot' : 'No free upload slot'}>
          <Text
            size="sm"
            c={release.freeUploadSlot ? 'green' : 'dimmed'}
            aria-label={release.freeUploadSlot ? 'Free slot' : 'No free slot'}
          >
            {release.freeUploadSlot ? <Check size={16} /> : <X size={16} />}
          </Text>
        </Tooltip>
      )}

      <Stack gap={0}>
        <Text size="xs">queue {release.queueLength === null ? '—' : release.queueLength}</Text>
        <Text size="xs" c="dimmed">
          {formatSpeed(release.uploadSpeed)}
        </Text>
      </Stack>
    </Group>
  );
}

/** The candidate's own length, called out when it is not the song's. */
function LengthCell({ release, songDurationMs }: { release: ReleaseResource; songDurationMs: number | null }) {
  const duration = release.durationMs === null ? null : Number(release.durationMs);
  const differs =
    duration !== null && songDurationMs !== null && Math.abs(duration - songDurationMs) > DURATION_TOLERANCE_MS;

  return (
    <Text size="sm" c={differs ? 'red' : undefined} fw={differs ? 600 : undefined}>
      {formatDuration(release.durationMs)}
    </Text>
  );
}

/** Every part of the score, so the total can be read back (MATCHING_ENGINE.md §6.3). */
function Breakdown({ breakdown }: { breakdown: ScoreBreakdown }) {
  const parts: [string, number][] = [
    ['Title', Number(breakdown.title)],
    ['Artist', Number(breakdown.artist)],
    ['Duration', Number(breakdown.duration)],
    ['Identity', Number(breakdown.identity)],
    ['Quality', Number(breakdown.quality)],
    ['Availability', Number(breakdown.availability)],
    ['Source', Number(breakdown.sourcePreference)],
    ...breakdown.adjustments.map((adjustment): [string, number] => [adjustment.name, Number(adjustment.points)]),
    ['Total', Number(breakdown.total)],
  ];

  return (
    <Stack gap={4}>
      <Text size="xs" fw={600}>
        Score breakdown
      </Text>

      <Group gap="md" wrap="wrap">
        {parts.map(([label, points]) => (
          <Text key={label} size="xs">
            {`${label}: ${points}`}
          </Text>
        ))}
      </Group>

      {breakdown.cappedForUnknownDuration && (
        <Text size="xs" c="dimmed">
          Capped: unknown duration.
        </Text>
      )}
    </Stack>
  );
}

/** What one row says about why it was rejected. */
function RejectionCell({ release }: { release: ReleaseResource }) {
  if (release.rejections.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        —
      </Text>
    );
  }

  return (
    <Group gap={4} wrap="wrap">
      {release.rejections.map((rejection) => (
        <Tooltip key={rejection.reason} label={rejection.message} multiline w={320}>
          <Badge color="red" variant="light" size="sm">
            {rejection.reason}
          </Badge>
        </Tooltip>
      ))}
    </Group>
  );
}

export interface SongSearchButtonsProps {
  /** The song the two buttons search for. */
  songId: number;
  /** The song's title, for the button labels and the notification. */
  title: string;
  /** The song's artist, for the dialog's heading. */
  artistCredit: string;
  /** The song's own length in milliseconds, for the length highlight. */
  durationMs?: number | null;
}

/**
 * The two search buttons a song row carries: the automatic search (the `SongSearch` command the
 * scheduler runs) and the interactive one, which opens {@link InteractiveSearchModal}. Both live
 * here so the Wanted and Library tables offer exactly the same pair.
 */
export function SongSearchButtons({ songId, title, artistCredit, durationMs = null }: SongSearchButtonsProps) {
  const [opened, setOpened] = useState(false);
  const search = useSongSearchCommand();

  return (
    <Group gap={4} wrap="nowrap">
      <Tooltip label="Search">
        <ActionIcon
          variant="default"
          aria-label={`Search for ${title}`}
          loading={search.isPending}
          onClick={() =>
            search.mutate(songId, {
              onSuccess: () => notifications.show({ message: `Searching for ${title}`, color: 'blue' }),
              onError: (error: Error) => notifications.show({ message: error.message, color: 'red' }),
            })
          }
        >
          <Search size={16} />
        </ActionIcon>
      </Tooltip>

      <Tooltip label="Interactive search">
        <ActionIcon variant="default" aria-label={`Interactive search for ${title}`} onClick={() => setOpened(true)}>
          <UserSearch size={16} />
        </ActionIcon>
      </Tooltip>

      <InteractiveSearchModal
        songId={songId}
        songTitle={title}
        artistCredit={artistCredit}
        songDurationMs={durationMs}
        opened={opened}
        onClose={() => setOpened(false)}
      />
    </Group>
  );
}

export interface InteractiveSearchPanelProps {
  /** The song to search for. */
  songId: number | null;
  /** Whether the search runs; each time this turns on, a fresh search starts. */
  active: boolean;
  /** The song's own length in milliseconds, for the length highlight. */
  songDurationMs?: number | null;
  /** Called after a candidate was grabbed. */
  onGrabbed: () => void;
}

/**
 * The interactive search's result table, its row actions and its confirmation, without a frame: the
 * dialog wraps it for the song tables, and the song page shows it inline under the header.
 */
export function InteractiveSearchPanel({
  songId,
  active,
  songDurationMs = null,
  onGrabbed,
}: InteractiveSearchPanelProps) {
  const query = useInteractiveSearch(songId, active);
  const grab = useGrabRelease();
  const [expanded, setExpanded] = useState<number | null>(null);
  const [confirming, setConfirming] = useState<ReleaseResource | null>(null);

  const grabRelease = (release: ReleaseResource) => {
    grab.mutate(Number(release.candidateId), {
      onSuccess: () => {
        notifications.show({ message: `Grabbed ${release.displayName}`, color: 'green' });
        setConfirming(null);
        onGrabbed();
      },
      onError: (error: Error) => {
        notifications.show({
          message: error instanceof ApiError && error.status === 409 ? 'Already downloading' : error.message,
          color: 'red',
        });
        setConfirming(null);
      },
    });
  };

  const onGrabClick = (release: ReleaseResource) => {
    if (release.accepted) {
      grabRelease(release);

      return;
    }

    setConfirming(release);
  };

  const releases = query.data?.releases ?? [];

  return (
    <>
      <Stack gap="md">
        {query.isPending && (
          <LoadingState label="Searching Soulseek… this can take up to a minute when the search budget is busy" />
        )}

        {query.error !== null && <ErrorState message={query.error.message} />}

        {query.data !== undefined && (
          <Text size="sm" c="dimmed">
            {summaryLine(query.data)}
          </Text>
        )}

        {query.data !== undefined && releases.length === 0 && (
          <EmptyState message="No candidate came back for this song." />
        )}

        {releases.length > 0 && (
          <Table.ScrollContainer minWidth={900}>
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w={80}>Score</Table.Th>
                  <Table.Th>File</Table.Th>
                  <Table.Th w={150}>Peer</Table.Th>
                  <Table.Th w={110}>Quality</Table.Th>
                  <Table.Th w={100}>Size</Table.Th>
                  <Table.Th w={80}>Length</Table.Th>
                  <Table.Th>Rejections</Table.Th>
                  <Table.Th w={90} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {releases.map((release) => {
                  const id = Number(release.candidateId);
                  const open = expanded === id;

                  return (
                    <Fragment key={id}>
                      <Table.Tr
                        onClick={() => setExpanded(open ? null : id)}
                        aria-expanded={open}
                        style={{ cursor: 'pointer' }}
                      >
                        <Table.Td>
                          <ScoreBadge release={release} />
                        </Table.Td>
                        <Table.Td>
                          <Tooltip label={release.remotePath} multiline w={420}>
                            <Text size="sm">{release.displayName}</Text>
                          </Tooltip>
                        </Table.Td>
                        <Table.Td>
                          <PeerCell release={release} />
                        </Table.Td>
                        <Table.Td>{release.qualityName ?? `#${release.qualityId}`}</Table.Td>
                        <Table.Td>{formatMegabytes(release.sizeBytes)}</Table.Td>
                        <Table.Td>
                          <LengthCell release={release} songDurationMs={songDurationMs} />
                        </Table.Td>
                        <Table.Td>
                          <RejectionCell release={release} />
                        </Table.Td>
                        <Table.Td>
                          <Button
                            size="compact-sm"
                            variant={release.accepted ? 'filled' : 'light'}
                            loading={grab.isPending && grab.variables === id}
                            aria-label={`Grab ${release.displayName}`}
                            onClick={(event) => {
                              event.stopPropagation();
                              onGrabClick(release);
                            }}
                          >
                            Grab
                          </Button>
                        </Table.Td>
                      </Table.Tr>

                      {open && (
                        <Table.Tr>
                          <Table.Td colSpan={8}>
                            <Breakdown breakdown={release.scoreBreakdown} />
                          </Table.Td>
                        </Table.Tr>
                      )}
                    </Fragment>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Stack>

      <Modal opened={confirming !== null} onClose={() => setConfirming(null)} title="Grab a rejected candidate">
        <Stack gap="md">
          <Text size="sm">
            {confirming === null
              ? ''
              : `This candidate was rejected: ${confirming.rejections
                  .map((rejection) => rejection.reason)
                  .join(', ')}. Grab anyway?`}
          </Text>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirming(null)}>
              Cancel
            </Button>
            <Button
              loading={grab.isPending}
              onClick={() => {
                if (confirming !== null) {
                  grabRelease(confirming);
                }
              }}
            >
              Grab anyway
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** The interactive search in a dialog, as the song tables open it. */
export function InteractiveSearchModal({
  songId,
  songTitle,
  artistCredit,
  opened,
  onClose,
  songDurationMs = null,
}: InteractiveSearchModalProps) {
  return (
    <Modal opened={opened} onClose={onClose} size="xl" title={`Interactive search — ${songTitle} · ${artistCredit}`}>
      <InteractiveSearchPanel songId={songId} active={opened} songDurationMs={songDurationMs} onGrabbed={onClose} />
    </Modal>
  );
}
